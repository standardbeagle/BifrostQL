using GraphQL.Validation;
using GraphQLParser.AST;

namespace BifrostQL.Core.QueryModel
{
    /// <summary>
    /// The GraphQL <c>@skip(if:)</c> / <c>@include(if:)</c> decision for one selection
    /// (field, inline fragment or fragment spread). A selection the client excluded is
    /// not part of the request: it must not add SQL columns, and the column read guards
    /// must not judge it. Shared by <see cref="SqlVisitor"/> (the table query tree) and
    /// <c>SelectionWalk</c> (aggregate and history resolvers) so both trees see the same
    /// selection the response will carry.
    /// </summary>
    internal static class SelectionDirectives
    {
        public static bool IsIncluded(IHasDirectivesNode node, Variables? variables)
        {
            if (node.Directives is not { } directives)
                return true;
            foreach (var directive in directives.Items)
            {
                var name = directive.Name.StringValue;
                var skip = name == "skip";
                if (!skip && name != "include")
                    continue;
                var condition = Condition(directive, variables);
                if (skip == condition)
                    return false;
            }
            return true;
        }

        // Validation has already checked `if: Boolean!` is present and boolean, so a missing
        // or non-boolean value here is an engine invariant break, not a client error.
        private static bool Condition(GraphQLDirective directive, Variables? variables)
        {
            var argument = directive.Arguments?.Items.FirstOrDefault(a => a.Name.StringValue == "if")
                ?? throw new InvalidOperationException($"@{directive.Name.StringValue} has no 'if' argument.");
            return argument.Value switch
            {
                GraphQLBooleanValue literal => literal.BoolValue,
                GraphQLVariable variable => variables?.FirstOrDefault(v => v.Name == variable.Name.StringValue)?.Value is bool b
                    ? b
                    : throw new InvalidOperationException($"@{directive.Name.StringValue}(if: ${variable.Name.StringValue}) has no boolean value."),
                _ => throw new InvalidOperationException($"@{directive.Name.StringValue}(if:) is not a boolean."),
            };
        }
    }
}
