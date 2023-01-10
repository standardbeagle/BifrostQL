using GraphQLProxy.Model;
using System.Diagnostics.CodeAnalysis;
using static GraphQLProxy.DbTableResolver;

namespace GraphQLProxy.QueryModel
{
    public enum JoinType
    {
        Join = 0,
        Single = 1,
    }

    public sealed class TableSqlData
    {
        public TableSqlData? Parent => JoinFrom?.FromTable;
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
        public List<string> ColumnNames { get; set; } = new List<string>();
        public List<TableSqlData> Links { get; set; } = new List<TableSqlData>();
        public List<string> Sort { get; set; } = new List<string>();
        public List<FragmentSpread> FragmentSpreads { get; set; } = new List<FragmentSpread>();
        public TableFilter? Filter { get; set; }
        public int? Limit { get; set; }
        public int? Offset { get; set; }
        public bool IsFragment { get; set; }
        public bool IncludeResult { get; set; }
        public bool ProcessingResultData { get; set; } = false;
        public List<TableJoin> Joins { get; set; } = new List<TableJoin>();
        private IEnumerable<TableJoin> RecurseJoins => Joins.Concat(Joins.SelectMany(j => j.ConnectedTable.RecurseJoins));

        public IEnumerable<string> AllJoinNames => new[] { TableName }
        .Concat(Joins.SelectMany(j => j.ConnectedTable.AllJoinNames.Select(n => $"{j.JoinName}+{n}")));

        public IEnumerable<(string name, string alias)> FullColumnNames =>
            ColumnNames.Where(c => c.StartsWith("__") == false)
            .Select(c => (c.ToLowerInvariant(), c.ToLowerInvariant()))
            .Concat(Joins.Select(j => (j.FromColumn.ToLowerInvariant(), j.FromColumn.ToLowerInvariant())))
            .DistinctBy(c => c.Item2, SqlNameComparer.Instance);

        public Dictionary<string, string> ToSql(IDbModel dbModel)
        {
            ConnectLinks(dbModel);
            var columnSql = string.Join(",", FullColumnNames.Select(n => $"[{n.name}] [{n.alias}]"));
            var cmdText = $"SELECT {columnSql} FROM {FullTableText}";

            var baseSql = cmdText + GetFilterSql() + GetSortAndPaging();
            var result = new Dictionary<string, string>();
            result.Add(KeyName, baseSql);
            result.Add($"{KeyName}_count", $"SELECT COUNT(*) FROM {FullTableText}{GetFilterSql()}");
            foreach (var join in RecurseJoins)
            {
                result.Add(join.JoinName, join.GetSql());
            }
            return result;
        }

        public void ConnectLinks(IDbModel dbModel, string basePath = "")
        {
            foreach (var link in Links)
            {
                var thisDto = dbModel.GetTableFromTableName(TableName);
                if (thisDto.MultiLinks.TryGetValue(link.GraphQlName, out var multiLink))
                {
                    link.TableName = multiLink.ChildTable.TableName;
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
                    link.TableName = singleLink.ParentTable.TableName;
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

        public string GetFilterSql(string? alias = null)
        {
            if (Filter == null) return "";
            return " WHERE " + Filter.ToSql(alias);
        }

        public TableJoin? GetJoin(string? alias, string name)
        {
            return RecurseJoins.FirstOrDefault(j => (alias != null && j.Alias == alias) || j.Name == name);
        }

        public string GetSortAndPaging()
        {
            var orderby = " ORDER BY (SELECT NULL)";
            if (Sort.Any())
            {
                orderby = " ORDER BY " + string.Join(", ", Sort);
            }
            orderby += Offset != null ? $" OFFSET {Offset} ROWS" : " OFFSET 0 ROWS";
            orderby += Limit != null ? $" FETCH NEXT {Limit} ROWS ONLY" : "";
            return orderby;
        }

        public override string ToString()
        {
            return $"{TableName}";
        }
    }

    internal class SqlNameComparer : IEqualityComparer<string>
    {
        public static readonly SqlNameComparer Instance = new SqlNameComparer();

        private SqlNameComparer() { }
        public bool Equals(string? x, string? y)
        {
            return string.Equals(x, y, StringComparison.InvariantCultureIgnoreCase);
        }

        public int GetHashCode([DisallowNull] string obj)
        {
            return string.GetHashCode(obj);
        }
    }
}
