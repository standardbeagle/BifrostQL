using System.Text;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// SQL-fragment builders for a <see cref="TableLinkDto"/>. These live in the
/// QueryModel layer, not on the DTO, so <c>DbModel</c> stays pure data — the Model
/// layer describes the schema; only this layer knows how to render it as SQL. The
/// sole caller (<see cref="GqlAggregateColumn.ToSqlParameterized"/>) already holds
/// an <see cref="ISqlDialect"/>, so nothing in Model needs a dialect anymore.
///
/// NOTE: these use the link's FIRST source/destination column only — a single-column
/// FK assumption. Composite-key links are not correlated here (see AGENTS.md).
/// </summary>
public static class TableLinkSql
{
    /// <summary>Escaped table reference for the link's source side, per direction.</summary>
    public static string SourceTableRef(TableLinkDto link, ISqlDialect dialect, LinkDirection direction)
    {
        var table = direction == LinkDirection.ManyToOne ? link.ChildTable : link.ParentTable;
        return dialect.TableReference(table.TableSchema, table.DbName);
    }

    /// <summary>Escaped table reference for the link's destination side, per direction.</summary>
    public static string DestTableRef(TableLinkDto link, ISqlDialect dialect, LinkDirection direction)
    {
        var table = direction == LinkDirection.ManyToOne ? link.ParentTable : link.ChildTable;
        return dialect.TableReference(table.TableSchema, table.DbName);
    }

    /// <summary>Raw (unescaped) destination-side join column DbName, per direction.</summary>
    public static string DestJoinColumn(TableLinkDto link, LinkDirection direction)
        => direction == LinkDirection.ManyToOne ? link.ParentId.DbName : link.ChildId.DbName;

    /// <summary>
    /// Qualified, escaped source-side join-column expression, optionally aliased.
    /// Uses <paramref name="tableName"/> as the qualifier when supplied, otherwise the
    /// direction-appropriate source table's DbName.
    /// </summary>
    public static string SourceColumns(
        TableLinkDto link,
        ISqlDialect dialect,
        LinkDirection direction,
        string? tableName = null,
        string? columnName = null)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(tableName))
            builder.Append($"{dialect.EscapeIdentifier(tableName)}.");
        else if (direction == LinkDirection.ManyToOne)
            builder.Append($"{dialect.EscapeIdentifier(link.ChildTable.DbName)}.");
        else
            builder.Append($"{dialect.EscapeIdentifier(link.ParentTable.DbName)}.");

        builder.Append(direction == LinkDirection.ManyToOne
            ? dialect.EscapeIdentifier(link.ChildId.DbName)
            : dialect.EscapeIdentifier(link.ParentId.DbName));

        if (!string.IsNullOrWhiteSpace(columnName))
            builder.Append($" AS {dialect.EscapeIdentifier(columnName)}");

        return builder.ToString();
    }
}
