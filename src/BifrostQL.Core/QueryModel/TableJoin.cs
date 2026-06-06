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

        /// <summary>
        /// Set for many-to-many joins. The source-to-target hop passes through
        /// a junction table: the join FROM emits an extra INNER JOIN against
        /// <see cref="JunctionBridge.JunctionTable"/> so the collection is keyed
        /// by the source parent (src_id = source key) rather than the junction
        /// or target row. Null for plain single-/multi-link joins.
        /// </summary>
        public JunctionBridge? Bridge { get; init; }

        /// <summary>
        /// Emits <c>SELECT DISTINCT FromCol AS JoinId</c> (single column)
        /// or the suffixed multi-column projection used by the inner
        /// restricted sub-query. Pass a table alias when the columns must
        /// be qualified (nested join layer).
        /// </summary>
        public string EmitJoinIdProjection(ISqlDialect dialect, string? tableAlias = null)
        {
            var prefix = tableAlias is null ? string.Empty : $"{dialect.EscapeIdentifier(tableAlias)}.";
            return string.Join(", ", FromColumns.Select((col, i) =>
                $"{prefix}{dialect.EscapeIdentifier(col)} AS {dialect.EscapeIdentifier(JoinKeyNames.JoinIdAt(i, FromColumns.Count))}"));
        }

        /// <summary>
        /// Emits the <c>a.JoinId src_id</c> projection (single column) or
        /// the suffixed multi-column projection used by the outer wrap
        /// <c>SELECT</c> in <see cref="GqlObjectQuery.ToConnectedSqlParameterized"/>.
        /// </summary>
        public string EmitSrcProjection(ISqlDialect dialect, string innerAlias)
        {
            var ea = dialect.EscapeIdentifier(innerAlias);
            return string.Join(", ", Enumerable.Range(0, FromColumns.Count).Select(i =>
                $"{ea}.{dialect.EscapeIdentifier(JoinKeyNames.JoinIdAt(i, FromColumns.Count))} {dialect.EscapeIdentifier(JoinKeyNames.SrcIdAt(i, FromColumns.Count))}"));
        }

        /// <summary>
        /// Emits the ON-clause connecting the inner sub-query (alias
        /// <paramref name="leftAlias"/>, key columns aliased as
        /// <see cref="JoinKeyNames.JoinIdAt"/>) to the connected table
        /// (alias <paramref name="rightAlias"/>). Single-column joins
        /// produce one equality; composite joins AND every per-column pair.
        /// The matched destination columns come from
        /// <paramref name="rightColumns"/> — defaults to
        /// <see cref="ConnectedColumns"/> but the parent-restricted
        /// recursion passes its own parent ConnectedColumns instead.
        /// </summary>
        public (string Sql, IReadOnlyList<SqlParameterInfo> Parameters) EmitOnClause(
            ISqlDialect dialect,
            SqlParameterCollection parameters,
            string leftAlias,
            string rightAlias,
            IReadOnlyList<string>? rightColumns = null)
        {
            var rightCols = rightColumns ?? ConnectedColumns;
            var collected = new List<SqlParameterInfo>();
            var clauses = new List<string>(rightCols.Count);
            for (var i = 0; i < rightCols.Count; i++)
            {
                var clause = TableFilter.GetSingleFilterParameterized(
                    dialect, parameters,
                    leftAlias, JoinKeyNames.JoinIdAt(i, rightCols.Count),
                    Operator,
                    new FieldRef { TableName = rightAlias, ColumnName = rightCols[i] });
                clauses.Add(clause.Sql);
                collected.AddRange(clause.Parameters);
            }
            return (string.Join(" AND ", clauses), collected);
        }

        public override string ToString()
        {
            return $"{JoinName}";
        }
    }

    /// <summary>
    /// Describes the junction hop for a many-to-many join. The join FROM clause
    /// becomes <c>(source) a JOIN junction j ON a.JoinId = j.JunctionSourceColumn
    /// JOIN target b ON j.JunctionTargetColumn = b.ConnectedColumn</c>, so the
    /// surfaced rows stay keyed by the source parent (src_id) and the per-parent
    /// window paging partitions by the source key — identical to multi-links.
    /// </summary>
    public sealed class JunctionBridge
    {
        public string JunctionTable { get; init; } = null!;
        /// <summary>Junction column referencing the source (matched to source JoinId).</summary>
        public string JunctionSourceColumn { get; init; } = null!;
        /// <summary>Junction column referencing the target (matched to target ConnectedColumn).</summary>
        public string JunctionTargetColumn { get; init; } = null!;
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
