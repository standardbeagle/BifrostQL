using BifrostQL.Core.Model;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// One primary-key column handed to <see cref="ISqlDialect.ObjectKeyExpression"/>. The dialect
/// renders it as the text <see cref="Storage.S3ObjectKeyMap"/> builds in C# from the value the
/// read pipeline returns for this column, so it needs both how the column is stored
/// (<see cref="Column"/>) and how it is read (<see cref="ReadExpression"/>, <see cref="ReadClrType"/>).
/// </summary>
/// <param name="ColumnExpression">The escaped, table-qualified column reference.</param>
/// <param name="ReadExpression">
/// What the SELECT projects for this column: <paramref name="ColumnExpression"/>, or the
/// dialect's <see cref="ISqlDialect.TextCast(string, string)"/> of it when
/// <paramref name="ReadsAsText"/>. A text-cast column reaches C# as exactly this text.
/// </param>
/// <param name="ReadsAsText">True when the read projection casts the column to text.</param>
/// <param name="Column">The key column: data type, length, precision and scale.</param>
/// <param name="ReadClrType">
/// The CLR type the read pipeline produced for this column (after
/// <see cref="Resolvers.ReaderEnum.DbConvert"/>), when the caller observed one. It settles what
/// the declared type alone cannot, such as MySQL's <c>TINYINT(1)</c> read as <see cref="bool"/>
/// and <c>CHAR(36)</c> read as <see cref="Guid"/>. Null when unknown.
/// </param>
public sealed record ObjectKeySegmentColumn(
    string ColumnExpression, string ReadExpression, bool ReadsAsText, ColumnDto Column, Type? ReadClrType)
{
    /// <summary>
    /// <paramref name="table"/>'s key columns in declared order, each qualified with
    /// <paramref name="tableReference"/> and carrying the read projection the SELECT applies
    /// (<see cref="ColumnReadProjection"/>, as <see cref="GqlObjectColumn"/> renders it), so a
    /// column the SELECT casts to text is keyed by that very text. <paramref name="readTypes"/>
    /// holds observed CLR read types (null: unknown).
    /// </summary>
    public static IReadOnlyList<ObjectKeySegmentColumn> For(
        IDbTable table, string tableReference, IDbModel model, ISqlDialect dialect, IReadOnlyList<Type?>? readTypes)
        => table.KeyColumns
            .Select((c, i) =>
            {
                var column = $"{tableReference}.{dialect.EscapeIdentifier(c.DbName)}";
                return new ObjectKeySegmentColumn(
                    column,
                    ColumnReadProjection.Render(model, dialect, c, column),
                    ColumnReadProjection.ReadsAsText(model, dialect, c),
                    c,
                    readTypes != null && i < readTypes.Count ? readTypes[i] : null);
            })
            .ToList();
}

/// <summary>
/// A keyset window over a row's object-key text: the primary-key segments joined by
/// <c>/</c>, exactly as <see cref="Storage.S3ObjectKeyMap.KeyFor"/> renders them after the
/// column segment. A node carrying one is ordered by that text in binary order and filtered
/// to <c>key &gt; After AND key &gt;= From AND key &lt; Before</c> (each bound optional).
/// The bounds are bound as parameters, never spliced into SQL text.
/// </summary>
/// <param name="After">Exclusive lower bound (a resume position), or null.</param>
/// <param name="From">Inclusive lower bound (a prefix start), or null.</param>
/// <param name="Before">Exclusive upper bound (a prefix's successor), or null.</param>
/// <param name="KeyReadClrTypes">
/// Per key column in declared order, the CLR type the read pipeline returns for it, or null
/// when not observed (see <see cref="ObjectKeySegmentColumn.ReadClrType"/>).
/// </param>
public sealed record ObjectKeyRange(
    string? After, string? From, string? Before, IReadOnlyList<Type?>? KeyReadClrTypes = null);
