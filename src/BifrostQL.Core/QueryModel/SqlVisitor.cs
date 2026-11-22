using GraphQLParser.AST;
using GraphQLParser.Visitors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BifrostQL.Core.QueryModel
{
    public class SqlVisitor : ASTVisitor<ISqlContext>
    {
        private readonly GraphQLOperationDefinition? _executingOperation;

        /// <summary>
        /// A visitor for a single-operation document. A document with more than one
        /// operation needs the executing one: see <see cref="SqlVisitor(GraphQLOperationDefinition)"/>.
        /// </summary>
        public SqlVisitor() { }

        /// <summary>
        /// A visitor that builds query nodes from <paramref name="executingOperation"/> only.
        /// Every other operation in the document is skipped: two operations can carry root
        /// fields with the same response key, and the other operation's node must never
        /// answer for the executing one.
        /// </summary>
        public SqlVisitor(GraphQLOperationDefinition executingOperation)
        {
            _executingOperation = executingOperation ?? throw new ArgumentNullException(nameof(executingOperation));
        }

        protected override ValueTask VisitOperationDefinitionAsync(GraphQLOperationDefinition operationDefinition, ISqlContext context)
        {
            if (_executingOperation != null && !ReferenceEquals(operationDefinition, _executingOperation))
                return ValueTask.CompletedTask;
            return base.VisitOperationDefinitionAsync(operationDefinition, context);
        }

        // A selection the client excluded with @skip/@include is not part of the request:
        // it adds no SQL column and the column read guards never see it.
        protected override async ValueTask VisitFieldAsync(GraphQLField field, ISqlContext context)
        {
            if (field == null || !SelectionDirectives.IsIncluded(field, context.Variables))
                return;
            context.PushField(field.Name.StringValue, field.Alias?.Name?.StringValue);
            await base.VisitFieldAsync(field, context);
            context.PopField();
        }
        protected override async ValueTask VisitArgumentAsync(GraphQLArgument argument, ISqlContext context)
        {
            context.PushArgument(argument.Name.StringValue);
            await base.VisitArgumentAsync(argument, context);
            context.PopArgument();
        }

        protected override async ValueTask VisitObjectValueAsync(GraphQLObjectValue objectValue, ISqlContext context)
        {
            var result = new Dictionary<string, object?>();
            context.FieldSetters.Push((key, value) => result[key] = value);
            await base.VisitObjectValueAsync(objectValue, context);
            context.FieldSetters.Pop();
            context.Setters.FirstOrDefault()?.Invoke(result);
            context.AddValue(result);
        }

        protected override async ValueTask VisitObjectFieldAsync(GraphQLObjectField objectField, ISqlContext context)
        {
            context.Setters.Push(value => context.FieldSetters.FirstOrDefault()?.Invoke(objectField.Name.StringValue, value));
            await base.VisitObjectFieldAsync(objectField, context);
            context.Setters.Pop();
        }

        protected override async ValueTask VisitVariableAsync(GraphQLVariable variable, ISqlContext context)
        {
            var foundVariable = context.Variables.FirstOrDefault(v => v.Name == variable.Name.StringValue);
            if (foundVariable == null)
                throw new ArgumentException($"no data provided for variable: {variable.Name.StringValue}");
            context.Setters.FirstOrDefault()?.Invoke(foundVariable?.Value);
            context.AddValue(foundVariable?.Value);
            await base.VisitVariableAsync(variable, context);
        }

        protected override async ValueTask VisitInlineFragmentAsync(GraphQLInlineFragment inlineFragment, ISqlContext context)
        {
            if (!SelectionDirectives.IsIncluded(inlineFragment, context.Variables))
                return;
            await base.VisitInlineFragmentAsync(inlineFragment, context);
        }

        // Directive arguments (`@include(if: $x)`) are not field arguments: visiting them
        // would push `if` onto the enclosing field as if the client had passed it there.
        protected override ValueTask VisitDirectiveAsync(GraphQLDirective directive, ISqlContext context)
            => ValueTask.CompletedTask;

        protected override async ValueTask VisitFragmentSpreadAsync(GraphQLFragmentSpread spread, ISqlContext context)
        {
            if (!SelectionDirectives.IsIncluded(spread, context.Variables))
                return;
            context.AddFragmentSpread(spread.FragmentName.Name.StringValue);
            await base.VisitFragmentSpreadAsync(spread, context);
        }

        protected override async ValueTask VisitFragmentDefinitionAsync(GraphQLFragmentDefinition fragmentDefinition, ISqlContext context)
        {
            context.PushFragment(fragmentDefinition.FragmentName.Name.StringValue, fragmentDefinition.FragmentName.Name.StringValue);
            await base.VisitFragmentDefinitionAsync(fragmentDefinition, context);
            context.PopFragment();
        }

        protected override ValueTask VisitIntValueAsync(GraphQLIntValue value, ISqlContext context)
        {
            // Parse as Int32 when it fits, else Int64, else BigInteger. A literal beyond
            // Int64 range (e.g. `_eq: 99999999999999999999999999`) previously reached
            // `long.Parse` and threw OverflowException — an unclassified exception escaping
            // document parsing to the host — because OverflowException is not FormatException.
            // BigInteger has no overflow and DbParameterBinder.ToProviderValue already binds
            // it (in-range as long, out-of-range as string) for BigInt columns. Keeping small
            // values as Int32 preserves the `(int?)` casts that read limit/offset arguments.
            var text = value.Value.ToString();
            // Box each branch as object independently: a `cond ? int : long` ternary has type
            // long, which would box even small values as Int64 and break the `(int?)` casts.
            // Parse with Integer + InvariantCulture: the bare TryParse overloads take the CURRENT
            // culture, so under a culture whose NegativeSign is not the ASCII hyphen a wire "-5"
            // would fail both parses and box as BigInteger (GraphQL int literals are always
            // ASCII '-' + digits).
            object parsed;
            if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i))
                parsed = i;
            else if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l))
                parsed = l;
            else
                // A GraphQLIntValue is grammar-guaranteed to be an integer literal, so
                // BigInteger.Parse cannot throw FormatException here — and BigInteger cannot
                // overflow, so this branch is exception-free for any valid int literal.
                parsed = System.Numerics.BigInteger.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            context.Set(parsed);
            context.AddValue(parsed);
            return base.VisitIntValueAsync(value, context);
        }
        protected override ValueTask VisitFloatValueAsync(GraphQLFloatValue value, ISqlContext context)
        {
            // GraphQL float literals are dot-decimal on the wire regardless of the
            // host culture. Convert.ToDouble(string) honours CurrentCulture, so under
            // a comma-decimal culture (de-DE) "1.5" parsed as 15 — a silently wrong
            // filter value. Parse invariant, matching the IntValue path above.
            var parsed = double.Parse(value.Value.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            context.Set(parsed);
            context.AddValue(parsed);
            return base.VisitFloatValueAsync(value, context);
        }
        protected override async ValueTask VisitStringValueAsync(GraphQLStringValue stringValue, ISqlContext context)
        {
            context.Set(stringValue.Value.Span.ToString());
            context.AddValue(stringValue.Value.Span.ToString());
            await base.VisitStringValueAsync(stringValue, context);
        }

        protected override ValueTask VisitBooleanValueAsync(GraphQLBooleanValue value, ISqlContext context)
        {
            context.Set(value.BoolValue);
            context.AddValue(value.BoolValue);
            return base.VisitBooleanValueAsync(value, context);
        }
        protected override ValueTask VisitNullValueAsync(GraphQLNullValue value, ISqlContext context)
        {
            context.Set(null);
            context.AddValue(null);
            return base.VisitNullValueAsync(value, context);
        }
        protected override ValueTask VisitEnumValueAsync(GraphQLEnumValue value, ISqlContext context)
        {
            context.Set(value.Name.StringValue);
            context.AddValue(value.Name.StringValue);
            return base.VisitEnumValueAsync(value, context);
        }
        protected override async ValueTask VisitListValueAsync(GraphQLListValue listType, ISqlContext context)
        {
            var result = new List<object?>();
            context.Setters.Push(value => result.Add(value));
            await base.VisitListValueAsync(listType, context);
            context.Setters.Pop();
            context.Setters.FirstOrDefault()?.Invoke(result);
            context.AddValue(result);
        }

        protected override async ValueTask VisitDocumentAsync(GraphQLDocument document, ISqlContext context)
        {
            if (_executingOperation == null && document.Definitions.OfType<GraphQLOperationDefinition>().Skip(1).Any())
                throw new InvalidOperationException(
                    "A document with more than one operation must be visited with the executing operation.");
            await base.VisitDocumentAsync(document, context);
            context.SyncFragments();
        }
    }
}
