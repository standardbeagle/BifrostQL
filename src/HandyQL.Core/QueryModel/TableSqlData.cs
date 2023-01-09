using GraphQLProxy.Model;
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
        public TableSqlData? Parent => ParentJoin?.ParentTable;
        public TableJoin? ParentJoin { get; set; }
        public string SchemaName { get; set; } = "";
        public string TableName { get; set; } = "";
        public string GraphQlName { get; set; } = "";
        public string FullTableText => string.IsNullOrWhiteSpace(SchemaName) switch
        {
            true => $"[{TableName}]",
            false => $"[{SchemaName}].[{TableName}]",
        };
        public string Alias { get; set; } = "";
        public string KeyName => $"{Alias}:{TableName}";
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
        private IEnumerable<TableJoin> RecurseJoins => Joins.Concat(Joins.SelectMany(j => j.ChildTable.RecurseJoins));

        public IEnumerable<string> AllJoinNames => new[] { TableName }
        .Concat(Joins.SelectMany(j => j.ChildTable.AllJoinNames.Select(n => $"{j.JoinName}+{n}")));

        public IEnumerable<(string name, string alias)> FullColumnNames =>
            ColumnNames.Where(c => c.StartsWith("__") == false)
            .Select(c => (c, c))
            .Concat(Joins.Select(j => (j.ParentColumn, j.ParentColumn)))
            .Distinct();

        public Dictionary<string, string> ToSql(IDbModel dbModel)
        {
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
            foreach (var link in Links)
            {
                var thisDto = dbModel.GetTable(GraphQlName);
                var linkedTableName = link.TableName;
                var multiLink = thisDto.MultiLinks.FirstOrDefault(l => string.Equals(l.ChildTable.GraphQLName, link.GraphQlName, StringComparison.InvariantCultureIgnoreCase));
                if (multiLink != null)
                {
                    var join = new TableJoin
                    {
                        Alias = link.Alias,
                        Name = link.TableName,
                        ChildTable = link,
                        ChildColumn = multiLink.ChildId.ColumnName,
                        ParentTable = this,
                        ParentColumn = multiLink.ParentId.ColumnName,
                        JoinType = JoinType.Join,
                    };
                    Joins.Add(join);
                    result.Add(join.JoinName, join.GetSql());
                    continue;
                }
            }
            return result;
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


        public Action<object?>? GetArgumentSetter(string argumentName)
        {
            switch (argumentName)
            {
                case "filter":
                    return value => TableFilter.FromObject(value);
                case "sort":
                    return value => Sort.AddRange((value as IEnumerable<object?>)?.Cast<string>() ?? throw new ArgumentException("sort", "Unable to convert list"));
                case "limit":
                    return value => Limit = Convert.ToInt32(value);
                case "offset":
                    return value => Offset = Convert.ToInt32(value);
                case "on":
                    return value =>
                    {
                        var columns = (value as IEnumerable<object?>)?.Cast<string>()?.ToArray() ?? throw new ArgumentException("on", "Unable to convert list");
                        if (columns.Length != 2)
                            throw new ArgumentException("on joins only support two columns");
                        if (ParentJoin == null)
                            throw new ArgumentException("Parent Join cannot be null for 'on' argument");
                        ParentJoin.ParentColumn = columns[0];
                        ParentJoin.ChildColumn = columns[1];
                    };
                default:
                    return value => { };
            }

        }
    }
}
