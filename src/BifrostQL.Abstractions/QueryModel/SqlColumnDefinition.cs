namespace BifrostQL.Core.QueryModel;

/// <summary>
/// The portable column kinds a dialect maps to its concrete storage type when
/// provisioning an internal table (see <see cref="ISqlDialect.CreateTableIfNotExistsSql"/>).
/// Deliberately minimal — only what Bifrost's own bookkeeping tables need.
/// </summary>
public enum SqlColumnKind
{
    /// <summary>Unbounded text (TEXT / NVARCHAR(MAX)).</summary>
    Text,
    /// <summary>32-bit integer (INTEGER / INT).</summary>
    Int,
}

/// <summary>
/// One column in a dialect-portable CREATE TABLE for an internal Bifrost table.
/// The dialect renders <see cref="Kind"/> to its concrete type and escapes
/// <see cref="Name"/>; the caller never emits raw type text.
/// </summary>
public sealed record SqlColumnDefinition(string Name, SqlColumnKind Kind, bool Nullable = false, bool PrimaryKey = false);
