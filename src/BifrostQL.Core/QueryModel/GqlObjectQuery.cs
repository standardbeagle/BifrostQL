using System.Data.SqlTypes;
using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using GraphQL;

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
        public string SchemaName { get; set; } = "";
        public string TableName { get; set; } = "";
        public string GraphQlName { get; set; } = "";
        public string FullTableText => string.IsNullOrWhiteSpace(SchemaName) switch
        {
            true => $"[{TableName}]",
            false => $"[{SchemaName}].[{TableName}]",
        };
        public string? Alias { get; set; }
        public string Path { get; set; } = "";
        public string KeyName => $"{Alias ?? GraphQlName}";
        public QueryType QueryType { get; set; }
        public List<GqlObjectColumn> ScalarColumns { get; init; } = new ();
        public List<GqlObjectColumn> AggregateColumns { get; init; } = new ();

        public List<GqlObjectQuery> Links { get; set; } = new ();
        public List<string> Sort { get; set; } = new ();
        public TableFilter? Filter { get; set; }
        public int? Limit { get; set; }
        public int? Offset { get; set; }
        public bool IsFragment { get; set; }
        public bool IncludeResult { get; set; }
        public List<TableJoin> Joins { get; set; } = new ();
        public IEnumerable<TableJoin> RecurseJoins => Joins.Concat(Joins.SelectMany(j => j.ConnectedTable.RecurseJoins));

        public IEnumerable<GqlObjectColumn> FullColumnNames =>
            ScalarColumns.Where(c => c.GraphQlDbName.StartsWith("__") == false)
            .Concat(Joins.Select(j => new GqlObjectColumn(j.FromColumn)))
            .DistinctBy(c => c.DbDbName, SqlNameComparer.Instance);

        public void AddSql(IDbModel dbModel, IDictionary<string, string> sqls, QueryLink? queryLink = null)
        {
            var columnSql = string.Join(",", FullColumnNames.Select(n => $"[{n.DbDbName}] [{n.GraphQlDbName}]"));
            var cmdText = $"SELECT {columnSql} FROM {FullTableText}";

            var filter = GetFilterSql(dbModel);
            var baseSql = cmdText + filter + GetSortAndPaging();
            var sqlKeyName = queryLink == null ? KeyName : queryLink.Join.JoinName;
            sqls[sqlKeyName] = baseSql;
            if (IncludeResult)
                sqls[$"{sqlKeyName}=>count"] = $"SELECT COUNT(*) FROM {FullTableText}{filter}";
            if (AggregateColumns.Any())
                sqls[$"{sqlKeyName}=>aggregate"] = $"SELECT {string.Join(", ", AggregateColumns.Select(c => c.GetSqlColumn()))} FROM {FullTableText}{filter}";
            foreach (var join in Joins)
            {
                var joinQueryLink = new QueryLink { FromTable = this, Join = join, Parent = queryLink};
                AddJoinSql(dbModel, sqls, joinQueryLink);
            }
        }

        private static void AddJoinSql(IDbModel model, IDictionary<string, string> sqls, QueryLink queryLink)
        {
            var main = GetRestrictedSql(model, queryLink);

            var sql = ToConnectedSql(model, main, queryLink.Join);
            sqls[queryLink.Join.JoinName] = sql;
            foreach (var join in queryLink.Join.ConnectedTable.Joins)
            {
                var joinQueryLink = new QueryLink { FromTable = queryLink.Join.ConnectedTable, Join = join, Parent = queryLink };
                AddJoinSql(model, sqls, joinQueryLink);
            }
        }

        public static string ToConnectedSql(IDbModel dbModel, string main, TableJoin tableJoin)
        {
            var connectedDbTable = dbModel.GetTableFromDbName(tableJoin.ConnectedTable.TableName);
            var connectedDbColumn = connectedDbTable.GraphQlLookup[tableJoin.ConnectedColumn];
            var joinColumnSql = string.Join(",",
                tableJoin.ConnectedTable.FullColumnNames.Select(c => $"[b].[{c.DbDbName}] AS [{c.GraphQlDbName}]"));

            var wrap = $"SELECT [a].[JoinId] [src_id], {joinColumnSql} FROM ({main}) [a]";
            var relation = TableFilter.GetSingleFilter("a", "JoinId", tableJoin.Operator,
                new FieldRef() { TableName = "b", ColumnName = connectedDbColumn.DbName });
            wrap += $" INNER JOIN [{tableJoin.ConnectedTable.TableName}] [b] ON {relation}";
            if (tableJoin.QueryType == QueryType.Single)
                return wrap;

            var filter = tableJoin.ConnectedTable.GetFilterSql(dbModel, "b");
            var sort = tableJoin.ConnectedTable.GetSortAndPaging();
            return wrap + filter + sort;
        }

        public static string GetRestrictedSql(IDbModel dbModel, QueryLink query)
        {
            if (query.Parent == null)
            {
                var filter = query.FromTable.GetFilterSql(dbModel);
                return $"SELECT DISTINCT [{query.Join.FromColumn}] AS JoinId FROM [{query.FromTable.TableName}]" + filter;
            }
            else
            {
                var baseSql = GetRestrictedSql(dbModel, query.Parent);

                var filter = query.FromTable.GetFilterSql(dbModel, "[a]");
                var relation = TableFilter.GetSingleFilter("b", "JoinId", query.Join.Operator,
                    new FieldRef() { TableName = "a", ColumnName = query.Parent.Join.ConnectedColumn });

                return $"SELECT DISTINCT [a].[{query.Join.FromColumn}] AS JoinId FROM [{query.FromTable.TableName}] [a] INNER JOIN ({baseSql}) [b] ON {relation}{filter}";
            }
        }

        /// <summary>
        /// Converts links to joins and connects them to the parent table
        /// </summary>
        /// <param name="dbModel"></param>
        /// <param name="basePath"></param>
        /// <exception cref="ExecutionError"></exception>
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
                throw new ExecutionError($"Unable to find join {link.GraphQlName} on table {TableName}");   
            }
            foreach (var join in RecurseJoins)
            {
                join.ConnectedTable.ConnectLinks(dbModel);
            }
        }

        public string GetFilterSql(IDbModel model, string? alias = null)
        {
            if (Filter == null) return "";
            var (join, filter) = Filter.ToSql(model, alias);
            if (string.IsNullOrEmpty(filter)) return join;
            return join + " WHERE " + filter;
        }

        public TableJoin? GetJoin(string? alias, string name)
        {
            return RecurseJoins.FirstOrDefault(j => (alias != null && j.Alias == alias) || j.Name == name);
        }

        public string GetSortAndPaging()
        {
            var orderBy = " ORDER BY (SELECT NULL)";
            if (Sort.Any())
            {
                orderBy = " ORDER BY " + string.Join(", ", Sort.Select(s => s switch
                {
                    { } when s.EndsWith("_asc") => s[..^4] + " asc",
                    { } when s.EndsWith("_desc") => s[..^5] + " desc",
                    _ => throw new NotSupportedException()
                }));
            }
            orderBy += Offset != null ? $" OFFSET {Offset} ROWS" : " OFFSET 0 ROWS";
            orderBy += Limit != null ? $" FETCH NEXT {Limit} ROWS ONLY" : "";
            return orderBy;
        }

        public override string ToString()
        {
            return $"{TableName}";
        }
    }
}
