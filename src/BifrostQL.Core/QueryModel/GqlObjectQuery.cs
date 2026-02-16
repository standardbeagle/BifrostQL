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
            .DistinctBy(c => c.DbDbName, SqlNameComparer.Instance);

        public void AddSqlParameterized(IDbModel dbModel, ISqlDialect dialect, IDictionary<string, ParameterizedSql> sqls, SqlParameterCollection parameters, QueryLink? queryLink = null)
        {
            var columnSql = string.Join(",", FullColumnNames.Select(n => $"{dialect.EscapeIdentifier(n.DbDbName)} {dialect.EscapeIdentifier(n.GraphQlDbName)}"));
            var tableRef = dialect.TableReference(SchemaName, TableName);
            var cmdText = $"SELECT {columnSql} FROM {tableRef}";

            var filter = GetFilterSqlParameterized(dbModel, dialect, parameters);
            var sortCols = Sort.Any() ? Sort.Select(s => s switch
            {
                { } when s.EndsWith("_asc") => dialect.EscapeIdentifier(s[..^4]) + " asc",
                { } when s.EndsWith("_desc") => dialect.EscapeIdentifier(s[..^5]) + " desc",
                _ => throw new NotSupportedException()
            }) : null;
            var pagination = dialect.Pagination(sortCols, Offset, Limit);

            var baseSql = new ParameterizedSql(cmdText, Array.Empty<SqlParameterInfo>())
                .Append(filter)
                .Append(pagination);

            var sqlKeyName = queryLink == null ? KeyName : queryLink.Join.JoinName;
            sqls[sqlKeyName] = baseSql;

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
            var connectedDbColumn = connectedDbTable.GraphQlLookup[tableJoin.ConnectedColumn];
            var ea = dialect.EscapeIdentifier("a");
            var eb = dialect.EscapeIdentifier("b");
            var joinColumnSql = string.Join(",",
                tableJoin.ConnectedTable.FullColumnNames.Select(c => $"{eb}.{dialect.EscapeIdentifier(c.DbDbName)} AS {dialect.EscapeIdentifier(c.GraphQlDbName)}"));

            var relation = TableFilter.GetSingleFilterParameterized(dialect, parameters, "a", "JoinId", tableJoin.Operator,
                new FieldRef() { TableName = "b", ColumnName = connectedDbColumn.DbName });

            var wrap = $"SELECT {ea}.{dialect.EscapeIdentifier("JoinId")} {dialect.EscapeIdentifier("src_id")}, {joinColumnSql} FROM ({main.Sql}) {ea}";
            wrap += $" INNER JOIN {dialect.EscapeIdentifier(tableJoin.ConnectedTable.TableName)} {eb} ON {relation.Sql}";

            if (tableJoin.QueryType == QueryType.Single)
                return new ParameterizedSql(wrap, main.Parameters.Concat(relation.Parameters).ToList());

            var filter = tableJoin.ConnectedTable.GetFilterSqlParameterized(dbModel, dialect, parameters, "b");
            var sortCols = tableJoin.ConnectedTable.Sort.Any() ? tableJoin.ConnectedTable.Sort.Select(s => s switch
            {
                { } when s.EndsWith("_asc") => dialect.EscapeIdentifier(s[..^4]) + " asc",
                { } when s.EndsWith("_desc") => dialect.EscapeIdentifier(s[..^5]) + " desc",
                _ => throw new NotSupportedException()
            }) : null;
            var pagination = dialect.Pagination(sortCols, tableJoin.ConnectedTable.Offset, tableJoin.ConnectedTable.Limit);

            return new ParameterizedSql(wrap, main.Parameters.Concat(relation.Parameters).ToList())
                .Append(filter)
                .Append(pagination);
        }

        public static ParameterizedSql GetRestrictedSqlParameterized(IDbModel dbModel, ISqlDialect dialect, SqlParameterCollection parameters, QueryLink query)
        {
            if (query.Parent == null)
            {
                var filter = query.FromTable.GetFilterSqlParameterized(dbModel, dialect, parameters);
                var sqlText = $"SELECT DISTINCT {dialect.EscapeIdentifier(query.Join.FromColumn)} AS {dialect.EscapeIdentifier("JoinId")} FROM {dialect.EscapeIdentifier(query.FromTable.TableName)}";
                return new ParameterizedSql(sqlText, Array.Empty<SqlParameterInfo>()).Append(filter);
            }

            var ea = dialect.EscapeIdentifier("a");
            var eb = dialect.EscapeIdentifier("b");
            var baseSql = GetRestrictedSqlParameterized(dbModel, dialect, parameters, query.Parent);
            var filterSql = query.FromTable.GetFilterSqlParameterized(dbModel, dialect, parameters, "a");

            var relation = TableFilter.GetSingleFilterParameterized(dialect, parameters, "b", "JoinId", query.Join.Operator,
                new FieldRef() { TableName = "a", ColumnName = query.Parent.Join.ConnectedColumn });

            var querySql = $"SELECT DISTINCT {ea}.{dialect.EscapeIdentifier(query.Join.FromColumn)} AS {dialect.EscapeIdentifier("JoinId")} FROM {dialect.EscapeIdentifier(query.FromTable.TableName)} {ea} INNER JOIN ({baseSql.Sql}) {eb} ON {relation.Sql}";
            return new ParameterizedSql(querySql, baseSql.Parameters.Concat(relation.Parameters).ToList()).Append(filterSql);
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
                        FromTable = this,
                        FromColumn = multiLink.ParentId.ColumnName,
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
                        FromTable = this,
                        FromColumn = singleLink.ChildId.ColumnName,
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
