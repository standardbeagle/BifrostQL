using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema;

/// <summary>
/// Schema-qualified identity of a table for the lookup-table-enum pipeline
/// (<see cref="EnumValueLoader"/> → <see cref="EnumColumnMap"/>). A bare DbName is
/// not an identity: <c>sales.status</c> and <c>hr.status</c> share one. The key
/// is a value rather than an <see cref="IDbTable"/> reference because the enum
/// values are loaded once against the base model and then attached to every
/// per-profile model, whose table instances differ. Comparison is
/// case-insensitive, matching <see cref="IDbModel.TryGetTableFromDbName(string, string, out IDbTable?)"/>.
/// </summary>
public readonly struct EnumTableKey : IEquatable<EnumTableKey>
{
    public EnumTableKey(string schema, string dbName)
    {
        Schema = schema ?? string.Empty;
        DbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
    }

    public string Schema { get; }
    public string DbName { get; }

    public static EnumTableKey Of(IDbTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new EnumTableKey(table.TableSchema, table.DbName);
    }

    public bool Equals(EnumTableKey other) =>
        StringComparer.OrdinalIgnoreCase.Equals(Schema, other.Schema)
        && StringComparer.OrdinalIgnoreCase.Equals(DbName, other.DbName);

    public override bool Equals(object? obj) => obj is EnumTableKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(Schema),
        StringComparer.OrdinalIgnoreCase.GetHashCode(DbName));

    public static bool operator ==(EnumTableKey left, EnumTableKey right) => left.Equals(right);
    public static bool operator !=(EnumTableKey left, EnumTableKey right) => !left.Equals(right);

    public override string ToString() => $"{Schema}.{DbName}";
}
