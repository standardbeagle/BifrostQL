using GraphQL.Types;

namespace BifrostQL.Core.Schema;

/// <summary>
/// <c>DateTime</c> that also serializes DATABASE STRING representations.
///
/// SQLite has no native datetime storage class: a column declared
/// <c>DATETIME</c> stores TEXT, and Microsoft.Data.Sqlite hands the value back
/// as a <see cref="string"/> ("2026-07-02 14:40:00"). The stock
/// <see cref="DateTimeGraphType"/> throws INVALID_OPERATION on strings, so
/// EVERY read of such a column failed to resolve — found live when the chat
/// demo's <c>messages.created_at</c> turned each history reload into a
/// GraphQL error banner. A parseable string round-trips as a DateTime; an
/// unparseable one still fails loudly rather than being passed through as a
/// differently-typed value the client did not ask for.
/// </summary>
public sealed class DbDateTimeGraphType : DateTimeGraphType
{
    public DbDateTimeGraphType()
    {
        // Claim the SDL's `DateTime` name — the default type-name derivation would
        // register this as "DbDateTime" and the stock scalar would keep winning.
        Name = "DateTime";
    }

    public override object? Serialize(object? value)
    {
        if (value is string text)
        {
            if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var parsed))
                return base.Serialize(parsed);
            return ThrowSerializationError(value);
        }
        return base.Serialize(value);
    }
}
