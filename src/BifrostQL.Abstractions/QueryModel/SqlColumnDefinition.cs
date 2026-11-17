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
    /// <summary>
    /// Text of at most <see cref="SqlColumnDefinition.KeyTextMaxLength"/> characters
    /// (VARCHAR(128) / NVARCHAR(128)). Use it for primary-key text columns: SQL Server refuses
    /// NVARCHAR(MAX) as an index key and MySQL refuses TEXT in a key without a prefix length.
    /// </summary>
    KeyText,
}

/// <summary>
/// One column in a dialect-portable CREATE TABLE for an internal Bifrost table.
/// The dialect renders <see cref="Kind"/> to its concrete type and escapes
/// <see cref="Name"/>; the caller never emits raw type text.
/// </summary>
public sealed record SqlColumnDefinition(string Name, SqlColumnKind Kind, bool Nullable = false, bool PrimaryKey = false)
{
    /// <summary>
    /// The character bound of <see cref="SqlColumnKind.KeyText"/>. Three such columns fit SQL
    /// Server's 900-byte clustered key (NVARCHAR, 2 bytes a character) and MySQL's 3072-byte key
    /// (utf8mb4, 4 bytes a character).
    /// </summary>
    public const int KeyTextMaxLength = 128;
}
