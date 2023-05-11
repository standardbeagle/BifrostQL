using BifrostQL.Core.Model;

namespace BifrostQL.Core.QueryModel
{
    public enum JoinType
    {
        Join = 0,
        Single = 1,
    }

    public sealed class TableSqlData
    {
        public TableJoin? JoinFrom { get; set; }
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
        public string KeyName => $"{Alias ?? TableName}";
        public List<(string GraphQlName, string DbName)> Columns { get; init; } = new List<(string GraphQlName, string DbName)>();

        public List<TableSqlData> Links { get; set; } = new List<TableSqlData>();
        public List<string> Sort { get; set; } = new List<string>();
        public TableFilter? Filter { get; set; }
        public int? Limit { get; set; }
        public int? Offset { get; set; }
        public bool IsFragment { get; set; }
        public bool IncludeResult { get; set; }
        public List<TableJoin> Joins { get; set; } = new List<TableJoin>();
        private IEnumerable<TableJoin> RecurseJoins => Joins.Concat(Joins.SelectMany(j => j.ConnectedTable.RecurseJoins));

        public IEnumerable<(string GraphQlName, string DbName)> FullColumnNames =>
            Columns.Where(c => c.GraphQlName.StartsWith("__") == false)
            .Concat(Joins.Select(j => (j.FromColumn, j.FromColumn)))
            .DistinctBy(c => c.Item2, SqlNameComparer.Instance);

        public Dictionary<string, string> ToSql(IDbModel dbModel)
        {
            ConnectLinks(dbModel);
            var columnSql = string.Join(",", FullColumnNames.Select(n => $"[{n.DbName}] [{n.GraphQlName}]"));
            var cmdText = $"SELECT {columnSql} FROM {FullTableText}";

            var filter = GetFilterSql(dbModel);
            var baseSql = cmdText + filter + GetSortAndPaging();
            var result = new Dictionary<string, string>
            {
                { KeyName, baseSql },
                { $"{KeyName}_count", $"SELECT COUNT(*) FROM {FullTableText}{filter}" }
            };
            foreach (var join in RecurseJoins)
            {
                result.Add(join.JoinName, join.GetSql(dbModel));
            }
            return result;
        }

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
                        JoinType = JoinType.Join,
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
                        JoinType = JoinType.Single,
                    };
                    Joins.Add(join);
                    continue;
                }
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
