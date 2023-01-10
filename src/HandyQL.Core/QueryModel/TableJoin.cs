using GraphQLProxy.Model;
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

        public string GetParentSql(IDbModel dbModel)
        {
            if (FromTable.JoinFrom == null)
            {
                var filter = FromTable.GetFilterSql(dbModel);
                return $"SELECT DISTINCT [{FromColumn}] AS JoinId FROM [{FromTable.TableName}]" + filter;
            }
            else
            {
                var baseSql = FromTable.JoinFrom.GetParentSql(dbModel);
                var filter = FromTable.GetFilterSql(dbModel, "a");
                return $"SELECT DISTINCT a.[{FromColumn}] AS JoinId FROM [{FromTable.TableName}] a INNER JOIN ({baseSql}) b ON b.JoinId=a.[{FromTable.JoinFrom.ConnectedColumn}]" + filter;
            }
        }

        public string GetSql(IDbModel dbModel)
        {
            var main = GetParentSql(dbModel);
            var joinColumnSql = string.Join(",", ConnectedTable.FullColumnNames.Select(c => $"b.[{c.name}] AS [{c.alias}]"));

            var wrap = $"SELECT a.[JoinId] [src_id], {joinColumnSql} FROM ({main}) a";
            wrap += $" INNER JOIN [{ConnectedTable.TableName}] b ON a.[JoinId] = b.[{ConnectedColumn}]";

            var filter = ConnectedTable.GetFilterSql(dbModel);
            return JoinType == JoinType.Single ? wrap : wrap + filter + ConnectedTable.GetSortAndPaging();
        }

        public override string ToString()
        {
            return $"{JoinName}";
        }
    }
}
