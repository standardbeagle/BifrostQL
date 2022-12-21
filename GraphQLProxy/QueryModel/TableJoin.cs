using static GraphQLProxy.DbTableResolver;

namespace GraphQLProxy.QueryModel
{
    public sealed class TableJoin
    {
        public string Name { get; set; } = null!;
        public string? Alias { get; set; } = null!;
        public string JoinName => $"{Alias ?? Name}+{Name}";
        public string ParentColumn { get; set; } = null!;
        public string ChildColumn { get; set; } = null!;
        public JoinType JoinType { get; set; }
        public TableSqlData ParentTable { get; set; } = null!;
        public TableSqlData ChildTable { get; set; } = null!;

        public string GetParentSql()
        {
            if (ParentTable.ParentJoin == null)
                return $"SELECT DISTINCT [{ParentColumn}] AS JoinId FROM [{ParentTable.TableName}]" + ParentTable.GetFilterSql();
            var baseSql = ParentTable.ParentJoin.GetParentSql();
            return $"SELECT DISTINCT a.[{ParentColumn}] AS JoinId FROM [{ParentTable.TableName}] a INNER JOIN ({baseSql}) b ON b.JoinId=a.[{ParentTable.ParentJoin.ChildColumn}]" + ParentTable.GetFilterSql("a");
        }

        public string GetSql()
        {
            var main = GetParentSql();
            var joinColumnSql = string.Join(",", ChildTable.FullColumnNames.Select(c => $"b.[{c.name}] AS [{c.alias}]"));

            var wrap = $"SELECT a.[JoinId] [src_id], {joinColumnSql} FROM ({main}) a";
            wrap += $" INNER JOIN [{ChildTable.TableName}] b ON a.[JoinId] = b.[{ChildColumn}]";

            var baseSql = wrap + ChildTable.GetFilterSql() + ChildTable.GetSortAndPaging();
            return baseSql;
        }

        public override string ToString()
        {
            return $"{JoinName}";
        }
    }
}
