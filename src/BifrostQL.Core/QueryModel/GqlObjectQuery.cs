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

        public void AddSqlParameterized(IDbModel dbModel, ISqlDialect dialect, IDictionary<string, ParameterizedSql> sqls, SqlParameterCollection parameters, QueryLink? queryLink = null)
        {
            var fullColumns = FullColumnNames.ToList();
            var tableRef = dialect.TableReference(SchemaName, TableName);

            var filter = GetFilterSqlParameterized(dbModel, dialect, parameters);
            var sqlKeyName = queryLink == null ? KeyName : queryLink.Join.JoinName;

            if (fullColumns.Count > 0)
            {
                var columnSql = string.Join(",", fullColumns.Select(n => n.ToSelectSql(dbModel, DbTable, dialect)));
                var cmdText = $"SELECT {columnSql} FROM {tableRef}";

                var sortCols = Sort.Any() ? Sort.Select(s => s switch
                {
                    { } when s.EndsWith("_asc") => dialect.EscapeIdentifier(s[..^4]) + " asc",
                    { } when s.EndsWith("_desc") => dialect.EscapeIdentifier(s[..^5]) + " desc",
                    _ => throw new BifrostExecutionError($"Unsupported sort token '{s}'; expected suffix '_asc' or '_desc'.")
                }) : null;
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
                var aggregateSql = col.ToSqlParameterized(dialect, filter);
                col.SqlKey = $"{sqlKeyName}=>agg_{col.FinalColumnGraphQlName}";
                sqls[col.SqlKey] = aggregateSql;
            }

            foreach (var join in Joins)
            {
                var joinQueryLink = new QueryLink(join, this, queryLink);
                AddJoinSqlParameterized(dbModel, dialect, sqls, parameters, joinQueryLink);
            }
        }

        private static void AddJoinSqlParameterized(IDbModel model, ISqlDialect dialect, IDictionary<string, ParameterizedSql> sqls, SqlParameterCollection parameters, QueryLink queryLink)
        {
            var main = GetRestrictedSqlParameterized(model, dialect, parameters, queryLink);
            var sql = ToConnectedSqlParameterized(model, dialect, parameters, main, queryLink.Join);
            sqls[queryLink.Join.JoinName] = sql;

            foreach (var join in queryLink.Join.ConnectedTable.Joins)
            {
                var joinQueryLink = new QueryLink(join, queryLink.Join.ConnectedTable, queryLink);
                AddJoinSqlParameterized(model, dialect, sqls, parameters, joinQueryLink);
            }
        }

        public static ParameterizedSql ToConnectedSqlParameterized(IDbModel dbModel, ISqlDialect dialect, SqlParameterCollection parameters, ParameterizedSql main, TableJoin tableJoin)
        {
            var connectedDbTable = dbModel.GetTableFromDbName(tableJoin.ConnectedTable.TableName);
            var ea = dialect.EscapeIdentifier("a");
            var eb = dialect.EscapeIdentifier("b");
            var joinColumnSql = string.Join(",",
                tableJoin.ConnectedTable.FullColumnNames.Select(c => c.ToSelectSql(dbModel, connectedDbTable, dialect, "b", useAsKeyword: true)));

            var srcProjection = tableJoin.EmitSrcProjection(dialect, "a");
            var projection = $"{srcProjection}, {joinColumnSql}";

            string fromClause;
            IReadOnlyList<SqlParameterInfo> relationParams;
            if (tableJoin.Bridge is { } bridge)
            {
                // Many-to-many: bridge source -> junction -> target. src_id stays
                // the source key (a.JoinId), so the collection is keyed and
                // window-paged per source parent just like a multi-link.
                var ej = dialect.EscapeIdentifier("j");
                var srcOnClause = string.Join(" AND ", Enumerable.Range(0, tableJoin.FromColumns.Count)
                    .Select(i => $"{ea}.{dialect.EscapeIdentifier(JoinKeyNames.JoinIdAt(i, tableJoin.FromColumns.Count))} = {ej}.{dialect.EscapeIdentifier(bridge.JunctionSourceColumn)}"));
                var tgtOnClause = $"{ej}.{dialect.EscapeIdentifier(bridge.JunctionTargetColumn)} = {eb}.{dialect.EscapeIdentifier(tableJoin.ConnectedColumn)}";
                fromClause = $"FROM ({main.Sql}) {ea}"
                    + $" INNER JOIN {dialect.EscapeIdentifier(bridge.JunctionTable)} {ej} ON {srcOnClause}"
                    + $" INNER JOIN {dialect.EscapeIdentifier(tableJoin.ConnectedTable.TableName)} {eb} ON {tgtOnClause}";
                relationParams = Array.Empty<SqlParameterInfo>();
            }
            else
            {
                var (relationSql, rp) = tableJoin.EmitOnClause(dialect, parameters, "a", "b");
                relationParams = rp;
                fromClause = $"FROM ({main.Sql}) {ea}"
                    + $" INNER JOIN {dialect.EscapeIdentifier(tableJoin.ConnectedTable.TableName)} {eb} ON {relationSql}";
            }
            var wrap = $"SELECT {projection} {fromClause}";

            if (tableJoin.QueryType == QueryType.Single)
                return new ParameterizedSql(wrap, main.Parameters.Concat(relationParams).ToList());

            var filter = tableJoin.ConnectedTable.GetFilterSqlParameterized(dbModel, dialect, parameters, "b");

            // Per-parent paged collection: compute a window partitioned by the
            // parent join-id columns so each parent gets its own row-number and
            // total, then filter on the row number. This keeps parent A's limit
            // from consuming parent B's rows — a flat global LIMIT cannot do
            // that. The non-paged path (single-link or m2m array) keeps the
            // historical global pagination.
            if (tableJoin.ConnectedTable.IncludeResult)
            {
                // Partition by the parent key columns as exposed by the inner
                // `a` sub-query (JoinId / JoinId_<i>). Order by the child sort
                // columns against the `b` alias — both are valid at the join
                // level where the window is computed.
                var srcCount = tableJoin.FromColumns.Count;
                var partitionCols = Enumerable.Range(0, srcCount)
                    .Select(i => $"{ea}.{dialect.EscapeIdentifier(JoinKeyNames.JoinIdAt(i, srcCount))}");
                var windowOrder = tableJoin.ConnectedTable.Sort.Any()
                    ? tableJoin.ConnectedTable.Sort.Select(s => s switch
                    {
                        { } when s.EndsWith("_asc") => $"{eb}.{dialect.EscapeIdentifier(s[..^4])} asc",
                        { } when s.EndsWith("_desc") => $"{eb}.{dialect.EscapeIdentifier(s[..^5])} desc",
                        _ => throw new BifrostExecutionError($"Unsupported sort token '{s}'; expected suffix '_asc' or '_desc'.")
                    })
                    : null;

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

            var sortCols = tableJoin.ConnectedTable.Sort.Any() ? tableJoin.ConnectedTable.Sort.Select(s => s switch
            {
                { } when s.EndsWith("_asc") => dialect.EscapeIdentifier(s[..^4]) + " asc",
                { } when s.EndsWith("_desc") => dialect.EscapeIdentifier(s[..^5]) + " desc",
                _ => throw new BifrostExecutionError($"Unsupported sort token '{s}'; expected suffix '_asc' or '_desc'.")
            }) : null;
            var pagination = dialect.Pagination(sortCols, tableJoin.ConnectedTable.Offset, tableJoin.ConnectedTable.Limit);

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
                var sqlText = $"SELECT DISTINCT {projection} FROM {dialect.TableReference(query.FromTable.SchemaName, query.FromTable.TableName)}";
                var rootSql = new ParameterizedSql(sqlText, Array.Empty<SqlParameterInfo>()).Append(filter);

                // Forward the parent table's pagination into the linked
                // sub-query so the joined rows stay aligned with the paged
                // parent set. Without this, the linked SQL distinct-selects
                // the unpaged universe and the join returns rows for parents
                // outside the current page.
                //
                // Skip when the parent is unbounded — `Limit == -1` is the
                // explicit "no limit" sentinel and `Offset` of 0 (or null)
                // means "from the start", so the linked sub-query already
                // matches the parent universe.
                var hasOffset = query.FromTable.Offset.HasValue && query.FromTable.Offset.Value > 0;
                var hasLimit = query.FromTable.Limit.HasValue && query.FromTable.Limit.Value > 0;
                if (hasOffset || hasLimit)
                {
                    var sortCols = query.FromTable.Sort.Any() ? query.FromTable.Sort.Select(s => s switch
                    {
                        { } when s.EndsWith("_asc") => dialect.EscapeIdentifier(s[..^4]) + " asc",
                        { } when s.EndsWith("_desc") => dialect.EscapeIdentifier(s[..^5]) + " desc",
                        _ => throw new BifrostExecutionError($"Unsupported sort token '{s}'; expected suffix '_asc' or '_desc'.")
                    }) : null;
                    var pagination = dialect.Pagination(sortCols, query.FromTable.Offset, query.FromTable.Limit);
                    rootSql = rootSql.Append(pagination);
                }
                return rootSql;
            }

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
                    var join = new TableJoin
                    {
                        Alias = link.Alias,
                        Name = fieldName,
                        ConnectedTable = link,
                        ConnectedColumn = singleLink.ParentId.ColumnName,
                        ConnectedColumns = singleLink.ParentIds.Select(c => c.ColumnName).ToArray(),
                        FromTable = this,
                        FromColumn = singleLink.ChildId.ColumnName,
                        FromColumns = singleLink.ChildIds.Select(c => c.ColumnName).ToArray(),
                        QueryType = QueryType.Single,
                    };
                    Joins.Add(join);
                    continue;
                }
                if (thisDto.MultiLinks.TryGetValue(fieldName, out var multiLink)
                    || (multiLink = thisDto.MultiLinks.Values.FirstOrDefault(l => string.Equals(l.ChildFieldName, fieldName, StringComparison.OrdinalIgnoreCase))) != null)
                {
                    link.TableName = multiLink.ChildTable.DbName;
                    var join = new TableJoin
                    {
                        Alias = link.Alias,
                        Name = fieldName,
                        ConnectedTable = link,
                        ConnectedColumn = multiLink.ChildId.ColumnName,
                        ConnectedColumns = multiLink.ChildIds.Select(c => c.ColumnName).ToArray(),
                        FromTable = this,
                        FromColumn = multiLink.ParentId.ColumnName,
                        FromColumns = multiLink.ParentIds.Select(c => c.ColumnName).ToArray(),
                        QueryType = QueryType.Join,
                    };
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
                    var join = new TableJoin
                    {
                        Alias = link.Alias,
                        Name = link.GraphQlName,
                        ConnectedTable = link,
                        ConnectedColumn = m2mLink.TargetColumn.ColumnName,
                        FromTable = this,
                        FromColumn = m2mLink.SourceColumn.ColumnName,
                        QueryType = QueryType.Join,
                        Bridge = new JunctionBridge
                        {
                            JunctionTable = m2mLink.JunctionTable.DbName,
                            JunctionSourceColumn = m2mLink.JunctionSourceColumn.ColumnName,
                            JunctionTargetColumn = m2mLink.JunctionTargetColumn.ColumnName,
                        },
                    };
                    Joins.Add(join);
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
            var result = Filter.ToSqlParameterized(model, dialect, parameters, alias);
            if (string.IsNullOrEmpty(result.Sql)) return ParameterizedSql.Empty;
            return result.Prepend(" WHERE ");
        }

        public TableJoin? GetJoin(string? alias, string name)
        {
            return RecurseJoins.FirstOrDefault(j => (alias != null && j.Alias == alias) || j.Name == name);
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
