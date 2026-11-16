using GraphQL.Types;

namespace BifrostQL.Core.Schema;

/// <summary>
/// <c>Boolean</c> that also serializes DATABASE INTEGER representations.
///
/// SQLite has no boolean storage class: a column declared <c>BOOLEAN</c> or
/// <c>BIT</c> stores INTEGER 0/1, and Microsoft.Data.Sqlite hands the value back
/// as a <see cref="long"/>. The stock <see cref="BooleanGraphType"/> throws on
/// integers, so every read of such a column failed to resolve. 0 and 1 map to
/// false and true; any other integer still fails loudly, because coercing it
/// (to true, say) would hide corrupt data behind a plausible value.
/// </summary>
public sealed class DbBooleanGraphType : BooleanGraphType
{
    public DbBooleanGraphType()
    {
        // Claim the SDL's `Boolean` name — the default type-name derivation would
        // register this as "DbBoolean" and the stock scalar would keep winning.
        Name = "Boolean";
    }

    public override object? Serialize(object? value) => value switch
    {
        long n => FromDatabaseInteger(n, value),
        int n => FromDatabaseInteger(n, value),
        short n => FromDatabaseInteger(n, value),
        byte n => FromDatabaseInteger(n, value),
        _ => base.Serialize(value),
    };

    private object? FromDatabaseInteger(long n, object value) => n switch
    {
        0 => false,
        1 => true,
        _ => ThrowSerializationError(value),
    };
}
