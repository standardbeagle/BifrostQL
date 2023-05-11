using BifrostQL.Core.Model;
using BifrostQL.Model;

namespace BifrostQL.Core.QueryModel
{
    public sealed class TableJoin
    {
        public string Name { get; init; } = null!;
        public string? Alias { get; init; }
        public string JoinName => $"{FromTable.Alias ?? FromTable.TableName}->{Alias ?? Name}";
        public string FromColumn { get; init; } = null!;
        public string ConnectedColumn { get; init; } = null!;
        public JoinType JoinType { get; init; }
        public string Operator { get; init; } = null!;
        public TableSqlData FromTable { get; init; } = null!;
        public TableSqlData ConnectedTable { get; init; } =  null!;

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
                var filter = FromTable.GetFilterSql(dbModel, "[a]");
                var relation = TableFilter.GetSingleFilter("b", "JoinId", Operator,
                    new FieldRef() { TableName = "a", ColumnName = FromTable.JoinFrom.ConnectedColumn });

                return $"SELECT DISTINCT a.[{FromColumn}] AS JoinId FROM [{FromTable.TableName}] [a] INNER JOIN ({baseSql}) {relation}{filter}";
            }
        }

        public string GetSql(IDbModel dbModel)
        {
            var main = GetParentSql(dbModel);
            var connectedDbTable = dbModel.GetTableFromDbName(ConnectedTable.TableName);
            var connectedDbColumn = connectedDbTable.GraphQlLookup[ConnectedColumn];
            var joinColumnSql = string.Join(",", ConnectedTable.FullColumnNames.Select(c => $"[b].[{c.DbName}] AS [{c.GraphQlName}]"));

            var wrap = $"SELECT [a].[JoinId] [src_id], {joinColumnSql} FROM ({main}) [a]";
            var relation = TableFilter.GetSingleFilter("a", "JoinId", Operator,
                new FieldRef() { TableName = "b", ColumnName = connectedDbColumn.DbName });
            wrap += $" INNER JOIN [{ConnectedTable.TableName}] [b] ON {relation}";

            var filter = ConnectedTable.GetFilterSql(dbModel, "b");
            return JoinType == JoinType.Single ? wrap : wrap + filter + ConnectedTable.GetSortAndPaging();
        }

        public override string ToString()
        {
            return $"{JoinName}";
        }
    }

    public class FieldRef
    {
        public string? TableName { get; init; }
        public string ColumnName { get; init; } = null!;

        public override string ToString()
        {
            return TableName == null ? $"[{ColumnName}]" : $"[{TableName}].[{ColumnName}]";
        }
    }
}
