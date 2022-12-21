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
        public string FullTableText => string.IsNullOrWhiteSpace(SchemaName) switch
        {
            true => $"[{TableName}]",
            false => $"[{SchemaName}].[{TableName}]",
        };
        public string Alias { get; set; } = "";
        public string KeyName => $"{Alias}:{TableName}";
        public List<string> ColumnNames { get; set; } = new List<string>();
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
            ColumnNames.Select(c => (c, c))
            .Concat(Joins.Select(j => (j.ParentColumn, j.ParentColumn)))
            .Distinct();

        public string GetFilterSql(string? alias = null)
        {
            if (Filter == null) return "";
            return " WHERE " + Filter.ToSql(alias);
        }

        public TableJoin GetJoin(string? alias, string name)
        {
            return RecurseJoins.First(j => j.Alias == alias && j.Name == name);
        }

        public Dictionary<string, string> ToSql()
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
            return result;
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
                    return value =>
                    {
                        var columnRow = (value as Dictionary<string, object?>)?.FirstOrDefault();
                        if (columnRow == null) return;
                        var operationRow = (columnRow?.Value as Dictionary<string, object?>)?.FirstOrDefault();
                        if (operationRow == null) return;
                        Filter = new TableFilter
                        {
                            ColumnName = columnRow?.Key!,
                            RelationName = operationRow?.Key!,
                            Value = operationRow?.Value
                        };

                    };
                case "sort":
                    return value => Sort.AddRange((value as List<object?>)?.Cast<string>() ?? Array.Empty<string>());
                case "limit":
                    return value => Limit = value as int?;
                case "offset":
                    return value => Offset = value as int?;
                case "on":
                    return value =>
                    {
                        var columns = (value as List<object?>)?.Cast<string>()?.ToArray() ?? Array.Empty<string>();
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
