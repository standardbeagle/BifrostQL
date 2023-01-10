using static GraphQLProxy.DbTableResolver;

namespace GraphQLProxy.QueryModel
{
    public sealed class TableJoin
    {
        public string Name { get; set; } = null!;
        public string? Alias { get; set; } = null!;
        public string JoinName => $"{FromTable.Alias ?? FromTable.TableName}->{Alias ?? Name}";
        public string Path { get; set; } = null!;
        public string FromColumn { get; set; } = null!;
        public string ConnectedColumn { get; set; } = null!;
        public JoinType JoinType { get; set; }
        public TableSqlData FromTable { get; set; } = null!;
        public TableSqlData ConnectedTable { get; set; } = null!;

        public string GetParentSql()
        {
            if (FromTable.JoinFrom == null)
                return $"SELECT DISTINCT [{FromColumn}] AS JoinId FROM [{FromTable.TableName}]" + FromTable.GetFilterSql();
            var baseSql = FromTable.JoinFrom.GetParentSql();
            return $"SELECT DISTINCT a.[{FromColumn}] AS JoinId FROM [{FromTable.TableName}] a INNER JOIN ({baseSql}) b ON b.JoinId=a.[{FromTable.JoinFrom.ConnectedColumn}]" + FromTable.GetFilterSql("a");
        }

        public string GetSql()
        {
            var main = GetParentSql();
            var joinColumnSql = string.Join(",", ConnectedTable.FullColumnNames.Select(c => $"b.[{c.name}] AS [{c.alias}]"));

            var wrap = $"SELECT a.[JoinId] [src_id], {joinColumnSql} FROM ({main}) a";
            wrap += $" INNER JOIN [{ConnectedTable.TableName}] b ON a.[JoinId] = b.[{ConnectedColumn}]";

            return JoinType == JoinType.Single ? wrap : wrap + ConnectedTable.GetFilterSql() + ConnectedTable.GetSortAndPaging();
        }

        public override string ToString()
        {
            return $"{JoinName}";
        }
    }
}
