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
        /// <summary>
        /// Source-side column for single-column joins; for composite-key
        /// joins this is the first column. The full ordered list lives on
        /// <see cref="FromColumns"/>.
        /// </summary>
        public string FromColumn { get; init; } = null!;
        /// <summary>
        /// Destination-side column for single-column joins; for composite-key
        /// joins this is the first column. The full ordered list lives on
        /// <see cref="ConnectedColumns"/>.
        /// </summary>
        public string ConnectedColumn { get; init; } = null!;
        /// <summary>
        /// Full ordered source-side columns for the join. Single-column
        /// joins default to a singleton over <see cref="FromColumn"/>.
        /// </summary>
        public IReadOnlyList<string> FromColumns
        {
            get => _fromColumns ?? (FromColumn is null ? Array.Empty<string>() : new[] { FromColumn });
            init => _fromColumns = value;
        }
        private readonly IReadOnlyList<string>? _fromColumns;
        /// <summary>
        /// Full ordered destination-side columns for the join. Single-column
        /// joins default to a singleton over <see cref="ConnectedColumn"/>.
        /// </summary>
        public IReadOnlyList<string> ConnectedColumns
        {
            get => _connectedColumns ?? (ConnectedColumn is null ? Array.Empty<string>() : new[] { ConnectedColumn });
            init => _connectedColumns = value;
        }
        private readonly IReadOnlyList<string>? _connectedColumns;
        /// <summary>True when the join spans more than one column pair.</summary>
        public bool IsComposite => FromColumns.Count > 1;
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
