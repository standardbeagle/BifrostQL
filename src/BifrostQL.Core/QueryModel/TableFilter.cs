using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.QueryModel
{
    public enum FilterType
    {
        And,
        Or,
        Relation,
        Join,
        /// <summary>
        /// The table-scoped <c>_search</c> full-text operator. Unlike every other node
        /// (which targets a single column), a Search node carries the raw query string in
        /// <see cref="TableFilter.Value"/> and lowers to the dialect's full-text predicate
        /// over the table's validated searchable columns (see <c>FtsConfig</c>).
        /// </summary>
        Search
    }
    public sealed class TableFilter
    {
        internal TableFilter() { }
        public string? TableName { get; init; }
        public string ColumnName { get; init; } = null!;
        public string RelationName { get; set; } = null!;
        public object? Value { get; set; }
        public FilterType FilterType { get; init; }
        public TableFilter? Next { get; set; }

        /// <summary>
        /// True on filter nodes the SERVER injected (the blind-index equality
        /// rewrite). Guard collection skips these leaves: the caller never named
        /// the column, and the guards instead bind to the ORIGINAL column the
        /// rewrite recorded — a client-authored reference to the same column is
        /// a distinct, unmarked node and is still guarded.
        /// </summary>
        internal bool ServerDerived { get; init; }
        public List<TableFilter> And { get; init; } = new();
        public List<TableFilter> Or { get; init; } = new();

        /// <summary>
        /// When this node is a relationship TRAVERSAL (<see cref="ColumnName"/> names a
        /// <see cref="IDbTable.SingleLinks"/> entry rather than a column), this carries
        /// the TRAVERSED PARENT table's own transformer-derived row filter — tenant
        /// scoping, soft-delete, policy — ANDed into the sub-query the traversal renders.
        ///
        /// Without it the sub-query was <c>SELECT DISTINCT id AS joinid FROM parent WHERE
        /// &lt;caller predicate&gt;</c> over the WHOLE parent table, so a caller matched
        /// child rows through parent rows it could not see. The parent's existence and
        /// field values then leak through the child result set: probing
        /// <c>comments(filter: { posts: { title: {_eq: …} } })</c> reads another tenant's
        /// (or a soft-deleted) post title one guess at a time. The column guards already
        /// recurse into these sub-filters and assert each column against ITS OWN table's
        /// policy; this is the row-filter half of the same traversal.
        ///
        /// Populated by <c>QueryTransformerService</c> (the only component holding the
        /// user context and transformer set) at the same time it computes the node
        /// query's own filter, and consumed by <see cref="BuildSqlParameterized"/>. Null
        /// when no transformer applies to the traversed table.
        /// </summary>
        internal TableFilter? TraversedTableFilter { get; set; }

        /// <summary>
        /// True when this node is a LEAF column predicate — <c>column: { _op: value }</c>,
        /// whose <see cref="Next"/> is the terminal <see cref="FilterType.Relation"/>
        /// operator node. False for an AND/OR wrapper (<see cref="Next"/> is null) and
        /// for a relationship traversal, whose <see cref="Next"/> is a nested column or
        /// an implicit/explicit AND/OR wrapper.
        ///
        /// This is the SINGLE notion of "leaf predicate vs relationship traversal",
        /// shared by the SQL renderer (<see cref="RenderParts(SqlBuildContext, string?, JoinAliasAllocator)"/>)
        /// and by the column read/filter-guard collector
        /// (<c>QueryTransformerService.CollectFilterColumns</c>). They previously
        /// disagreed: the collector keyed on <c>Next.Next == null</c>, which is true for
        /// the AND wrapper that sibling relationship predicates produce
        /// (<c>customer: { ssn: {_eq}, active: {_eq} }</c>), so the collector treated the
        /// relationship NAME as a column, resolved it to null, and never recursed — while
        /// the renderer emitted the <c>ssn</c> predicate for real. A caller denied read on
        /// <c>ssn</c> (or holding only an encrypted-column projection of it) could then
        /// filter on it and read the value back out of which rows matched. Keeping one
        /// predicate for both callers is what makes the guarded column set and the emitted
        /// SQL structurally unable to diverge again.
        /// </summary>
        internal bool IsLeafColumnPredicate => Next is { FilterType: FilterType.Relation };

        /// <summary>
        /// ANDs two filters into one node — the single combinator behind every "narrow,
        /// never replace" composition: the mutation transformer chain folding successive
        /// <c>AdditionalFilter</c>s, the query transformer service, the query field's
        /// filter+<c>_primaryKey</c> merge, and the filtered-update pipeline ANDing the
        /// caller's WHERE onto the transformers' row scope.
        /// </summary>
        public static TableFilter CombineAnd(TableFilter first, TableFilter second) =>
            new()
            {
                And = new List<TableFilter> { first, second },
                FilterType = FilterType.And,
            };

        public static TableFilter FromPrimaryKey(IEnumerable<object?> values, IEnumerable<ColumnDto> keyColumns, string tableName)
        {
            var keyColumnList = keyColumns.ToList();
            var valueList = values.ToList();

            if (keyColumnList.Count == 0)
                throw new BifrostExecutionError($"Table '{tableName}' has no primary key columns.");

            if (valueList.Count != keyColumnList.Count)
                throw new BifrostExecutionError(
                    $"_primaryKey for '{tableName}' expects {keyColumnList.Count} value(s) " +
                    $"({string.Join(", ", keyColumnList.Select(c => c.GraphQlName))}) but received {valueList.Count}.");

            if (keyColumnList.Count == 1)
            {
                return FromObject(new Dictionary<string, object?>
                {
                    { keyColumnList[0].GraphQlName, new Dictionary<string, object?> { { "_eq", valueList[0] } } }
                }, tableName);
            }

            var andFilters = keyColumnList.Zip(valueList, (col, val) =>
                (object?)new Dictionary<string, object?>
                {
                    { col.GraphQlName, new Dictionary<string, object?> { { "_eq", val } } }
                }).ToList();

            return FromObject(new Dictionary<string, object?> { { "and", andFilters } }, tableName);
        }

        public static TableFilter FromObject(object? value, string tableName)
        {
            var dictValue = value as Dictionary<string, object?> ?? throw new BifrostExecutionError($"Error filtering {tableName}, null filter value");

            var filter = StackFilters(dictValue, tableName);
            // A lone table-scoped _search node has no And/Or/Next but is still a valid
            // filter (it lowers to the dialect full-text predicate), so it is exempt from
            // the empty-shape guard.
            if (filter.And.Count == 0 && filter.Or.Count == 0 && filter.Next == null
                && filter.FilterType != FilterType.Search)
                // BifrostExecutionError like every sibling throw in this parse path: this is a
                // CLIENT-shape fault (e.g. `and: []`), and only that type's text is forwarded to
                // the GraphQL error. As a bare ArgumentException it fell through resolvers'
                // catches into the engine's generic unexpected-error path.
                throw new BifrostExecutionError($"Error filtering {tableName}, invalid filter object");
            return filter;
        }

        private static TableFilter StackFilters(IDictionary<string, object?> filter, string? tableName)
        {
            if (!filter.Any()) throw new BifrostExecutionError($"Filter on {tableName} has no properties");

            // Sibling keys form an implicit AND: `{ status: {_eq:...}, owner_id: {_eq:...} }`
            // must constrain on BOTH columns. Previously only `filter.FirstOrDefault()`
            // was taken and every remaining key was silently dropped, producing an
            // over-broad WHERE clause (a security/correctness hazard). Wrap each entry
            // in an AND so no sibling is lost.
            if (filter.Count > 1)
            {
                return new TableFilter
                {
                    And = filter.Select(kv => StackSingle(kv, tableName)).ToList(),
                    FilterType = FilterType.And,
                };
            }

            return StackSingle(filter.First(), tableName);
        }

        private static TableFilter StackSingle(KeyValuePair<string, object?> kv, string? tableName)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) throw new BifrostExecutionError($"Filter on {tableName} has empty property name");
            return kv switch
            {
                // The table-scoped full-text operator. Its value is the raw query STRING
                // (not a nested `{ _op: value }` object like a column predicate), so it is
                // captured verbatim here and lowered to the dialect's full-text predicate
                // over the table's searchable columns at render time.
                { Key: FilterOperators.Search } => new TableFilter
                {
                    FilterType = FilterType.Search,
                    TableName = tableName,
                    RelationName = FilterOperators.Search,
                    Value = kv.Value,
                },
                { Key: "and" } => new TableFilter
                {
                    And = ((IEnumerable<object>)kv.Value!).Select(v => StackFilters((IDictionary<string, object?>)v, tableName)).ToList(),
                    FilterType = FilterType.And,
                },
                { Key: "or" } => new TableFilter
                {
                    Or = ((IEnumerable<object>)kv.Value!).Select(v => StackFilters((IDictionary<string, object?>)v, tableName)).ToList(),
                    FilterType = FilterType.Or,
                },
                { Value: IDictionary<string, object?> val } => new TableFilter
                {
                    ColumnName = kv.Key!,
                    Next = StackFilters(val, null),
                    TableName = tableName,
                    FilterType = FilterType.Join,
                },
                { Value: null, Key: null } => throw new BifrostExecutionError($"Filter on {tableName} has null key and value."),
                { Key: null } => throw new BifrostExecutionError($"Filter on {tableName} has null key."),
                _ => new TableFilter
                {
                    RelationName = kv.Key,
                    Value = kv.Value,
                    FilterType = FilterType.Relation,
                },
            };
        }

        /// <summary>
        /// Identifies the column a single filter predicate targets, bundling the
        /// three loose values (<paramref name="Table"/> alias/name,
        /// <paramref name="Field"/> DB column, <paramref name="ColumnType"/> for
        /// dialect casts) that were previously threaded as separate arguments.
        /// </summary>
        internal readonly record struct FilterColumnRef(string? Table, string Field, string? ColumnType);

        /// <summary>Coarse operator families used to dispatch a single predicate.</summary>
        private enum OperatorKind { WildcardLike, RawLike, InList, Between, Comparison }

        private static OperatorKind Classify(string op) => op switch
        {
            FilterOperators.Contains or FilterOperators.NContains
                or FilterOperators.StartsWith or FilterOperators.NStartsWith
                or FilterOperators.EndsWith or FilterOperators.NEndsWith => OperatorKind.WildcardLike,
            FilterOperators.Like or FilterOperators.NLike => OperatorKind.RawLike,
            FilterOperators.In or FilterOperators.NIn => OperatorKind.InList,
            FilterOperators.Between or FilterOperators.NBetween => OperatorKind.Between,
            _ => OperatorKind.Comparison,
        };

        public static ParameterizedSql GetSingleFilterParameterized(
            ISqlDialect dialect,
            SqlParameterCollection parameters,
            string? table,
            string field,
            string op,
            object? value,
            string? columnType = null)
            => GetSingleFilterParameterized(dialect, parameters, new FilterColumnRef(table, field, columnType), op, value);

        internal static ParameterizedSql GetSingleFilterParameterized(
            ISqlDialect dialect,
            SqlParameterCollection parameters,
            FilterColumnRef column,
            string op,
            object? value)
        {
            var columnRef = column.Table == null
                ? dialect.EscapeIdentifier(column.Field)
                : $"{dialect.EscapeIdentifier(column.Table)}.{dialect.EscapeIdentifier(column.Field)}";

            // Handle NULL comparisons (no parameters needed)
            if (op == FilterOperators.Eq && value == null)
                return new ParameterizedSql($"{columnRef} IS NULL", Array.Empty<SqlParameterInfo>());
            if (op == FilterOperators.Neq && value == null)
                return new ParameterizedSql($"{columnRef} IS NOT NULL", Array.Empty<SqlParameterInfo>());

            // The schema-advertised `_null: Boolean` operator: `_null: true` tests
            // for NULL, `_null: false` for NOT NULL. It never binds a parameter and
            // must not fall through to a `column = @param` comparison.
            if (op == FilterOperators.Null)
            {
                var wantsNull = value is not bool b || b;
                return new ParameterizedSql(
                    $"{columnRef} IS {(wantsNull ? "" : "NOT ")}NULL", Array.Empty<SqlParameterInfo>());
            }

            // Handle FieldRef (column-to-column comparison, no parameters)
            if (value is FieldRef fieldRef)
            {
                var refSql = fieldRef.TableName == null
                    ? dialect.EscapeIdentifier(fieldRef.ColumnName)
                    : $"{dialect.EscapeIdentifier(fieldRef.TableName)}.{dialect.EscapeIdentifier(fieldRef.ColumnName)}";
                return new ParameterizedSql($"{columnRef} {dialect.GetOperator(op)} {refSql}", Array.Empty<SqlParameterInfo>());
            }

            return Classify(op) switch
            {
                OperatorKind.WildcardLike => BuildWildcardLike(dialect, parameters, columnRef, op, value),
                OperatorKind.RawLike => BuildRawLike(dialect, parameters, columnRef, op, value),
                OperatorKind.InList => BuildInList(dialect, parameters, columnRef, column.ColumnType, op, value),
                OperatorKind.Between => BuildBetween(dialect, parameters, columnRef, column.ColumnType, op, value),
                _ => BuildComparison(dialect, parameters, columnRef, column.ColumnType, op, value),
            };
        }

        // LIKE patterns. These operators wrap the user's VALUE in wildcards, so the
        // value itself must match literally: escape LIKE metacharacters in the bound
        // value and declare the escape character, otherwise `_contains: "100%"`
        // matches everything starting with "100" and a bare "%" matches the whole
        // table.
        private static ParameterizedSql BuildWildcardLike(ISqlDialect dialect, SqlParameterCollection parameters, string columnRef, string op, object? value)
        {
            var sqlOp = dialect.GetOperator(op);
            var patternType = op is FilterOperators.Contains or FilterOperators.NContains ? LikePatternType.Contains
                : op is FilterOperators.StartsWith or FilterOperators.NStartsWith ? LikePatternType.StartsWith
                : LikePatternType.EndsWith;
            var escapedValue = value is string s ? dialect.EscapeLikeValue(s) : value;
            var paramName = parameters.AddParameter(escapedValue);
            return new ParameterizedSql(
                $"{columnRef} {sqlOp} {dialect.LikePattern(paramName, patternType)}{dialect.LikeEscapeClause}",
                parameters.Parameters.TakeLast(1).ToList());
        }

        // _like/_nlike intentionally pass the raw pattern through — the caller owns
        // the wildcards there.
        private static ParameterizedSql BuildRawLike(ISqlDialect dialect, SqlParameterCollection parameters, string columnRef, string op, object? value)
        {
            var sqlOp = dialect.GetOperator(op);
            var paramName = parameters.AddParameter(value);
            return new ParameterizedSql($"{columnRef} {sqlOp} {paramName}",
                parameters.Parameters.TakeLast(1).ToList());
        }

        // IN clause. Each parameter is cast to the column type (Postgres: a text-bound
        // value won't compare against e.g. a date column — see CastParameterReference).
        private static ParameterizedSql BuildInList(ISqlDialect dialect, SqlParameterCollection parameters, string columnRef, string? columnType, string op, object? value)
        {
            var sqlOp = dialect.GetOperator(op);
            // Materialize once: the value is enumerated for the empty check, the parameter
            // bind, and the count. A lazy/single-use source would otherwise re-run (or, worse,
            // yield a different count for TakeLast than was bound).
            var values = ((value as IEnumerable<object?>) ?? Array.Empty<object?>()).ToList();
            // An empty list makes "col IN ()" / "col NOT IN ()" — a syntax error every
            // dialect rejects, turning a client-supplied empty array into a 500. Emit
            // the equivalent constant predicate instead: nothing is IN an empty set
            // (always false); everything is NOT IN it (always true).
            if (values.Count == 0)
                return new ParameterizedSql(op == FilterOperators.In ? "1 = 0" : "1 = 1", Array.Empty<SqlParameterInfo>());
            parameters.AddParameters(values);
            var added = parameters.Parameters.TakeLast(values.Count).ToList();
            var paramRefs = string.Join(",", added.Select(p => dialect.CastParameterReference(p.Name, columnType)));
            return new ParameterizedSql($"{columnRef} {sqlOp} ({paramRefs})", added);
        }

        private static ParameterizedSql BuildBetween(ISqlDialect dialect, SqlParameterCollection parameters, string columnRef, string? columnType, string op, object? value)
        {
            var sqlOp = dialect.GetOperator(op);
            var values = ((value as IEnumerable<object?>) ?? Array.Empty<object?>()).ToArray();
            if (values.Length < 2)
                // Fewer than two bounds cannot form a BETWEEN. Falling through to the
                // default comparison would emit `col BETWEEN @p` with the whole array
                // bound to one parameter — malformed SQL surfacing as an opaque 500.
                throw new BifrostExecutionError(
                    $"Operator '{op}' requires exactly two values (lower and upper bound); got {values.Length}.");

            var p1 = dialect.CastParameterReference(parameters.AddParameter(values[0]), columnType);
            var p2 = dialect.CastParameterReference(parameters.AddParameter(values[1]), columnType);
            return new ParameterizedSql($"{columnRef} {sqlOp} {p1} AND {p2}",
                parameters.Parameters.TakeLast(2).ToList());
        }

        // Simple comparison (default)
        private static ParameterizedSql BuildComparison(ISqlDialect dialect, SqlParameterCollection parameters, string columnRef, string? columnType, string op, object? value)
        {
            var sqlOp = dialect.GetOperator(op);
            var param = dialect.CastParameterReference(parameters.AddParameter(value), columnType);
            return new ParameterizedSql($"{columnRef} {sqlOp} {param}",
                parameters.Parameters.TakeLast(1).ToList());
        }

        /// <summary>
        /// Renders this filter as a parameterized WHERE-clause fragment for the
        /// mutation resolver path, which operates entirely in database-name
        /// space (no GraphQL-name lookup, no joins). It supports exactly the
        /// shapes mutation transformers produce on
        /// <see cref="Modules.MutationTransformResult.AdditionalFilter"/>:
        /// a single <c>column = value</c> / <c>column IS NULL</c> equality
        /// (built by <see cref="Modules.TableFilterFactory.Equals"/> /
        /// <see cref="Modules.TableFilterFactory.IsNull"/>) and an AND of such
        /// filters (built by the transformer wraps when more than one
        /// transformer contributes a filter). Any other shape — OR, joins,
        /// non-equality operators — throws <see cref="BifrostExecutionError"/>
        /// because no mutation transformer produces it today; widening the
        /// grammar is intentionally out of scope.
        /// </summary>
        public ParameterizedSql RenderForMutation(ISqlDialect dialect, SqlParameterCollection parameters)
        {
            ArgumentNullException.ThrowIfNull(dialect);
            ArgumentNullException.ThrowIfNull(parameters);

            // AND combination: every branch is itself a mutation filter.
            if (Next == null && And.Count > 0)
            {
                var rendered = And.Select(f => f.RenderForMutation(dialect, parameters)).ToArray();
                var sql = string.Join(" AND ", rendered.Select(r => $"({r.Sql})"));
                return new ParameterizedSql(sql, rendered.SelectMany(r => r.Parameters).ToList());
            }

            // Single equality: FilterType.Join with a relation Next holding the
            // operator and value, as produced by TableFilterFactory.Equals.
            if (FilterType == FilterType.Join && Next is { Next: null })
            {
                if (Next.RelationName != "_eq")
                    throw new BifrostExecutionError(
                        "Mutation additional filter only supports equality comparisons.");

                return GetSingleFilterParameterized(
                    dialect, parameters, table: null, field: ColumnName, op: "_eq", value: Next.Value);
            }

            throw new BifrostExecutionError(
                "Mutation additional filter has an unsupported shape.");
        }

        /// <summary>
        /// A rendered filter split into its FROM-clause join fragments and its
        /// WHERE predicate. Relationship filters (nested-table columns) inject an
        /// <c>INNER JOIN</c> that belongs after the table reference; leaf filters
        /// produce a WHERE predicate. Keeping them separate is what lets a filter
        /// tree mix the two — e.g. a tenant leaf ANDed onto a relationship join —
        /// without emitting the invalid <c>WHERE INNER JOIN ...</c> that a single
        /// combined fragment produced.
        /// </summary>
        internal readonly record struct FilterParts(string Joins, string Where, List<SqlParameterInfo> Parameters);

        /// <summary>
        /// Backwards-compatible single-fragment render. Leaf/AND/OR filters return
        /// their WHERE predicate; a pure relationship filter returns its join
        /// fragment; a mixed filter returns "<c>{joins} WHERE {where}</c>". Prefer
        /// <see cref="RenderParts"/> when assembling SQL so joins and predicates
        /// land in their correct clauses.
        /// </summary>
        public ParameterizedSql ToSqlParameterized(IDbModel model, ISqlDialect dialect, SqlParameterCollection parameters, string? alias = null)
        {
            var parts = RenderParts(model, dialect, parameters, alias);
            var hasWhere = !string.IsNullOrWhiteSpace(parts.Where);
            var sql = string.IsNullOrWhiteSpace(parts.Joins)
                ? parts.Where
                : hasWhere ? $"{parts.Joins} WHERE {parts.Where}" : parts.Joins;
            return new ParameterizedSql(sql, parts.Parameters);
        }

        /// <summary>Allocates unique relationship-join aliases (j0, j1, …) across a
        /// single render pass so two relationship sub-filters at the same combine
        /// level don't collide on one alias.</summary>
        private sealed class JoinAliasAllocator { private int _n; public string Next() => $"j{_n++}"; }

        internal FilterParts RenderParts(IDbModel model, ISqlDialect dialect, SqlParameterCollection parameters, string? alias)
            => RenderParts(new SqlBuildContext(model, dialect, parameters), alias, new JoinAliasAllocator());

        private FilterParts RenderParts(SqlBuildContext ctx, string? alias, JoinAliasAllocator aliases)
        {
            var dialect = ctx.Dialect;
            var parameters = ctx.Parameters;

            // The table-scoped _search operator lowers to a dialect full-text predicate
            // over the table's validated searchable columns — not a per-column leaf.
            if (FilterType == FilterType.Search)
                return RenderSearchParts(ctx, alias);

            if (Next == null)
            {
                if (And.Count > 0) return CombineParts(And, "AND", ctx, alias, aliases);
                if (Or.Count > 0) return CombineParts(Or, "OR", ctx, alias, aliases);
                throw new BifrostExecutionError("Filter object missing all required fields.");
            }

            var table = ctx.Model.GetTableFromDbName(TableName ?? throw new BifrostExecutionError("TableFilter with undefined TableName"));
            // A leaf predicate is `column: { _op: value }` — `Next` is the terminal
            // operator (Relation) node. A relationship sub-filter instead has a nested
            // column (`Join`) or an implicit/explicit AND/OR wrapper as `Next`, and must
            // route to the join branch below. Keying on `Next.Next == null` misrouted the
            // AND-wrapper produced by sibling relationship predicates
            // (`posts: { published: {_eq:true}, featured: {_eq:true} }`) — its `Next` is
            // null — into the leaf path, where the relationship name ("posts") was looked
            // up as a column and threw "unknown column". Key on the node type instead,
            // via the shared predicate the column guards also use.
            if (IsLeafColumnPredicate)
            {
                // Resolve the column tolerant of both name spaces: user filters key
                // by GraphQL name, but security transformers (tenant, soft-delete)
                // build filters keyed by the raw DB column name. A GraphQlLookup-only
                // lookup threw KeyNotFoundException whenever the two names differ.
                var column = table.GraphQlLookup.TryGetValue(ColumnName, out var byGraphQl) ? byGraphQl
                    : table.ColumnLookup.TryGetValue(ColumnName, out var byDb) ? byDb
                    : throw new BifrostExecutionError(
                        $"Filter references unknown column '{ColumnName}' on table '{TableName}'.");
                var leaf = GetSingleFilterParameterized(dialect, parameters, alias ?? TableName, column.DbName, Next.RelationName, Next.Value, column.DataType);
                return new FilterParts("", leaf.Sql, leaf.Parameters.ToList());
            }

            // Relationship filter: the nested-column predicate lives inside the
            // joined sub-query, so this contributes an INNER JOIN fragment (for
            // the FROM clause) and no WHERE predicate of its own. Each join gets a
            // unique alias so sibling relationship filters at one AND level don't
            // both emit `[j]` (a duplicate-alias syntax error).
            // Tolerate an unknown/relationship-typed column with a clear error rather
            // than a raw KeyNotFoundException 500. A multi-link (one-to-many) target is
            // not filterable through this single-link INNER JOIN path.
            if (!table.SingleLinks.TryGetValue(ColumnName, out var link))
            {
                var hint = table.MultiLinks.ContainsKey(ColumnName)
                    ? " It is a one-to-many relationship, which cannot be used as a single-link filter target."
                    : "";
                throw new BifrostExecutionError(
                    $"Filter references unknown single-link relationship '{ColumnName}' on table '{TableName}'.{hint}");
            }
            var (joinSql, joinParams) = BuildSqlParameterized(Next, link, ctx, aliases, TraversedTableFilter, includeValue: false);
            var ej = dialect.EscapeIdentifier(aliases.Next());
            var fullJoin = $" INNER JOIN ({joinSql}) {ej} ON {ej}.{dialect.EscapeIdentifier("joinid")} = {dialect.EscapeIdentifier(alias ?? table.DbName)}.{dialect.EscapeIdentifier(link.ChildId.ColumnName)}";
            return new FilterParts(fullJoin, "", joinParams.ToList());
        }

        /// <summary>
        /// Lowers a <see cref="FilterType.Search"/> node to its dialect full-text predicate.
        /// Columns come only from the table's validated <c>FtsConfig</c> (schema-derived,
        /// never client input); the query string is tokenized and each term's value is
        /// bound as a parameter by the dialect. The predicate is wrapped in parentheses so
        /// that when it is ANDed with the security filters (tenant/policy in band 0-99,
        /// soft-delete in band 100-199) it can never bind more loosely than they do — an
        /// unwrapped <c>a MATCH x OR b MATCH y</c> ANDed with <c>tenant = @t</c> would leak
        /// across tenants.
        /// </summary>
        private FilterParts RenderSearchParts(SqlBuildContext ctx, string? alias)
        {
            var dialect = ctx.Dialect;
            var table = ctx.Model.GetTableFromDbName(
                TableName ?? throw new BifrostExecutionError("Search filter with undefined TableName"));

            var fts = Modules.Fts.FtsConfig.FromTable(table);
            if (!fts.IsSearchable)
                throw new BifrostExecutionError(
                    $"Table '{table.DbName}' declares no searchable columns ('search' metadata); " +
                    "the _search operator is not available on it.");

            var terms = FtsQueryParser.Parse(Value as string);
            // An empty/whitespace search contributes no predicate, so it composes
            // harmlessly with the security filters rather than matching all-or-nothing.
            if (terms.Count == 0)
                return new FilterParts("", "", new List<SqlParameterInfo>());

            var request = new FtsPredicateRequest(
                TableAlias: alias ?? table.DbName,
                TableSchema: table.TableSchema,
                TableName: table.DbName,
                ColumnNames: fts.SearchColumns,
                KeyColumnNames: table.KeyColumns.Select(c => c.ColumnName).ToList(),
                Terms: terms,
                Language: fts.Language,
                Parameters: ctx.Parameters);

            var predicate = dialect.SearchPredicate(request);
            return new FilterParts("", $"({predicate.Sql})", predicate.Parameters.ToList());
        }

        private FilterParts CombineParts(List<TableFilter> children, string op, SqlBuildContext ctx, string? alias, JoinAliasAllocator aliases)
        {
            var rendered = children.Select(f => f.RenderParts(ctx, alias, aliases)).ToList();

            // A relationship sub-filter contributes an INNER JOIN, which narrows
            // (AND semantics). ORing it with other branches can't be expressed by
            // concatenating joins — doing so silently drops the OR and returns
            // AND'd rows. Reject it rather than return wrong data; OR over
            // relationship filters needs EXISTS/subquery support that does not
            // exist yet.
            if (op == "OR" && rendered.Count > 1 && rendered.Any(r => !string.IsNullOrWhiteSpace(r.Joins)))
                throw new BifrostExecutionError(
                    "OR over relationship (nested-table) filters is not supported.");

            var joins = string.Concat(rendered.Select(r => r.Joins));
            var wheres = rendered.Where(r => !string.IsNullOrWhiteSpace(r.Where)).Select(r => r.Where).ToArray();
            var where = wheres.Length switch
            {
                0 => "",
                1 => wheres[0],
                _ => $"(({string.Join($") {op} (", wheres)}))",
            };
            return new FilterParts(joins, where, rendered.SelectMany(r => r.Parameters).ToList());
        }

        /// <summary>
        /// Resolves a filter column against a relationship's parent table, tolerant of
        /// both name spaces exactly like the leaf <see cref="RenderParts"/> path: user
        /// filters key by GraphQL name, security transformers by raw DB name. Returns
        /// the resolved column so callers use its <c>DbName</c> in SQL (renamed columns
        /// broke when the raw GraphQL name was emitted) and its <c>DataType</c> for
        /// dialect casts (a DB-name-keyed lookup on a GraphQL name missed the type,
        /// e.g. skipping Postgres <c>::date</c> casts).
        /// </summary>
        private static ColumnDto ResolveRelationshipColumn(TableLinkDto link, string columnName)
        {
            var parent = link.ParentTable;
            if (parent.GraphQlLookup.TryGetValue(columnName, out var byGraphQl))
                return byGraphQl;
            if (parent.ColumnLookup.TryGetValue(columnName, out var byDb))
                return byDb;
            throw new BifrostExecutionError(
                $"Relationship filter references unknown column '{columnName}' on table '{parent.DbName}'.");
        }

        /// <summary>
        /// Builds the shared <c>SELECT DISTINCT {ParentId} AS joinid [, {value}] FROM
        /// {parentTableRef} {tail}</c> skeleton every relationship sub-query starts
        /// from. Schema-qualifies the FROM so non-default-schema parents resolve to
        /// the right table; column references stay table-name-qualified because a
        /// schema-qualified FROM without an alias still exposes the bare table name in
        /// every supported dialect. <paramref name="valueProjection"/> (already
        /// including its leading comma) adds the optional value column for
        /// value-returning contexts; <paramref name="joins"/> carries any
        /// <c>INNER JOIN …</c> fragments (empty for none) and <paramref name="wheres"/>
        /// the predicates ANDed into the sub-query's WHERE.
        ///
        /// Joins and WHERE are assembled here rather than by the callers so that the
        /// traversed table's security predicate cannot be dropped by a branch that
        /// happens to build only a join — every branch goes through one assembler.
        /// </summary>
        private static string RelationshipSubquery(
            TableLinkDto link, ISqlDialect dialect, string joins, IReadOnlyList<string> wheres, string valueProjection = "")
        {
            var ejoinid = dialect.EscapeIdentifier("joinid");
            var parentTableRef = dialect.TableReference(link.ParentTable.TableSchema, link.ParentTable.DbName);
            var joinPart = string.IsNullOrWhiteSpace(joins) ? "" : " " + joins.Trim();
            var kept = wheres.Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
            var wherePart = kept.Count == 0 ? "" : " WHERE " + string.Join(" AND ", kept.Select(w => $"({w})"));
            return $"SELECT DISTINCT {dialect.EscapeIdentifier(link.ParentId.ColumnName)} AS {ejoinid}{valueProjection} FROM {parentTableRef}{joinPart}{wherePart}";
        }

        /// <summary>
        /// Renders the traversed parent table's own transformer filter
        /// (<see cref="TraversedTableFilter"/>) as a fragment for the relationship
        /// sub-query. The filter is evaluated against the PARENT table, aliased by its
        /// bare table name to match how <see cref="RelationshipSubquery"/> qualifies
        /// columns in an unaliased FROM. A transformer may itself emit a
        /// relationship-shaped filter, so joins are returned alongside the predicate
        /// rather than assumed away.
        /// </summary>
        private static FilterParts RenderTraversedTableFilter(
            TableFilter? traversedFilter, TableLinkDto link, SqlBuildContext ctx, JoinAliasAllocator aliases)
            => traversedFilter == null
                ? new FilterParts("", "", new List<SqlParameterInfo>())
                : traversedFilter.RenderParts(ctx, link.ParentTable.DbName, aliases);

        private static (string sql, List<SqlParameterInfo> parameters) BuildSqlParameterized(
            TableFilter filter,
            TableLinkDto link,
            SqlBuildContext ctx,
            JoinAliasAllocator aliases,
            TableFilter? traversedTableFilter,
            bool includeValue = false)
        {
            var dialect = ctx.Dialect;
            var parameters = ctx.Parameters;
            var scope = RenderTraversedTableFilter(traversedTableFilter, link, ctx, aliases);
            if (filter is { Next: { } } || (filter.Next == null && filter.And.Count > 0) || (filter.Next == null && filter.Or.Count > 0))
            {
                var ej = dialect.EscapeIdentifier("j");
                var ejoinid = dialect.EscapeIdentifier("joinid");
                var evalue = dialect.EscapeIdentifier("value");

                // Multiple sibling predicates on one relationship form an implicit AND
                // (`posts: { published: {_eq:true}, featured: {_eq:true} }`) or an explicit
                // `and` block. Combine them into ONE subquery WHERE so every predicate
                // constrains the relationship, instead of dropping into the leaf path
                // (which resolved the relationship name as a column and threw
                // "unknown column"). OR across relationship predicates is not expressible
                // as a single narrowing join and still falls through to the shape error.
                if (filter.Next == null && filter.And.Count > 0)
                {
                    if (includeValue)
                        throw new BifrostExecutionError(
                            $"Relationship filter on '{link.ChildTable.DbName}' via link '{link.Name}' " +
                            "cannot combine multiple predicates in a value-returning context.");
                    var pred = BuildRelationshipLeafPredicate(filter, link, dialect, parameters);
                    var combined = RelationshipSubquery(link, dialect, scope.Joins, new[] { pred.Sql, scope.Where });
                    return (combined, pred.Parameters.Concat(scope.Parameters).ToList());
                }

                switch (filter.FilterType)
                {
                    case FilterType.Join
                        when link.ParentTable.SingleLinks.TryGetValue(filter.ColumnName, out var nextLink):
                        {
                            // Each hop carries its OWN traversed table's scope; the next
                            // hop's comes from the nested node, not from this one.
                            var (nextSql, nextParams) = BuildSqlParameterized(
                                filter.Next!, nextLink, ctx, aliases, filter.Next!.TraversedTableFilter);
                            var innerJoin = $"INNER JOIN ({nextSql}) {ej} ON {ej}.{ejoinid} = {dialect.EscapeIdentifier(link.ParentTable.DbName)}.{dialect.EscapeIdentifier(nextLink.ChildId.ColumnName)}";
                            var sql = RelationshipSubquery(
                                link, dialect, $"{innerJoin}{scope.Joins}", new[] { scope.Where },
                                valueProjection: includeValue ? $", {evalue}" : "");
                            return (sql, nextParams.Concat(scope.Parameters).ToList());
                        }
                    case FilterType.Join:
                        // Map the GraphQL column name to its DB name (and pick up its
                        // DataType) so renamed columns emit the real identifier and get
                        // dialect casts — the DB-name-keyed ColumnLookup missed both.
                        var parentColumn = ResolveRelationshipColumn(link, filter.ColumnName);
                        if (includeValue)
                        {
                            return (
                                RelationshipSubquery(link, dialect, scope.Joins, new[] { scope.Where },
                                    valueProjection: $", {dialect.EscapeIdentifier(parentColumn.DbName)} AS {evalue}"),
                                scope.Parameters.ToList());
                        }
                        else
                        {
                            var filterResult = GetSingleFilterParameterized(dialect, parameters, link.ParentTable.DbName, parentColumn.DbName, filter.Next!.RelationName, filter.Next.Value, parentColumn.DataType);
                            return (
                                RelationshipSubquery(link, dialect, scope.Joins, new[] { filterResult.Sql, scope.Where }),
                                filterResult.Parameters.Concat(scope.Parameters).ToList());
                        }
                }
            }

            // No branch produced a sub-query. Returning ("", empty) here let the
            // caller splice an empty parenthesis into `INNER JOIN () ...`, a syntax
            // error surfacing as an opaque 500. Fail loudly with the shape instead,
            // mirroring the guards above.
            throw new BifrostExecutionError(
                $"Relationship filter on '{link.ChildTable.DbName}' via link '{link.Name}' " +
                $"has an unsupported shape (filter type '{filter.FilterType}') and cannot be rendered.");
        }

        /// <summary>
        /// Renders one sibling predicate of a relationship's implicit/explicit AND as a
        /// WHERE fragment evaluated against the relationship's (parent) table, so several
        /// predicates can be combined into a single relationship subquery. Only plain
        /// column comparisons and nested ANDs of them are supported; an OR block or a
        /// further nested relationship on the same relationship throws a clear shape error
        /// rather than silently dropping a constraint.
        /// </summary>
        private static ParameterizedSql BuildRelationshipLeafPredicate(
            TableFilter filter, TableLinkDto link, ISqlDialect dialect, SqlParameterCollection parameters)
        {
            if (filter.Next == null && filter.And.Count > 0)
            {
                var predicates = new List<string>();
                var combinedParams = new List<SqlParameterInfo>();
                foreach (var child in filter.And)
                {
                    var childPred = BuildRelationshipLeafPredicate(child, link, dialect, parameters);
                    predicates.Add($"({childPred.Sql})");
                    combinedParams.AddRange(childPred.Parameters);
                }
                return new ParameterizedSql(string.Join(" AND ", predicates), combinedParams);
            }

            if (filter.FilterType == FilterType.Join && filter.Next is { Next: null, FilterType: FilterType.Relation } rel)
            {
                // Map GraphQL name -> DB name and carry the DataType so renamed columns
                // render correctly and receive dialect casts (see ResolveRelationshipColumn).
                var parentColumn = ResolveRelationshipColumn(link, filter.ColumnName);
                return GetSingleFilterParameterized(
                    dialect, parameters, link.ParentTable.DbName, parentColumn.DbName, rel.RelationName, rel.Value, parentColumn.DataType);
            }

            throw new BifrostExecutionError(
                $"Relationship filter on '{link.ChildTable.DbName}' via link '{link.Name}' combines multiple predicates " +
                $"but one has an unsupported shape (filter type '{filter.FilterType}'); only column comparisons and nested AND are supported.");
        }

    }
}
