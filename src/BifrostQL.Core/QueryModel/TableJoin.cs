using BifrostQL.Core.Model;
using BifrostQL.Model;

namespace BifrostQL.Core.QueryModel
{
    public sealed class TableJoin
    {
        public TableJoin() { }
        public string Name { get; init; } = null!;
        public string? Alias { get; init; }
        public string JoinName => $"{FromTable.Path}->{Alias ?? Name}";
        public string FromColumn { get; init; } = null!;
        public string ConnectedColumn { get; init; } = null!;
        public QueryType QueryType { get; init; }
        public string Operator { get; init; } = null!;
        public GqlObjectQuery FromTable { get; init; } = null!;
        public GqlObjectQuery ConnectedTable { get; init; } = null!;

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

    public class QueryLink
    {
        public QueryLink(TableJoin join, GqlObjectQuery fromTable, QueryLink? parent)
        {
            Join = join;
            FromTable = fromTable;
            Parent = parent;
        }

        public TableJoin Join { get; init; }
        public GqlObjectQuery FromTable { get; init; }
        public QueryLink? Parent { get; init; }
    }
}
