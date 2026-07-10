using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;

namespace BifrostQL.Core.QueryModel
{
    public enum QueryType
    {
        Standard = 0,
        Join = 1,
        Single = 2,
        Aggregate = 3,
    }

    public sealed class GqlObjectQuery
    {
        public GqlObjectQuery() { }
        //public TableJoin? JoinFrom { get; set; }
        public IDbTable DbTable { get; init; } = null!;
        public string SchemaName { get; set; } = "";
        public string TableName { get; set; } = "";
        public string FieldName { get; set; } = "";
        public string GraphQlName { get; set; } = "";
        public string? Alias { get; set; }
        public string Path { get; set; } = "";
        public string KeyName => $"{Alias ?? GraphQlName}";
        public QueryType QueryType { get; set; }
        public List<GqlObjectColumn> ScalarColumns { get; init; } = new();
        public List<GqlAggregateColumn> AggregateColumns { get; init; } = new();

        /// <summary>
        /// When set, this node is a dedicated base-table GROUP BY aggregate query
        /// (<c>&lt;table&gt;Aggregate</c> root field). <see cref="AddSqlParameterized"/>
        /// emits a single grouped statement instead of the row SELECT/count/join set.
        /// </summary>
        public GroupedAggregate? GroupedAggregate { get; set; }

        public List<GqlObjectQuery> Links { get; set; } = new();
        public List<string> Sort { get; set; } = new();
        public TableFilter? Filter { get; set; }
        public int? Limit { get; set; }
        public int? Offset { get; set; }
        public bool IsFragment { get; set; }
        public bool IncludeResult { get; set; }

        /// <summary>
        /// Module query-argument values (keyed by module context key, e.g.
        /// <c>include_deleted</c>) captured off this node's GraphQL field
        /// arguments. Threaded into the user context under table-scoped keys by
        /// <c>QueryTransformerService</c> so module filter transformers honor
        /// them per node — on nested join fields, not just the root field.
        /// </summary>
        public IReadOnlyDictionary<string, object?> ModuleQueryArguments { get; set; } =
            Modules.ModuleApiRegistry.EmptyArguments;
        public List<TableJoin> Joins { get; set; } = new();
        public IEnumerable<TableJoin> RecurseJoins => Joins.Concat(Joins.SelectMany(j => j.ConnectedTable.RecurseJoins));

        public IEnumerable<GqlObjectColumn> FullColumnNames =>
            ScalarColumns
            .Where(c => c.GraphQlDbName.StartsWith("__") == false && !c.IsProviderComputed)
            .Concat(ScalarColumns
                .Where(c => c.IsProviderComputed)
                .SelectMany(c => ProviderDependencies(c).Select(d => new GqlObjectColumn(d))))
            .Concat(Joins.Select(j => new GqlObjectColumn(j.FromColumn)))
            .DistinctBy(c => c.GraphQlDbName, SqlNameComparer.Instance);

        private IEnumerable<string> ProviderDependencies(GqlObjectColumn column)
        {
            var dependencies = column.ComputedColumn!.Dependencies;
            if (dependencies.Count == 0)
                return DbTable.KeyColumns.Select(c => c.DbName);

            return dependencies.Select(d => Modules.ComputedColumns.ComputedColumnDefinition.ResolveDependencyColumn(DbTable, d));
        }

        /// <summary>
        /// Translates GraphQL sort tokens (e.g. "name_asc", "id_desc") into dialect-escaped
        /// ORDER BY column expressions. Centralizes the `_asc`/`_desc` suffix parsing and
        /// identifier escaping used by every pagination call site below, optionally
        /// qualifying each column with an escaped alias prefix (e.g. "b" -> `b`.`col`).
        /// The token's column part is the GraphQL column name; it is mapped back to the
        /// database column name via <paramref name="table"/> before escaping, so a column
        /// whose GraphQL name differs from its DB name (e.g. "Order Date" -> "Order_Date")
        /// sorts correctly in nested/paged/restricted paths, not only where a projection
        /// alias happens to cover it.
        /// </summary>
        private static IEnumerable<string>? RenderSortColumns(ISqlDialect dialect, IDbTable? table, IReadOnlyList<string> sort, string? alias = null)
        {
            if (!sort.Any())
                return null;

            var prefix = alias == null ? "" : $"{dialect.EscapeIdentifier(alias)}.";
            return sort.Select(s =>
            {
                var (graphQlName, direction) = s switch
                {
                    { } when s.EndsWith("_asc") => (s[..^4], "asc"),
                    { } when s.EndsWith("_desc") => (s[..^5], "desc"),
                    _ => throw new BifrostExecutionError($"Unsupported sort token '{s}'; expected suffix '_asc' or '_desc'.")
                };
                // Map the GraphQL column name back to its DB name. When the node has no
                // resolved table, or the token is not a mapped scalar column (e.g. a
                // projection alias), leave it as-is — the prior behavior.
                var dbName = graphQlName;
                if (table != null && table.GraphQlLookup.TryGetValue(graphQlName, out var col))
                    dbName = col.DbName;
                return $"{prefix}{dialect.EscapeIdentifier(dbName)} {direction}";
            });
        }

        /// <summary>
        /// Fails fast when an aggregate selection cannot be correlated back to its
        /// parent rows. A keyless table (view / no PK) has nothing to correlate on;
        /// a composite PK would silently group by only the first key column and hand
        /// every row a value aggregated across all rows sharing that column — wrong
        /// data, not an error. Both cases are deferred features, so throw a clear
        /// error instead of the opaque KeyNotFoundException / "Sequence contains no
        /// elements" (or silently-wrong data) the execution path would produce.
        /// </summary>
        private void ValidateAggregateKeying()
        {
            if (AggregateColumns.Count == 0)
                return;

            if (!DbTable.KeyColumns.Any())
                throw new BifrostExecutionError(
                    $"Aggregate queries require a primary key on table '{GraphQlName}'; " +
                    $"table '{TableName}' has no primary key, so aggregate results cannot be correlated to its rows.");

            if (DbTable.KeyColumns.Skip(1).Any())
                throw new BifrostExecutionError(
                    $"Aggregate queries on composite-primary-key table '{GraphQlName}' are not supported; " +
                    "aggregate correlation uses a single key column and would produce incorrect results.");
        }

        /// <summary>
        /// Ensures the base SELECT projects every column ReaderEnum needs to correlate
        /// aggregate rows. Aggregate correlation matches each aggregate row back to its
        /// parent row by the column the aggregate joined on (ParentKeyColumnDbName —
        /// the parent PK for a OneToMany first hop, the child FK for a ManyToOne first
        /// hop). That column MUST be in the SELECT even when the user selected only
        /// non-key scalars (`data { name _agg {...} }`) or nothing at all
        /// (`data { _agg {...} }`) — otherwise ReaderEnum threw KeyNotFoundException
        /// probing a column that was never projected. Also project the key column(s)
        /// so an aggregate-only selection still emits a base result set with its row
        /// identity. Link-less aggregates are skipped here: they are rejected later by
        /// GqlAggregateColumn.ToSqlParameterized with a clearer message, and probing
        /// ParentKeyColumnDbName would pre-empt it.
        /// </summary>
        private List<GqlObjectColumn> AppendAggregateKeyColumns(List<GqlObjectColumn> fullColumns)
        {
            if (AggregateColumns.Count == 0)
                return fullColumns;

            var neededDbNames = DbTable.KeyColumns.Select(c => c.DbName)
                .Concat(AggregateColumns.Where(a => a.Links.Count > 0).Select(a => a.ParentKeyColumnDbName));
            return fullColumns
                .Concat(neededDbNames.Select(n => new GqlObjectColumn(n)))
                .DistinctBy(c => c.GraphQlDbName, SqlNameComparer.Instance)
                .ToList();
        }

        public void AddSqlParameterized(IDbModel dbModel, ISqlDialect dialect, IDictionary<string, ParameterizedSql> sqls, SqlParameterCollection parameters, QueryLink? queryLink = null)
        {
            // A grouped base-table aggregate emits ONE statement (no row SELECT,
            // count, nested _agg, or joins). Its WHERE is this node's Filter, which
            // the transformer service has already augmented with tenant/soft-delete
            // scope, so grouping runs over the caller-visible rows only.
            if (GroupedAggregate is { } grouped)
            {
                var aggFilter = GetFilterSqlParameterized(dbModel, dialect, parameters);
                var aggTableRef = dialect.TableReference(SchemaName, TableName);
                sqls[KeyName] = grouped.ToSqlParameterized(dialect, aggTableRef, aggFilter);
                return;
            }

            ValidateAggregateKeying();

            var fullColumns = AppendAggregateKeyColumns(FullColumnNames.ToList());
            var tableRef = dialect.TableReference(SchemaName, TableName);

            var filter = GetFilterSqlParameterized(dbModel, dialect, parameters);
            var sqlKeyName = queryLink == null ? KeyName : queryLink.Join.JoinName;

            if (fullColumns.Count > 0)
            {
                var columnSql = string.Join(",", fullColumns.Select(n => n.ToSelectSql(dbModel, DbTable, dialect)));
                var cmdText = $"SELECT {columnSql} FROM {tableRef}";

                var sortCols = RenderSortColumns(dialect, DbTable, Sort);
                var pagination = dialect.Pagination(sortCols, Offset, Limit);

                var baseSql = new ParameterizedSql(cmdText, Array.Empty<SqlParameterInfo>())
                    .Append(filter)
                    .Append(pagination);

                sqls[sqlKeyName] = baseSql;
            }

            if (IncludeResult)
            {
                var countSql = $"SELECT COUNT(*) FROM {tableRef}";
                sqls[$"{sqlKeyName}=>count"] = new ParameterizedSql(countSql, Array.Empty<SqlParameterInfo>()).Append(filter);
            }

            foreach (var col in AggregateColumns)
            {
                var aggregateSql = col.ToSqlParameterized(dbModel, dialect, parameters, filter);
                col.SqlKey = $"{sqlKeyName}=>agg_{col.FinalColumnGraphQlName}";
                sqls[col.SqlKey] = aggregateSql;
            }

            var ctx = new SqlBuildContext(dbModel, dialect, parameters);
            foreach (var join in Joins)
            {
                var joinQueryLink = new QueryLink(join, this, queryLink);
                AddJoinSqlParameterized(ctx, sqls, joinQueryLink);
            }
        }

        private static void AddJoinSqlParameterized(SqlBuildContext ctx, IDictionary<string, ParameterizedSql> sqls, QueryLink queryLink)
        {
            var main = GetRestrictedSqlParameterized(ctx.Model, ctx.Dialect, ctx.Parameters, queryLink);
            var sql = ToConnectedSqlParameterized(ctx.Model, ctx.Dialect, ctx.Parameters, main, queryLink.Join);
            sqls[queryLink.Join.JoinName] = sql;

            foreach (var join in queryLink.Join.ConnectedTable.Joins)
            {
                var joinQueryLink = new QueryLink(join, queryLink.Join.ConnectedTable, queryLink);
                AddJoinSqlParameterized(ctx, sqls, joinQueryLink);
            }
        }

        public static ParameterizedSql ToConnectedSqlParameterized(IDbModel dbModel, ISqlDialect dialect, SqlParameterCollection parameters, ParameterizedSql main, TableJoin tableJoin)
        {
            var ctx = new SqlBuildContext(dbModel, dialect, parameters);
            var connectedDbTable = dbModel.GetTableFromDbName(tableJoin.ConnectedTable.TableName);
            var joinColumnSql = string.Join(",",
                tableJoin.ConnectedTable.FullColumnNames.Select(c => c.ToSelectSql(dbModel, connectedDbTable, dialect, "b", useAsKeyword: true)));

            var srcProjection = tableJoin.EmitSrcProjection(dialect, "a");
            // The connected table may contribute no columns of its own (e.g. a
            // polymorphic/child collection selected with only the relationship,
            // no scalar fields). Appending ", {empty}" produced "src_id, FROM"
            // → "Incorrect syntax near ','". Only join the child projection when
            // it has content.
            var projection = string.IsNullOrEmpty(joinColumnSql)
                ? srcProjection
                : $"{srcProjection}, {joinColumnSql}";

            // Schema-qualify the joined tables. The root FROM already qualifies via
            // TableReference; an unqualified join target resolves against the
            // connection's default schema — an invalid-object error for tables in
            // other schemas, or worse, a silent read of a same-named default-schema
            // table.
            var connectedTableRef = dialect.TableReference(connectedDbTable.TableSchema, tableJoin.ConnectedTable.TableName);

            string fromClause;
            IReadOnlyList<SqlParameterInfo> relationParams;
            if (tableJoin.Bridge is { } bridge)
            {
                fromClause = BuildBridgeFromClause(ctx, tableJoin, bridge, main, connectedTableRef);
                relationParams = Array.Empty<SqlParameterInfo>();
            }
            else
            {
                var ea = dialect.EscapeIdentifier("a");
                var eb = dialect.EscapeIdentifier("b");
                var (relationSql, rp) = tableJoin.EmitOnClause(dialect, parameters, "a", "b");
                relationParams = rp;
                fromClause = $"FROM ({main.Sql}) {ea}"
                    + $" INNER JOIN {connectedTableRef} {eb} ON {relationSql}";
            }
            var wrap = $"SELECT {projection} {fromClause}";

            // Transformer-derived filters (tenant isolation, soft-delete, policy
            // row scope) land on ConnectedTable.Filter and must constrain EVERY
            // join type. Single links (forward FK / _single) previously returned
            // before the filter was applied, silently exposing soft-deleted,
            // cross-tenant, and policy-scoped-out parent rows. Fail-closed: a
            // filtered-out parent simply has no matching row, so the field
            // resolves null.
            var filter = tableJoin.ConnectedTable.GetFilterSqlParameterized(dbModel, dialect, parameters, "b");

            // Dispatch on join mode: a single-row link, a per-parent paged
            // collection, or a flat (non-paged) multi-row collection.
            if (tableJoin.QueryType == QueryType.Single)
                return BuildSingleJoinSql(wrap, main, relationParams, filter);

            if (tableJoin.ConnectedTable.IncludeResult)
                return BuildPagedCollectionSql(ctx, tableJoin, projection, fromClause, filter, main, relationParams);

            return BuildFlatCollectionSql(ctx, tableJoin, wrap, filter, main, relationParams);
        }

        /// <summary>
        /// Many-to-many FROM: bridge source -> junction -> target. src_id stays the
        /// source key (a.JoinId), so the collection is keyed and window-paged per
        /// source parent just like a multi-link.
        /// </summary>
        private static string BuildBridgeFromClause(SqlBuildContext ctx, TableJoin tableJoin, JunctionBridge bridge, ParameterizedSql main, string connectedTableRef)
        {
            var dialect = ctx.Dialect;
            var ea = dialect.EscapeIdentifier("a");
            var eb = dialect.EscapeIdentifier("b");
            var ej = dialect.EscapeIdentifier("j");
            var srcOnClause = string.Join(" AND ", Enumerable.Range(0, tableJoin.FromColumns.Count)
                .Select(i => $"{ea}.{dialect.EscapeIdentifier(JoinKeyNames.JoinIdAt(i, tableJoin.FromColumns.Count))} = {ej}.{dialect.EscapeIdentifier(bridge.JunctionSourceColumn)}"));
            var tgtOnClause = $"{ej}.{dialect.EscapeIdentifier(bridge.JunctionTargetColumn)} = {eb}.{dialect.EscapeIdentifier(tableJoin.ConnectedColumn)}";
            return $"FROM ({main.Sql}) {ea}"
                + $" INNER JOIN {dialect.TableReference(bridge.JunctionSchema, bridge.JunctionTable)} {ej} ON {srcOnClause}"
                + $" INNER JOIN {connectedTableRef} {eb} ON {tgtOnClause}";
        }

        /// <summary>Single-row link (forward FK / _single): no pagination, filter only.</summary>
        private static ParameterizedSql BuildSingleJoinSql(string wrap, ParameterizedSql main, IReadOnlyList<SqlParameterInfo> relationParams, ParameterizedSql filter)
            => new ParameterizedSql(wrap, main.Parameters.Concat(relationParams).ToList())
                .Append(filter);

        /// <summary>
        /// Per-parent paged collection: compute a window partitioned by the parent
        /// join-id columns so each parent gets its own row-number and total, then
        /// filter on the row number. This keeps parent A's limit from consuming
        /// parent B's rows — a flat global LIMIT cannot do that. Partition by the
        /// parent key columns as exposed by the inner `a` sub-query (JoinId /
        /// JoinId_&lt;i&gt;); order by the child sort columns against the `b` alias —
        /// both valid at the join level where the window is computed.
        /// </summary>
        private static ParameterizedSql BuildPagedCollectionSql(SqlBuildContext ctx, TableJoin tableJoin, string projection, string fromClause, ParameterizedSql filter, ParameterizedSql main, IReadOnlyList<SqlParameterInfo> relationParams)
        {
            var dialect = ctx.Dialect;
            var ea = dialect.EscapeIdentifier("a");
            var srcCount = tableJoin.FromColumns.Count;
            var partitionCols = Enumerable.Range(0, srcCount)
                .Select(i => $"{ea}.{dialect.EscapeIdentifier(JoinKeyNames.JoinIdAt(i, srcCount))}");
            var windowOrder = RenderSortColumns(dialect, tableJoin.ConnectedTable.DbTable, tableJoin.ConnectedTable.Sort, "b");

            var pagedSql = dialect.ConnectedPaging(
                projection,
                fromClause + filter.Sql,
                partitionCols,
                windowOrder,
                PagedKeys.RowNumber,
                PagedKeys.Total,
                tableJoin.ConnectedTable.Offset,
                tableJoin.ConnectedTable.Limit);

            return new ParameterizedSql(pagedSql, main.Parameters.Concat(relationParams).Concat(filter.Parameters).ToList());
        }

        /// <summary>
        /// Non-paged multi-row join (explicit `_join_`, and non-IncludeResult
        /// collections). A LIMIT here is GLOBAL across every parent's joined rows,
        /// so a DEFAULT limit would silently drop matched rows once the combined
        /// child count crossed it (dialect.Pagination defaults null -> LIMIT 100).
        /// Per-parent windowing is reserved for the IncludeResult paged path (it
        /// needs __rn/__total columns the flat reader cannot consume). So apply only
        /// an EXPLICITLY requested limit/offset — documented as global — and
        /// otherwise emit NO limit (the -1 sentinel) so every parent keeps all its
        /// matched rows instead of being silently truncated at 100.
        /// </summary>
        private static ParameterizedSql BuildFlatCollectionSql(SqlBuildContext ctx, TableJoin tableJoin, string wrap, ParameterizedSql filter, ParameterizedSql main, IReadOnlyList<SqlParameterInfo> relationParams)
        {
            var dialect = ctx.Dialect;
            var effectiveLimit = tableJoin.ConnectedTable.Limit ?? -1;
            var sortCols = RenderSortColumns(dialect, tableJoin.ConnectedTable.DbTable, tableJoin.ConnectedTable.Sort);
            var pagination = dialect.Pagination(sortCols, tableJoin.ConnectedTable.Offset, effectiveLimit);

            return new ParameterizedSql(wrap, main.Parameters.Concat(relationParams).ToList())
                .Append(filter)
                .Append(pagination);
        }

        public static ParameterizedSql GetRestrictedSqlParameterized(IDbModel dbModel, ISqlDialect dialect, SqlParameterCollection parameters, QueryLink query)
        {
            if (query.Parent == null)
            {
                var filter = query.FromTable.GetFilterSqlParameterized(dbModel, dialect, parameters);
                var projection = query.Join.EmitJoinIdProjection(dialect);
                var tableRef = dialect.TableReference(query.FromTable.SchemaName, query.FromTable.TableName);

                // Skip pagination when the parent is unbounded — `Limit == -1` is the
                // explicit "no limit" sentinel and `Offset` of 0 (or null) means "from
                // the start", so the linked sub-query already matches the parent universe.
                var hasOffset = query.FromTable.Offset.HasValue && query.FromTable.Offset.Value > 0;
                var hasLimit = query.FromTable.Limit.HasValue && query.FromTable.Limit.Value > 0;
                if (!(hasOffset || hasLimit))
                {
                    var sqlText = $"SELECT DISTINCT {projection} FROM {tableRef}";
                    return new ParameterizedSql(sqlText, Array.Empty<SqlParameterInfo>()).Append(filter);
                }

                // Forward the parent table's pagination into the linked sub-query so the
                // joined rows stay aligned with the paged parent set. Page the parent rows
                // in an inner (non-DISTINCT) query ordered by the parent's sort, then
                // DISTINCT the join-id columns in an outer wrap.
                //
                // The inner query must NOT be DISTINCT: `SELECT DISTINCT {join-ids} ...
                // ORDER BY {pk}` is rejected by SQL Server because the sort (pk) columns
                // aren't in the DISTINCT projection. A non-DISTINCT inner can ORDER BY any
                // column, and the outer wrap then de-duplicates the join-id set.
                var sortCols = RenderSortColumns(dialect, query.FromTable.DbTable, query.FromTable.Sort);
                var pagination = dialect.Pagination(sortCols, query.FromTable.Offset, query.FromTable.Limit);

                var inner = new ParameterizedSql($"SELECT {projection} FROM {tableRef}", Array.Empty<SqlParameterInfo>())
                    .Append(filter)
                    .Append(pagination);

                var joinIdNames = string.Join(", ", Enumerable.Range(0, query.Join.FromColumns.Count)
                    .Select(i => dialect.EscapeIdentifier(JoinKeyNames.JoinIdAt(i, query.Join.FromColumns.Count))));

                return new ParameterizedSql(
                    $"SELECT DISTINCT {joinIdNames} FROM ({inner.Sql}) {dialect.EscapeIdentifier("p")}",
                    inner.Parameters);
            }

            // A join nested beneath a many-to-many collection is not correctly
            // supported: the parent m2m restricted sub-query exposes SOURCE-key values
            // as its JoinIds, but the stitch below matches them against this layer
            // using the parent's ConnectedColumns, which name TARGET columns — and the
            // junction Bridge is never consulted. That silently produced wrong/empty
            // rows. Fail loudly instead of returning incorrect data; correlating the
            // child through the junction is a deferred feature.
            if (query.Parent.Join.Bridge is not null)
                throw new BifrostExecutionError(
                    $"Nesting a join ('{query.Join.Name}') beneath a many-to-many collection " +
                    $"('{query.Parent.Join.Name}') is not supported: the child cannot be correlated " +
                    "through the junction table. Query the many-to-many collection without a further nested join.");

            var ea = dialect.EscapeIdentifier("a");
            var eb = dialect.EscapeIdentifier("b");
            var baseSql = GetRestrictedSqlParameterized(dbModel, dialect, parameters, query.Parent);
            var filterSql = query.FromTable.GetFilterSqlParameterized(dbModel, dialect, parameters, "a");

            // The recursive `b` sub-query exposes the parent's join-key
            // columns as JoinKeyNames.JoinIdAt — match them against this
            // layer's `a` row using the parent's ConnectedColumns.
            var (relationSql, relationParams) = query.Join.EmitOnClause(
                dialect, parameters,
                leftAlias: "b",
                rightAlias: "a",
                rightColumns: query.Parent.Join.ConnectedColumns);

            var nestedProjection = query.Join.EmitJoinIdProjection(dialect, "a");
            var querySql = $"SELECT DISTINCT {nestedProjection} FROM {dialect.TableReference(query.FromTable.SchemaName, query.FromTable.TableName)} {ea} INNER JOIN ({baseSql.Sql}) {eb} ON {relationSql}";
            return new ParameterizedSql(querySql, baseSql.Parameters.Concat(relationParams).ToList()).Append(filterSql);
        }

        /// <summary>
        /// Builds a <see cref="TableJoin"/> from this node to a resolved
        /// <paramref name="connectedTable"/> link. Centralizes the shared shape of
        /// the single-link, multi-link, and many-to-many branches in
        /// <see cref="ConnectLinks"/>; each caller supplies its own source/destination
        /// column pair(s), <see cref="QueryType"/>, and (for many-to-many) the
        /// junction <paramref name="bridge"/>. Passing <c>null</c> column lists lets
        /// the single-column branches fall back to <see cref="TableJoin"/>'s singleton
        /// default over the scalar <paramref name="fromColumn"/>/<paramref name="connectedColumn"/>.
        /// </summary>
        private TableJoin BuildTableJoin(
            GqlObjectQuery connectedTable,
            string name,
            string connectedColumn,
            IReadOnlyList<string>? connectedColumns,
            string fromColumn,
            IReadOnlyList<string>? fromColumns,
            QueryType queryType,
            JunctionBridge? bridge = null)
        {
            return new TableJoin
            {
                Alias = connectedTable.Alias,
                Name = name,
                ConnectedTable = connectedTable,
                ConnectedColumn = connectedColumn,
                ConnectedColumns = connectedColumns!,
                FromTable = this,
                FromColumn = fromColumn,
                FromColumns = fromColumns!,
                QueryType = queryType,
                Bridge = bridge,
            };
        }

        /// <summary>
        /// Converts links to joins and connects them to the parent table
        /// </summary>
        /// <param name="dbModel"></param>
        /// <param name="basePath"></param>
        /// <exception cref="BifrostExecutionError"></exception>
        public void ConnectLinks(IDbModel dbModel, string basePath = "")
        {
            foreach (var link in Links)
            {
                var thisDto = dbModel.GetTableFromDbName(TableName);
                // The real query path sets FieldName (normalized) via QueryField; links
                // constructed directly carry only GraphQlName. Fall back so both resolve
                // — matching the ManyToMany branch below, which keys on GraphQlName.
                var fieldName = string.IsNullOrEmpty(link.FieldName) ? link.GraphQlName : link.FieldName;
                if (thisDto.SingleLinks.TryGetValue(fieldName, out var singleLink)
                    || (singleLink = thisDto.SingleLinks.Values.FirstOrDefault(l => string.Equals(l.ParentFieldName, fieldName, StringComparison.OrdinalIgnoreCase))) != null)
                {
                    link.TableName = singleLink.ParentTable.DbName;
                    link.SchemaName = singleLink.ParentTable.TableSchema;
                    Joins.Add(BuildTableJoin(
                        link, fieldName,
                        connectedColumn: singleLink.ParentId.ColumnName,
                        connectedColumns: singleLink.ParentIds.Select(c => c.ColumnName).ToArray(),
                        fromColumn: singleLink.ChildId.ColumnName,
                        fromColumns: singleLink.ChildIds.Select(c => c.ColumnName).ToArray(),
                        queryType: QueryType.Single));
                    continue;
                }
                if (thisDto.MultiLinks.TryGetValue(fieldName, out var multiLink)
                    || (multiLink = thisDto.MultiLinks.Values.FirstOrDefault(l => string.Equals(l.ChildFieldName, fieldName, StringComparison.OrdinalIgnoreCase))) != null)
                {
                    link.TableName = multiLink.ChildTable.DbName;
                    link.SchemaName = multiLink.ChildTable.TableSchema;
                    var join = BuildTableJoin(
                        link, fieldName,
                        connectedColumn: multiLink.ChildId.ColumnName,
                        connectedColumns: multiLink.ChildIds.Select(c => c.ColumnName).ToArray(),
                        fromColumn: multiLink.ParentId.ColumnName,
                        fromColumns: multiLink.ParentIds.Select(c => c.ColumnName).ToArray(),
                        queryType: QueryType.Join);
                    // Polymorphic link: constrain the child node to its discriminator
                    // value (e.g. notes.entity_type = 'company'). Applying it to the
                    // child node's Filter means the existing filter machinery emits it
                    // against the child in every SQL path — both when the child is the
                    // connected table and when it is the parent of a deeper nested join —
                    // keeping each parent's collection isolated to its own rows.
                    if (multiLink.TypePredicate is { } predicate)
                    {
                        var constFilter = TableFilter.FromObject(
                            new Dictionary<string, object?>
                            {
                                [predicate.Column.GraphQlName] =
                                    new Dictionary<string, object?> { ["_eq"] = predicate.Value }
                            },
                            multiLink.ChildTable.DbName);
                        link.Filter = link.Filter == null
                            ? constFilter
                            : new TableFilter { FilterType = FilterType.And, And = { link.Filter, constFilter } };
                    }
                    Joins.Add(join);
                    continue;
                }
                if (thisDto.ManyToManyLinks.TryGetValue(link.GraphQlName, out var m2mLink))
                {
                    // Single source->target join that bridges through the junction
                    // table. Keying by the source key (src_id = source column) keeps
                    // the collection per-parent and lets the same window-paging the
                    // multi-link path uses partition by the source — a two-node
                    // junction->target chain would instead key by the target row
                    // and lose the source partition.
                    link.TableName = m2mLink.TargetTable.DbName;
                    link.SchemaName = m2mLink.TargetTable.TableSchema;
                    Joins.Add(BuildTableJoin(
                        link, link.GraphQlName,
                        connectedColumn: m2mLink.TargetColumn.ColumnName,
                        connectedColumns: null,
                        fromColumn: m2mLink.SourceColumn.ColumnName,
                        fromColumns: null,
                        queryType: QueryType.Join,
                        bridge: new JunctionBridge
                        {
                            JunctionTable = m2mLink.JunctionTable.DbName,
                            JunctionSchema = m2mLink.JunctionTable.TableSchema,
                            JunctionSourceColumn = m2mLink.JunctionSourceColumn.ColumnName,
                            JunctionTargetColumn = m2mLink.JunctionTargetColumn.ColumnName,
                        }));
                    continue;
                }
                throw new BifrostExecutionError($"Unable to find join {link.GraphQlName} on table {TableName}");
            }
            foreach (var join in Joins)
            {
                // Propagate the full path to the connected table before recursing so
                // that nested join keys (TableJoin.JoinName = "{FromTable.Path}->{Name}")
                // include the complete ancestor chain. When GqlObjectQuery objects are
                // constructed directly (e.g. in tests) rather than via QueryField.ToSqlData
                // the Path is not pre-set on child nodes, so we set it here if the child
                // has not already received a path (ToSqlData sets it before calling ConnectLinks).
                if (string.IsNullOrEmpty(join.ConnectedTable.Path))
                    join.ConnectedTable.Path = join.JoinName;
                join.ConnectedTable.ConnectLinks(dbModel);
            }
        }

        public ParameterizedSql GetFilterSqlParameterized(IDbModel model, ISqlDialect dialect, SqlParameterCollection parameters, string? alias = null)
        {
            if (Filter == null) return ParameterizedSql.Empty;

            // Assemble join fragments and the WHERE predicate in clause order:
            // "{joins} WHERE {where}". A relationship filter contributes an INNER
            // JOIN (which must sit before WHERE); leaf/tenant/soft-delete filters
            // contribute the predicate. Callers append this immediately after the
            // FROM table reference, so the joins land in the FROM clause and the
            // predicate in a real WHERE — never the invalid "WHERE INNER JOIN".
            var parts = Filter.RenderParts(model, dialect, parameters, alias);
            var hasWhere = !string.IsNullOrWhiteSpace(parts.Where);
            var joins = parts.Joins ?? "";
            if (string.IsNullOrWhiteSpace(joins) && !hasWhere) return ParameterizedSql.Empty;

            var sql = joins;
            if (hasWhere) sql += " WHERE " + parts.Where;
            if (!sql.StartsWith(" ")) sql = " " + sql;
            return new ParameterizedSql(sql, parts.Parameters);
        }

        public TableJoin? GetJoin(string? alias, string name)
        {
            // Scope to this level's DIRECT joins only. Searching the flattened
            // RecurseJoins (whole subtree) matched by name lets a join at a
            // different path with the same field name shadow the right one — the
            // "same table via two paths nulls the deeper one" bug. The reader
            // resolves level by level, so the correct join is always a direct
            // child of the current node.
            return Joins.FirstOrDefault(j => (alias != null && j.Alias == alias) || j.Name == name);
        }

        public GqlAggregateColumn? GetAggregate(string? alias, string name)
        {
            return AggregateColumns.FirstOrDefault(j => (alias != null && j.FinalColumnGraphQlName == alias) || j.FinalColumnGraphQlName == name);
        }

        public override string ToString()
        {
            return $"{TableName}";
        }
    }
}
