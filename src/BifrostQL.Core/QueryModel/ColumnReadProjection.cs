using BifrostQL.Core.Model;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// How the read path projects a table column: as itself, or through the dialect's
/// <see cref="ISqlDialect.TextCast(string, string)"/> when
/// <see cref="ISqlDialect.RequiresTextCast(string, string)"/> says so (PostgreSQL reads a
/// <c>uuid</c>, a temporal type, <c>bytea</c>, an enum, … as text). Every SELECT whose value
/// C# compares with another projection of the same column renders it here: a scalar field
/// (<see cref="GqlObjectColumn"/>), a link's parent key (<see cref="TableJoin.EmitSrcProjection"/>),
/// a row aggregate's key (<see cref="GqlAggregateColumn"/>) and an object-key segment
/// (<see cref="ObjectKeySegmentColumn"/>). Two projections of one column that disagree reach the
/// reader as a string and a provider value, which never compare equal.
/// </summary>
internal static class ColumnReadProjection
{
    /// <summary>True when the read path casts <paramref name="column"/> to text.</summary>
    public static bool ReadsAsText(IDbModel model, ISqlDialect dialect, ColumnDto column)
        => dialect.RequiresTextCast(column.DataType, model.TypeMapper.GetGraphQlType(column.EffectiveDataType));

    /// <summary>
    /// <paramref name="expression"/> (an escaped reference to <paramref name="column"/>, or to a
    /// sub-query alias carrying its raw value) as the read path projects it.
    /// </summary>
    public static string Render(IDbModel model, ISqlDialect dialect, ColumnDto column, string expression)
        => ReadsAsText(model, dialect, column) ? dialect.TextCast(expression, column.DataType) : expression;
}
