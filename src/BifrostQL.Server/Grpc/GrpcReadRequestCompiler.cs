using System.Globalization;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// The ONE compiler both the unary <c>List</c> and the server-streaming <c>Stream</c> RPC feed:
    /// it turns a decoded List/Stream request (its <c>filter</c> / <c>order_by</c> / <c>page_size</c>
    /// / <c>page_token</c> fields) into the programmatic <see cref="GqlObjectQuery"/> handed to
    /// <see cref="Core.Resolvers.IQueryIntentExecutor"/>. Because both RPCs compile through here, the
    /// same request yields identical, identically-ordered rows on both surfaces (criterion 4).
    ///
    /// <para><b>Adapter builds no scope.</b> The filter is expressed as the SAME parameterized
    /// <see cref="TableFilter"/> the GraphQL/OData paths build (every literal becomes a bound SQL
    /// parameter, never interpolated; field/operator/sort names are validated against the caller's
    /// identity-visible columns and the documented operator set, never against raw request text).
    /// The compiled filter is set on <see cref="GqlObjectQuery.Filter"/> and AND-composed downstream
    /// by the pipeline with tenant/soft-delete/policy filters — it can neither replace nor bypass
    /// them (criterion 3).</para>
    ///
    /// <para><b>Every untrusted bound is enforced BEFORE it is honored</b> (invariant 6): filter
    /// nesting depth and predicate count, <c>_in</c>/<c>_between</c> list size, sort-key count, page
    /// size, and cursor length are all capped by <see cref="GrpcReadCaps"/> up front. A malformed or
    /// over-cap request throws <see cref="GrpcRequestException"/> → a clean INVALID_ARGUMENT through
    /// the single <see cref="GrpcStatusMapper"/> funnel, never an unbounded recurse/allocate or a
    /// leaked internal fault (invariants 3, 5, 6).</para>
    /// </summary>
    internal static class GrpcReadRequestCompiler
    {
        private const string FilterField = "filter";
        private const string OrderByField = "order_by";
        private const string PageSizeField = "page_size";
        private const string PageTokenField = "page_token";

        // Exactly the documented Bifrost operator vocabulary this surface exposes (criterion 1).
        private static readonly HashSet<string> AllowedOperators = new(StringComparer.Ordinal)
        {
            FilterOperators.Eq, FilterOperators.Neq,
            FilterOperators.Lt, FilterOperators.Lte, FilterOperators.Gt, FilterOperators.Gte,
            FilterOperators.Contains, FilterOperators.In, FilterOperators.Between, FilterOperators.Null,
        };

        // A generous stdlib backstop; the explicit logical-depth counter below is the real invariant-6
        // guard (bound BEFORE recursing), this only stops a pathological token stream at parse time.
        private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 2 * GrpcReadCaps.MaxFilterDepth + 4 };

        public static GrpcCompiledRead Compile(
            IDbTable table,
            IReadOnlyList<ColumnDto> visibleColumns,
            IReadOnlyDictionary<string, object?> request,
            int defaultPageSize,
            int maxPageSize,
            IDictionary<string, object?> identity,
            byte[] tokenSecret,
            DateTimeOffset now,
            TimeSpan ttl)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(visibleColumns);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(tokenSecret);

            var byGraphQlName = BuildColumnLookup(visibleColumns);

            var filterText = ReadString(request, FilterField);
            var orderByText = ReadString(request, OrderByField);
            var pageSize = ResolvePageSize(request, defaultPageSize, maxPageSize);

            var filter = CompileFilter(table, byGraphQlName, filterText);
            var sort = CompileSort(table, byGraphQlName, orderByText);
            var offset = ResolveOffset(request, table, filterText, orderByText, pageSize, identity, tokenSecret, now, ttl);

            var query = BuildRowQuery(table);
            query.Filter = filter;
            query.Sort = sort;
            query.Limit = pageSize;
            if (offset > 0)
                query.Offset = offset;

            var binding = new GrpcPageBinding(
                table.GraphQlName,
                GrpcPageCursor.QueryShapeHash(filterText, orderByText),
                pageSize,
                GrpcPageCursor.FingerprintIdentity(identity));

            return new GrpcCompiledRead(query, offset, pageSize, binding);
        }

        // ---- page size -------------------------------------------------------------------------

        private static int ResolvePageSize(
            IReadOnlyDictionary<string, object?> request, int defaultPageSize, int maxPageSize)
        {
            var effectiveDefault = Math.Min(defaultPageSize, maxPageSize);
            if (!request.TryGetValue(PageSizeField, out var raw) || raw is null)
                return effectiveDefault;

            var requested = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            // proto3 default (0) and any non-positive value mean "unspecified" → endpoint default;
            // a positive value is clamped to the endpoint maximum so a caller can never widen the cap.
            return requested <= 0 ? effectiveDefault : Math.Min(requested, maxPageSize);
        }

        // ---- cursor / offset -------------------------------------------------------------------

        private static int ResolveOffset(
            IReadOnlyDictionary<string, object?> request,
            IDbTable table,
            string? filterText,
            string? orderByText,
            int pageSize,
            IDictionary<string, object?> identity,
            byte[] tokenSecret,
            DateTimeOffset now,
            TimeSpan ttl)
        {
            var token = ReadString(request, PageTokenField);
            if (token is null)
                return 0;

            // Size cap enforced BEFORE any decode work (invariant 6) so an over-long token is a
            // fixed-work rejection, not an unbounded parse.
            if (token.Length > GrpcReadCaps.MaxCursorChars)
                throw GrpcRequestException.InvalidArgument("The page token provided is not valid.");

            var binding = new GrpcPageBinding(
                table.GraphQlName,
                GrpcPageCursor.QueryShapeHash(filterText, orderByText),
                pageSize,
                GrpcPageCursor.FingerprintIdentity(identity));

            return GrpcPageCursor.Decode(token, binding, tokenSecret, now, ttl);
        }

        // ---- filter ----------------------------------------------------------------------------

        private static TableFilter? CompileFilter(
            IDbTable table, IReadOnlyDictionary<string, ColumnDto> columns, string? filterText)
        {
            if (string.IsNullOrWhiteSpace(filterText))
                return null;

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(filterText, JsonOptions);
                root = doc.RootElement.Clone();
            }
            // Any malformed JSON (incl. over-depth from the stdlib backstop) is a clean
            // INVALID_ARGUMENT — never an unhandled parse fault (invariants 5, 6).
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                throw GrpcRequestException.InvalidArgument("The filter is not valid JSON.");
            }

            var predicateBudget = GrpcReadCaps.MaxFilterPredicates;
            var dict = TransformNode(root, columns, depth: 1, ref predicateBudget);

            try
            {
                return TableFilter.FromObject(dict, table.DbName);
            }
            // Defensive: names/operators are already validated, so this should not fire — but a
            // structural surprise must still be a sanitized INVALID_ARGUMENT, never a leaked
            // BifrostExecutionError on the wire (invariant 3).
            catch (Exception ex) when (ex is not GrpcRequestException)
            {
                throw GrpcRequestException.InvalidArgument("The filter could not be compiled.");
            }
        }

        /// <summary>
        /// Lowers one JSON filter node into the GraphQL-shaped dictionary
        /// <see cref="TableFilter.FromObject"/> consumes, validating names/operators and coercing
        /// literals as it goes. <paramref name="depth"/> is checked BEFORE descending into an
        /// <c>and</c>/<c>or</c> block (invariant 6): the cap can never be exceeded by recursion.
        /// </summary>
        private static Dictionary<string, object?> TransformNode(
            JsonElement node, IReadOnlyDictionary<string, ColumnDto> columns, int depth, ref int predicateBudget)
        {
            if (depth > GrpcReadCaps.MaxFilterDepth)
                throw GrpcRequestException.InvalidArgument(
                    $"The filter is nested too deeply (more than {GrpcReadCaps.MaxFilterDepth} levels).");
            if (node.ValueKind != JsonValueKind.Object)
                throw GrpcRequestException.InvalidArgument("A filter node must be a JSON object.");

            var result = new Dictionary<string, object?>();
            foreach (var property in node.EnumerateObject())
            {
                var key = property.Name;
                if (key is "and" or "or")
                {
                    if (property.Value.ValueKind != JsonValueKind.Array)
                        throw GrpcRequestException.InvalidArgument($"'{key}' must be an array of filter nodes.");

                    var operands = new List<object>();
                    foreach (var element in property.Value.EnumerateArray())
                    {
                        Spend(ref predicateBudget);
                        operands.Add(TransformNode(element, columns, depth + 1, ref predicateBudget));
                    }
                    result[key] = operands;
                }
                else
                {
                    var column = ResolveColumn(columns, key);
                    result[column.GraphQlName] = TransformLeaf(column, property.Value, ref predicateBudget);
                }
            }

            if (result.Count == 0)
                throw GrpcRequestException.InvalidArgument("A filter node must contain at least one predicate.");
            return result;
        }

        /// <summary>Lowers a <c>{ _op: value, … }</c> leaf into the operator→value dict, one predicate per operator.</summary>
        private static Dictionary<string, object?> TransformLeaf(
            ColumnDto column, JsonElement value, ref int predicateBudget)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw GrpcRequestException.InvalidArgument(
                    $"Filter for '{column.GraphQlName}' must be an object mapping an operator to a value.");

            var predicate = new Dictionary<string, object?>();
            foreach (var op in value.EnumerateObject())
            {
                Spend(ref predicateBudget);
                if (!AllowedOperators.Contains(op.Name))
                    throw GrpcRequestException.InvalidArgument($"Unsupported filter operator '{op.Name}'.");
                predicate[op.Name] = CoerceOperand(column, op.Name, op.Value);
            }

            if (predicate.Count == 0)
                throw GrpcRequestException.InvalidArgument(
                    $"Filter for '{column.GraphQlName}' must specify an operator.");
            return predicate;
        }

        private static object? CoerceOperand(ColumnDto column, string op, JsonElement value)
        {
            switch (op)
            {
                case FilterOperators.Null:
                    return value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw Mismatch(column, op, "a boolean"),
                    };

                case FilterOperators.In:
                    return CoerceList(column, op, value, minCount: 1);

                case FilterOperators.Between:
                {
                    var bounds = CoerceList(column, op, value, minCount: 2);
                    if (bounds.Count != 2)
                        throw GrpcRequestException.InvalidArgument(
                            $"Operator '{op}' on '{column.GraphQlName}' requires exactly two values.");
                    return bounds;
                }

                case FilterOperators.Contains:
                    if (GrpcProtoTypeMapper.Map(column.EffectiveDataType) != GrpcScalarKind.String)
                        throw GrpcRequestException.InvalidArgument(
                            $"Operator '{op}' is only supported on string columns; '{column.GraphQlName}' is not one.");
                    return value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : throw Mismatch(column, op, "a string");

                default:
                    return CoerceScalar(column, op, value);
            }
        }

        private static List<object?> CoerceList(ColumnDto column, string op, JsonElement value, int minCount)
        {
            if (value.ValueKind != JsonValueKind.Array)
                throw GrpcRequestException.InvalidArgument(
                    $"Operator '{op}' on '{column.GraphQlName}' requires a list of values.");

            var items = new List<object?>();
            foreach (var element in value.EnumerateArray())
            {
                if (items.Count >= GrpcReadCaps.MaxInListSize)
                    throw GrpcRequestException.InvalidArgument(
                        $"Operator '{op}' list is too large (more than {GrpcReadCaps.MaxInListSize} items).");
                items.Add(CoerceScalar(column, op, element));
            }
            if (items.Count < minCount)
                throw GrpcRequestException.InvalidArgument(
                    $"Operator '{op}' on '{column.GraphQlName}' requires at least {minCount} value(s).");
            return items;
        }

        /// <summary>
        /// Coerces one JSON literal to the CLR value bound as a SQL parameter for
        /// <paramref name="column"/>, validated against the column's proto/wire kind. An out-of-range
        /// number makes <c>TryGetInt64</c>/<c>TryGetDouble</c> return false — a clean INVALID_ARGUMENT,
        /// never an unhandled overflow (invariant 5). Text-mapped kinds (string, decimal, timestamp,
        /// bytes) bind the literal text and let the dialect cast, exactly as the OData path does.
        /// </summary>
        private static object? CoerceScalar(ColumnDto column, string op, JsonElement value)
        {
            var kind = GrpcProtoTypeMapper.Map(column.EffectiveDataType);
            switch (kind)
            {
                case GrpcScalarKind.Int32:
                case GrpcScalarKind.Int64:
                    return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l)
                        ? l
                        : throw Mismatch(column, op, "an integer");

                case GrpcScalarKind.Double:
                case GrpcScalarKind.Float:
                    return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)
                        ? d
                        : throw Mismatch(column, op, "a number");

                case GrpcScalarKind.Bool:
                    return value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw Mismatch(column, op, "a boolean"),
                    };

                default:
                    // String / decimal-as-string / Timestamp / Bytes bind the canonical text.
                    return value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : throw Mismatch(column, op, "a string");
            }
        }

        // ---- sort ------------------------------------------------------------------------------

        /// <summary>
        /// Builds the ORDER BY token list as a TOTAL order: the caller's sort keys (each validated
        /// against the visible columns, direction defaulting to ascending) followed by the table's
        /// FULL primary key ascending as a deterministic tiebreak — so a page boundary never splits
        /// rows that compare equal on the requested keys. Composite keys are emitted in full, never a
        /// first-column guess (.claude/rules/composite-pk-compliance.md).
        /// </summary>
        private static List<string> CompileSort(
            IDbTable table, IReadOnlyDictionary<string, ColumnDto> columns, string? orderByText)
        {
            var tokens = new List<string>();
            var referenced = new HashSet<string>(StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(orderByText))
            {
                var clauses = orderByText.Split(',');
                foreach (var raw in clauses)
                {
                    var item = raw.Trim();
                    if (item.Length == 0)
                        throw GrpcRequestException.InvalidArgument("The sort contains an empty clause.");

                    var parts = item.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    var direction = "asc";
                    if (parts.Length == 2)
                    {
                        direction = parts[1].ToLowerInvariant();
                        if (direction is not ("asc" or "desc"))
                            throw GrpcRequestException.InvalidArgument(
                                $"Invalid sort direction '{parts[1]}'; expected 'asc' or 'desc'.");
                    }
                    else if (parts.Length != 1)
                    {
                        throw GrpcRequestException.InvalidArgument(
                            $"Invalid sort clause '{item}'; expected '<field> [asc|desc]'.");
                    }

                    var column = ResolveColumn(columns, parts[0]);
                    if (referenced.Add(column.GraphQlName))
                    {
                        if (referenced.Count > GrpcReadCaps.MaxSortKeys)
                            throw GrpcRequestException.InvalidArgument(
                                $"Too many sort keys (more than {GrpcReadCaps.MaxSortKeys}).");
                        tokens.Add($"{column.GraphQlName}_{direction}");
                    }
                }
            }

            // Deterministic tiebreak: the full primary key ascending, in schema order — every key
            // column, never index-zero-reduced (composite-PK compliance).
            foreach (var key in table.KeyColumns)
            {
                if (referenced.Add(key.GraphQlName))
                    tokens.Add($"{key.GraphQlName}_asc");
            }

            return tokens;
        }

        // ---- shared helpers --------------------------------------------------------------------

        private static void Spend(ref int predicateBudget)
        {
            if (--predicateBudget < 0)
                throw GrpcRequestException.InvalidArgument(
                    $"The filter is too complex (more than {GrpcReadCaps.MaxFilterPredicates} predicates).");
        }

        private static Dictionary<string, ColumnDto> BuildColumnLookup(IReadOnlyList<ColumnDto> visibleColumns)
        {
            var lookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in visibleColumns)
                lookup[column.GraphQlName] = column;
            return lookup;
        }

        /// <summary>
        /// Resolves a caller-supplied field name against the identity-VISIBLE columns. An unknown name
        /// — including a column the caller may not READ — is an INVALID_ARGUMENT, never interpolated
        /// (criterion 1 / invariant 4: filtering/sorting on a hidden column would be an existence
        /// oracle). The violation names only the request field the caller supplied.
        /// </summary>
        private static ColumnDto ResolveColumn(IReadOnlyDictionary<string, ColumnDto> columns, string name)
            => columns.TryGetValue(name, out var column)
                ? column
                : throw GrpcRequestException.InvalidField(name, "Unknown or unreadable field.");

        private static GrpcRequestException Mismatch(ColumnDto column, string op, string expected)
            => GrpcRequestException.InvalidArgument(
                $"Operator '{op}' on '{column.GraphQlName}' requires {expected}.");

        private static string? ReadString(IReadOnlyDictionary<string, object?> request, string field)
        {
            if (!request.TryGetValue(field, out var raw) || raw is null)
                return null;
            var text = raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static GqlObjectQuery BuildRowQuery(IDbTable table)
        {
            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
            };
            foreach (var column in table.Columns.OrderBy(c => c.OrdinalPosition))
                query.ScalarColumns.Add(new GqlObjectColumn(column.DbName));
            return query;
        }
    }

    /// <summary>
    /// A compiled List/Stream read: the programmatic <see cref="GqlObjectQuery"/> to execute, the
    /// resolved page <see cref="Offset"/> and <see cref="PageSize"/>, and the
    /// <see cref="GrpcPageBinding"/> to mint the next page's cursor against.
    /// </summary>
    internal sealed record GrpcCompiledRead(
        GqlObjectQuery Query, int Offset, int PageSize, GrpcPageBinding Binding);
}
