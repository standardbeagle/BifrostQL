using System.Globalization;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// Translates a parsed OData <c>$filter</c> <see cref="ODataFilterNode"/> tree into the EXACT
    /// same parameterized <see cref="TableFilter"/> structure the GraphQL query path builds — the
    /// filter is expressed as the GraphQL filter dictionary (<c>{ prop: { _eq: value }, and:[…] }</c>)
    /// and handed to <see cref="TableFilter.FromObject"/>, so every literal becomes a bound
    /// <see cref="TableFilter.Value"/> and is emitted as a SQL parameter by the shared filter
    /// machinery — never interpolated into SQL or GraphQL text. The adapter builds no WHERE of its
    /// own beyond this predicate: the resulting filter is set on the intent's
    /// <see cref="GqlObjectQuery.Filter"/> and AND-composed by the pipeline with the tenant/
    /// soft-delete/policy filters (which the adapter can neither see nor replace).
    ///
    /// <para>Property names are validated against the identity-filtered column projection
    /// (<see cref="ODataProperty.Resolve"/>): an unknown or read-denied name is a 400, never
    /// interpolated. A literal is coerced against its target column's EDM type; an incompatible or
    /// out-of-range literal is a clean 400 — the numeric/date parse catches the full
    /// FormatException/OverflowException/ArgumentException family
    /// (.claude/rules/protocol-adapter-security.md invariant 5). <c>not</c> is pushed into the
    /// leaves by De Morgan (negating the leaf operator, swapping and/or) so the whole tree maps onto
    /// the existing negated operators (<c>_neq/_nin/_ncontains</c>, <c>IS [NOT] NULL</c>) rather than
    /// needing a NOT node the filter model does not have.</para>
    /// </summary>
    internal static class ODataFilterTranslator
    {
        private const string Context = "$filter";

        /// <summary>
        /// Parses and translates <paramref name="filterText"/> to a <see cref="TableFilter"/>, or
        /// null when there is no <c>$filter</c>. Throws <see cref="ODataProtocolException"/> (400)
        /// for any parse, property, or coercion failure.
        /// </summary>
        public static TableFilter? Translate(ODataEntity entity, string? filterText, ITypeMapper typeMapper)
        {
            if (entity is null) throw new ArgumentNullException(nameof(entity));
            if (typeMapper is null) throw new ArgumentNullException(nameof(typeMapper));
            if (string.IsNullOrWhiteSpace(filterText))
                return null;

            var ast = ODataFilterParser.Parse(filterText);
            var dict = ToDict(entity, typeMapper, ast, negate: false);
            return TableFilter.FromObject(dict, entity.Table.DbName);
        }

        /// <summary>
        /// Lowers one AST node to a GraphQL-shaped filter dictionary. <paramref name="negate"/>
        /// carries an enclosing <c>not</c> down through De Morgan: it swaps and/or, negates each
        /// leaf comparison, and flips null / contains / in to their negated operators.
        /// </summary>
        private static Dictionary<string, object?> ToDict(
            ODataEntity entity, ITypeMapper typeMapper, ODataFilterNode node, bool negate)
        {
            switch (node)
            {
                case NotNode not:
                    return ToDict(entity, typeMapper, not.Operand, !negate);

                case BinaryNode binary:
                {
                    var op = negate ? Swap(binary.BoolOp) : binary.BoolOp;
                    var children = new List<object>
                    {
                        ToDict(entity, typeMapper, binary.Left, negate),
                        ToDict(entity, typeMapper, binary.Right, negate),
                    };
                    return new Dictionary<string, object?> { { op, children } };
                }

                case NullCompareNode nullCompare:
                {
                    var column = ODataProperty.Resolve(entity, nullCompare.Property, Context);
                    var wantsEqual = negate ? !nullCompare.IsEqual : nullCompare.IsEqual;
                    return Leaf(column, wantsEqual ? FilterOperators.Eq : FilterOperators.Neq, null);
                }

                case ComparisonNode comparison:
                {
                    var column = ODataProperty.Resolve(entity, comparison.Property, Context);
                    var op = negate ? Negate(comparison.Op) : comparison.Op;
                    var value = CoerceScalar(column, typeMapper, comparison.Value, Context);
                    return Leaf(column, OpToken(op), value);
                }

                case ContainsNode contains:
                {
                    var column = ODataProperty.Resolve(entity, contains.Property, Context);
                    RequireStringColumn(column, typeMapper);
                    var value = RequireStringLiteral(column, contains.Value);
                    return Leaf(column, negate ? FilterOperators.NContains : FilterOperators.Contains, value);
                }

                case InNode inList:
                {
                    var column = ODataProperty.Resolve(entity, inList.Property, Context);
                    var values = inList.Values
                        .Select(v => CoerceScalar(column, typeMapper, v, $"{Context} 'in' list"))
                        .ToList();
                    return Leaf(column, negate ? FilterOperators.NIn : FilterOperators.In, values);
                }

                default:
                    // The parser only produces the cases above; a new node without a case here is a bug.
                    throw new InvalidOperationException($"Unhandled $filter node '{node.GetType().Name}'.");
            }
        }

        private static Dictionary<string, object?> Leaf(ColumnDto column, string op, object? value)
            => new() { { column.GraphQlName, new Dictionary<string, object?> { { op, value } } } };

        // ---- literal coercion ---------------------------------------------------------------

        /// <summary>
        /// Coerces a literal to the CLR value bound for <paramref name="column"/>, validating the
        /// literal's syntactic kind against the column's EDM type. An incompatible kind or an
        /// out-of-range number is a clean 400; the value returned here is what the shared filter
        /// machinery binds as a SQL parameter.
        /// </summary>
        private static object? CoerceScalar(ColumnDto column, ITypeMapper typeMapper, ODataLiteral literal, string context)
        {
            if (literal.Kind == ODataLiteralKind.Null)
                throw ODataProtocolException.BadRequest(
                    $"{context} does not support a null value for property '{column.GraphQlName}'.");

            var edm = ODataEdmTypes.ForColumn(column, typeMapper);
            return edm switch
            {
                "Edm.String" => RequireStringLiteral(column, literal),
                "Edm.Boolean" => literal.Kind == ODataLiteralKind.Boolean
                    ? bool.Parse(literal.Text)
                    : throw Mismatch(column, "a boolean"),
                "Edm.Int16" or "Edm.Int32" or "Edm.Int64" or "Edm.Byte"
                    => ParseNumber(column, literal, context, "an integer", t => long.Parse(t, CultureInfo.InvariantCulture)),
                "Edm.Decimal"
                    => ParseNumber(column, literal, context, "a decimal", t => decimal.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture)),
                "Edm.Double"
                    => ParseNumber(column, literal, context, "a number", t => double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture)),
                "Edm.DateTimeOffset"
                    => ParseDateTime(column, literal, context),
                // JSON and any other type project as Edm.String and accept a string literal.
                _ => RequireStringLiteral(column, literal),
            };
        }

        private static object ParseNumber(
            ColumnDto column, ODataLiteral literal, string context, string what, Func<string, object> parse)
        {
            if (literal.Kind != ODataLiteralKind.Number)
                throw Mismatch(column, what);
            try
            {
                return parse(literal.Text);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                // Invariant 5: the whole parse-exception family (incl. an overflowing value)
                // collapses to one deterministic 400, never an unhandled 500.
                throw ODataProtocolException.BadRequest(
                    $"{context} value '{literal.Text}' is not {what} for property '{column.GraphQlName}'.");
            }
        }

        private static object ParseDateTime(ColumnDto column, ODataLiteral literal, string context)
        {
            if (literal.Kind != ODataLiteralKind.String)
                throw Mismatch(column, "a date/time");
            try
            {
                // Validate it is a real instant; bind the canonical text (the query path binds
                // date filter values as text and lets the dialect cast the column).
                _ = DateTimeOffset.Parse(literal.Text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                return literal.Text;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw ODataProtocolException.BadRequest(
                    $"{context} value '{literal.Text}' is not a valid date/time for property '{column.GraphQlName}'.");
            }
        }

        private static string RequireStringLiteral(ColumnDto column, ODataLiteral literal)
            => literal.Kind == ODataLiteralKind.String
                ? literal.Text
                : throw Mismatch(column, "a string");

        private static void RequireStringColumn(ColumnDto column, ITypeMapper typeMapper)
        {
            if (ODataEdmTypes.ForColumn(column, typeMapper) != "Edm.String")
                throw ODataProtocolException.BadRequest(
                    $"$filter contains() is only supported on string properties; '{column.GraphQlName}' is not one.");
        }

        private static ODataProtocolException Mismatch(ColumnDto column, string expected)
            => ODataProtocolException.BadRequest(
                $"$filter literal for property '{column.GraphQlName}' must be {expected}.");

        // ---- operator mapping (incl. De Morgan negation) ------------------------------------

        private static string OpToken(ComparisonOp op) => op switch
        {
            ComparisonOp.Eq => FilterOperators.Eq,
            ComparisonOp.Ne => FilterOperators.Neq,
            ComparisonOp.Lt => FilterOperators.Lt,
            ComparisonOp.Le => FilterOperators.Lte,
            ComparisonOp.Gt => FilterOperators.Gt,
            ComparisonOp.Ge => FilterOperators.Gte,
            _ => throw new InvalidOperationException($"Unhandled comparison operator '{op}'."),
        };

        private static ComparisonOp Negate(ComparisonOp op) => op switch
        {
            ComparisonOp.Eq => ComparisonOp.Ne,
            ComparisonOp.Ne => ComparisonOp.Eq,
            ComparisonOp.Lt => ComparisonOp.Ge,
            ComparisonOp.Le => ComparisonOp.Gt,
            ComparisonOp.Gt => ComparisonOp.Le,
            ComparisonOp.Ge => ComparisonOp.Lt,
            _ => throw new InvalidOperationException($"Unhandled comparison operator '{op}'."),
        };

        private static string Swap(string boolOp)
            => boolOp == BinaryNode.And ? BinaryNode.Or : BinaryNode.And;
    }
}
