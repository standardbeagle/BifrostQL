using System.Globalization;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// The programmatic query an entity-set read compiles to: the <see cref="GqlObjectQuery"/> tree
    /// handed to <see cref="Core.Resolvers.IQueryIntentExecutor"/>, plus the ordered set of columns
    /// the response projects. The adapter builds NO predicate of its own — no <c>WHERE</c>/Filter is
    /// ever set here; the transformer pipeline narrows scope from the caller's identity, so tenant
    /// isolation and soft-delete are unskippable (.claude/rules/protocol-adapter-security.md, read
    /// seam invariant).
    /// </summary>
    internal sealed record ODataEntityRead(GqlObjectQuery Query, IReadOnlyList<ColumnDto> ProjectedColumns);

    /// <summary>
    /// Pure translation from an entity-set GET plus its <c>$select</c>/<c>$orderby</c>/<c>$top</c>/
    /// <c>$skip</c> options into a programmatic <see cref="GqlObjectQuery"/>. Nothing here assembles
    /// GraphQL text or SQL: property names are resolved against the identity-filtered
    /// <see cref="ODataEntity"/> projection (a name that is not a visible column — including a
    /// read-denied one — is rejected, never interpolated), and options map onto the query tree's
    /// <see cref="GqlObjectQuery.ScalarColumns"/>, <see cref="GqlObjectQuery.Sort"/>,
    /// <see cref="GqlObjectQuery.Limit"/>, and <see cref="GqlObjectQuery.Offset"/> fields. Every
    /// validation failure throws a user-facing <see cref="ODataProtocolException"/>.
    ///
    /// <para>Ordering is always made a TOTAL order: the caller's <c>$orderby</c> keys are followed by
    /// the entity's full primary key (in schema order, ascending) as a deterministic tiebreak, so a
    /// page boundary never splits rows that compare equal on the requested keys. With no
    /// <c>$orderby</c> the sort is the primary key ascending. Composite keys are emitted in full —
    /// never a first-column guess (.claude/rules/composite-pk-compliance.md).</para>
    /// </summary>
    internal static class ODataEntityReadTranslator
    {
        public static ODataEntityRead Translate(
            ODataEntity entity, ODataReadOptions options, int defaultPageSize, int maxPageSize,
            TableFilter? filter = null)
        {
            if (entity is null) throw new ArgumentNullException(nameof(entity));
            if (options is null) throw new ArgumentNullException(nameof(options));

            var projected = ResolveSelect(entity, options.Select);
            var sort = ResolveOrderBy(entity, options.OrderBy);
            var limit = ResolveTop(options.Top, defaultPageSize, maxPageSize);
            var offset = ResolveSkip(options.Skip);

            var table = entity.Table;
            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
                Sort = sort,
                Limit = limit,
                // The $filter predicate, already translated to the SAME parameterized TableFilter the
                // GraphQL path uses. It is AND-composed with the pipeline's tenant/soft-delete/policy
                // filters downstream — it never replaces or bypasses them (the adapter cannot).
                Filter = filter,
            };
            foreach (var column in projected)
                query.ScalarColumns.Add(new GqlObjectColumn(column.DbName));
            if (offset > 0)
                query.Offset = offset;

            return new ODataEntityRead(query, projected);
        }

        /// <summary>
        /// The projected columns: the explicit <c>$select</c> set (validated, de-duplicated, order
        /// preserved) or — absent <c>$select</c> — every column the identity may read.
        /// </summary>
        private static IReadOnlyList<ColumnDto> ResolveSelect(ODataEntity entity, string? select)
        {
            if (string.IsNullOrWhiteSpace(select))
                return entity.Columns;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<ColumnDto>();
            foreach (var raw in select.Split(','))
            {
                var name = raw.Trim();
                if (name.Length == 0)
                    throw ODataProtocolException.BadRequest("$select contains an empty property name.");

                var column = ResolveProperty(entity, name, "$select");
                if (seen.Add(column.GraphQlName))
                    result.Add(column);
            }
            return result;
        }

        /// <summary>
        /// Builds the ORDER BY token list. Each <c>$orderby</c> item is <c>&lt;property&gt;</c> with an
        /// optional <c>asc</c>/<c>desc</c> direction (default ascending); the property is resolved
        /// against the visible columns. The entity's full primary key is appended ascending as a
        /// stable tiebreak (skipping any key already named), so the total order is deterministic.
        /// </summary>
        private static List<string> ResolveOrderBy(ODataEntity entity, string? orderBy)
        {
            var tokens = new List<string>();
            var referenced = new HashSet<string>(StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(orderBy))
            {
                foreach (var raw in orderBy.Split(','))
                {
                    var item = raw.Trim();
                    if (item.Length == 0)
                        throw ODataProtocolException.BadRequest("$orderby contains an empty clause.");

                    var parts = item.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    var direction = "asc";
                    if (parts.Length == 2)
                    {
                        direction = parts[1].ToLowerInvariant();
                        if (direction is not ("asc" or "desc"))
                            throw ODataProtocolException.BadRequest(
                                $"Invalid $orderby direction '{parts[1]}'; expected 'asc' or 'desc'.");
                    }
                    else if (parts.Length != 1)
                    {
                        throw ODataProtocolException.BadRequest(
                            $"Invalid $orderby clause '{item}'; expected '<property> [asc|desc]'.");
                    }

                    var column = ResolveProperty(entity, parts[0], "$orderby");
                    if (referenced.Add(column.GraphQlName))
                        tokens.Add($"{column.GraphQlName}_{direction}");
                }
            }

            // Deterministic tiebreak: append the full primary key ascending, in schema order.
            foreach (var key in entity.KeyColumns)
            {
                if (referenced.Add(key.GraphQlName))
                    tokens.Add($"{key.GraphQlName}_asc");
            }

            return tokens;
        }

        /// <summary>
        /// The page size: <c>$top</c> when supplied, else the endpoint default, clamped to the
        /// endpoint maximum. A non-integer or out-of-range value (e.g. a 29-digit number that
        /// overflows) is a clean 400 — never an unhandled parse fault
        /// (.claude/rules/protocol-adapter-security.md invariant 5); a negative value is rejected.
        /// </summary>
        private static int ResolveTop(string? top, int defaultPageSize, int maxPageSize)
        {
            if (top is null)
                return Math.Min(defaultPageSize, maxPageSize);

            var requested = ParseNonNegative(top, "$top");
            return Math.Min(requested, maxPageSize);
        }

        /// <summary>The row offset from <c>$skip</c> (0 when absent). Negative is rejected.</summary>
        private static int ResolveSkip(string? skip)
            => skip is null ? 0 : ParseNonNegative(skip, "$skip");

        /// <summary>
        /// Parses a non-negative integer from untrusted query text. <see cref="int.TryParse(string,
        /// NumberStyles, IFormatProvider, out int)"/> returns false (never throws) on a malformed or
        /// overflowing value, so the whole parse-exception family collapses to one clean 400.
        /// </summary>
        private static int ParseNonNegative(string value, string option)
        {
            var text = value.Trim();
            if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                throw ODataProtocolException.BadRequest($"{option} must be an integer.");
            if (number < 0)
                throw ODataProtocolException.BadRequest($"{option} must be non-negative.");
            return number;
        }

        /// <summary>
        /// Resolves a caller-supplied property name against the entity's VISIBLE columns via the
        /// shared <see cref="ODataProperty.Resolve"/> rule (case-insensitive; unknown/read-denied →
        /// 400, ambiguous → 400) so $select/$orderby and $filter resolve names identically.
        /// </summary>
        private static ColumnDto ResolveProperty(ODataEntity entity, string name, string option)
            => ODataProperty.Resolve(entity, name, option);
    }
}
