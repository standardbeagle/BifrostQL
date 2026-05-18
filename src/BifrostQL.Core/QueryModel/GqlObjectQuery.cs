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
        public List<TableJoin> Joins { get; set; } = new();
        public IEnumerable<TableJoin> RecurseJoins => Joins.Concat(Joins.SelectMany(j => j.ConnectedTable.RecurseJoins));

        public IEnumerable<GqlObjectColumn> FullColumnNames =>
            ScalarColumns.Where(c => c.GraphQlDbName.StartsWith("__") == false)
            .Concat(Joins.Select(j => new GqlObjectColumn(j.FromColumn)))
            .DistinctBy(c => c.GraphQlDbName, SqlNameComparer.Instance);

        public void AddSqlParameterized(IDbModel dbModel, ISqlDialect dialect, IDictionary<string, ParameterizedSql> sqls, SqlParameterCollection parameters, QueryLink? queryLink = null)
        {
            var fullColumns = FullColumnNames.ToList();
            var tableRef = dialect.TableReference(SchemaName, TableName);

            var filter = GetFilterSqlParameterized(dbModel, dialect, parameters);
            var sqlKeyName = queryLink == null ? KeyName : queryLink.Join.JoinName;

            if (fullColumns.Count > 0)
            {
                var columnSql = string.Join(",", fullColumns.Select(n => $"{dialect.EscapeIdentifier(n.DbDbName)} {dialect.EscapeIdentifier(n.GraphQlDbName)}"));
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
                tableJoin.ConnectedTable.FullColumnNames.Select(c => $"{eb}.{dialect.EscapeIdentifier(c.DbDbName)} AS {dialect.EscapeIdentifier(c.GraphQlDbName)}"));

            // Single-column joins keep the historical `JoinId` / `src_id`
            // names so existing unit tests and ReaderEnum lookups stay
            // unchanged. Composite joins suffix every alias with `_<index>`
            // and AND every per-column equality into the ON clause.
            var (relationSql, relationParams) = BuildJoinRelation(dialect, parameters, tableJoin);
            var srcProjection = BuildSrcProjection(dialect, tableJoin, "a");

            var wrap = $"SELECT {srcProjection}, {joinColumnSql} FROM ({main.Sql}) {ea}";
            wrap += $" INNER JOIN {dialect.EscapeIdentifier(tableJoin.ConnectedTable.TableName)} {eb} ON {relationSql}";

            if (tableJoin.QueryType == QueryType.Single)
                return new ParameterizedSql(wrap, main.Parameters.Concat(relationParams).ToList());

            var filter = tableJoin.ConnectedTable.GetFilterSqlParameterized(dbModel, dialect, parameters, "b");
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

        /// <summary>
        /// Build the ON-clause SQL connecting the inner DISTINCT sub-query
        /// (alias `a`, key columns aliased `JoinId` / `JoinId_<i>`) to the
        /// connected table (alias `b`). Single-column joins produce one
        /// equality; composite joins AND every per-column pair.
        /// </summary>
        private static (string Sql, IReadOnlyList<SqlParameterInfo> Parameters) BuildJoinRelation(
            ISqlDialect dialect,
            SqlParameterCollection parameters,
            TableJoin tableJoin)
        {
            var connectedCols = tableJoin.ConnectedColumns;
            if (connectedCols.Count <= 1)
            {
                var single = TableFilter.GetSingleFilterParameterized(dialect, parameters, "a", "JoinId", tableJoin.Operator,
                    new FieldRef() { TableName = "b", ColumnName = tableJoin.ConnectedColumn });
                return (single.Sql, single.Parameters);
            }

            var collected = new List<SqlParameterInfo>();
            var clauses = new List<string>(connectedCols.Count);
            for (var i = 0; i < connectedCols.Count; i++)
            {
                var perCol = TableFilter.GetSingleFilterParameterized(dialect, parameters, "a", $"JoinId_{i}", tableJoin.Operator,
                    new FieldRef() { TableName = "b", ColumnName = connectedCols[i] });
                clauses.Add(perCol.Sql);
                collected.AddRange(perCol.Parameters);
            }
            return (string.Join(" AND ", clauses), collected);
        }

        /// <summary>
        /// Build the `a.JoinId src_id` projection (single column) or
        /// `a.JoinId_0 src_id_0, …, a.JoinId_N src_id_N` projection
        /// (composite) used by the wrap `SELECT` in
        /// <see cref="ToConnectedSqlParameterized"/>.
        /// </summary>
        private static string BuildSrcProjection(ISqlDialect dialect, TableJoin tableJoin, string innerAlias)
        {
            var ea = dialect.EscapeIdentifier(innerAlias);
            if (tableJoin.FromColumns.Count <= 1)
                return $"{ea}.{dialect.EscapeIdentifier("JoinId")} {dialect.EscapeIdentifier("src_id")}";

            return string.Join(", ", Enumerable.Range(0, tableJoin.FromColumns.Count).Select(i =>
                $"{ea}.{dialect.EscapeIdentifier($"JoinId_{i}")} {dialect.EscapeIdentifier($"src_id_{i}")}"));
        }

        /// <summary>
        /// Build the `SELECT DISTINCT FromCol AS JoinId` projection (single
        /// column) or `FromCol_0 AS JoinId_0, …` projection (composite)
        /// used by <see cref="GetRestrictedSqlParameterized"/> and by the
        /// recursive parent-restricted join layer.
        /// </summary>
        private static string BuildJoinIdProjection(ISqlDialect dialect, TableJoin tableJoin, string? tableAlias = null)
        {
            var prefix = tableAlias is null ? string.Empty : $"{dialect.EscapeIdentifier(tableAlias)}.";
            if (tableJoin.FromColumns.Count <= 1)
                return $"{prefix}{dialect.EscapeIdentifier(tableJoin.FromColumn)} AS {dialect.EscapeIdentifier("JoinId")}";

            return string.Join(", ", tableJoin.FromColumns.Select((col, i) =>
                $"{prefix}{dialect.EscapeIdentifier(col)} AS {dialect.EscapeIdentifier($"JoinId_{i}")}"));
        }

        public static ParameterizedSql GetRestrictedSqlParameterized(IDbModel dbModel, ISqlDialect dialect, SqlParameterCollection parameters, QueryLink query)
        {
            if (query.Parent == null)
            {
                var filter = query.FromTable.GetFilterSqlParameterized(dbModel, dialect, parameters);
                var projection = BuildJoinIdProjection(dialect, query.Join);
                var sqlText = $"SELECT DISTINCT {projection} FROM {dialect.EscapeIdentifier(query.FromTable.TableName)}";
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
            // columns as `JoinId` / `JoinId_<i>`. Match them against this
            // layer's `a` row using the parent's ConnectedColumns.
            var parentConnected = query.Parent.Join.ConnectedColumns;
            string relationSql;
            IReadOnlyList<SqlParameterInfo> relationParams;
            if (parentConnected.Count <= 1)
            {
                var single = TableFilter.GetSingleFilterParameterized(dialect, parameters, "b", "JoinId", query.Join.Operator,
                    new FieldRef() { TableName = "a", ColumnName = query.Parent.Join.ConnectedColumn });
                relationSql = single.Sql;
                relationParams = single.Parameters;
            }
            else
            {
                var collected = new List<SqlParameterInfo>();
                var clauses = new List<string>(parentConnected.Count);
                for (var i = 0; i < parentConnected.Count; i++)
                {
                    var perCol = TableFilter.GetSingleFilterParameterized(dialect, parameters, "b", $"JoinId_{i}", query.Join.Operator,
                        new FieldRef() { TableName = "a", ColumnName = parentConnected[i] });
                    clauses.Add(perCol.Sql);
                    collected.AddRange(perCol.Parameters);
                }
                relationSql = string.Join(" AND ", clauses);
                relationParams = collected;
            }

            var nestedProjection = BuildJoinIdProjection(dialect, query.Join, "a");
            var querySql = $"SELECT DISTINCT {nestedProjection} FROM {dialect.EscapeIdentifier(query.FromTable.TableName)} {ea} INNER JOIN ({baseSql.Sql}) {eb} ON {relationSql}";
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
                if (thisDto.MultiLinks.TryGetValue(link.GraphQlName, out var multiLink))
                {
                    link.TableName = multiLink.ChildTable.DbName;
                    var join = new TableJoin
                    {
                        Alias = link.Alias,
                        Name = link.GraphQlName,
                        ConnectedTable = link,
                        ConnectedColumn = multiLink.ChildId.ColumnName,
                        ConnectedColumns = multiLink.ChildIds.Select(c => c.ColumnName).ToArray(),
                        FromTable = this,
                        FromColumn = multiLink.ParentId.ColumnName,
                        FromColumns = multiLink.ParentIds.Select(c => c.ColumnName).ToArray(),
                        QueryType = QueryType.Join,
                    };
                    Joins.Add(join);
                    continue;
                }
                if (thisDto.SingleLinks.TryGetValue(link.GraphQlName, out var singleLink))
                {
                    link.TableName = singleLink.ParentTable.DbName;
                    var join = new TableJoin
                    {
                        Alias = link.Alias,
                        Name = link.GraphQlName,
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
                if (thisDto.ManyToManyLinks.TryGetValue(link.GraphQlName, out var m2mLink))
                {
                    // Create a junction query that selects the FK column to the target
                    var junctionQuery = new GqlObjectQuery
                    {
                        DbTable = m2mLink.JunctionTable,
                        TableName = m2mLink.JunctionTable.DbName,
                        SchemaName = m2mLink.JunctionTable.TableSchema,
                        GraphQlName = m2mLink.JunctionTable.GraphQlName,
                        Path = $"{Path}->{m2mLink.JunctionTable.GraphQlName}",
                        QueryType = QueryType.Standard,
                        ScalarColumns = { new GqlObjectColumn(m2mLink.JunctionTargetColumn.ColumnName) },
                    };
                    // First hop: source -> junction (OneToMany)
                    var junctionJoin = new TableJoin
                    {
                        Name = m2mLink.JunctionTable.GraphQlName,
                        ConnectedTable = junctionQuery,
                        ConnectedColumn = m2mLink.JunctionSourceColumn.ColumnName,
                        FromTable = this,
                        FromColumn = m2mLink.SourceColumn.ColumnName,
                        QueryType = QueryType.Join,
                    };
                    // Second hop: junction -> target (ManyToOne via junction FK)
                    link.TableName = m2mLink.TargetTable.DbName;
                    var targetJoin = new TableJoin
                    {
                        Alias = link.Alias,
                        Name = link.GraphQlName,
                        ConnectedTable = link,
                        ConnectedColumn = m2mLink.TargetColumn.ColumnName,
                        FromTable = junctionQuery,
                        FromColumn = m2mLink.JunctionTargetColumn.ColumnName,
                        QueryType = QueryType.Join,
                    };
                    junctionQuery.Joins.Add(targetJoin);
                    Joins.Add(junctionJoin);
                    continue;
                }
                throw new BifrostExecutionError($"Unable to find join {link.GraphQlName} on table {TableName}");
            }
            foreach (var join in Joins)
            {
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
