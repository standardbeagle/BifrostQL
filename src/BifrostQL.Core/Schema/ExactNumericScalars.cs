using System.Globalization;
using System.Numerics;
using GraphQL.Types;
using GraphQLParser.AST;

namespace BifrostQL.Core.Schema;

/// <summary>
/// The <c>BigInt</c> and <c>Decimal</c> scalars, replaced with versions that also
/// accept their value as a decimal STRING.
///
/// JSON — and so every GraphQL variable payload — has one numeric type, and in a
/// browser that type is an IEEE-754 double. <c>9007199254740993</c> cannot be
/// written as a JSON number without silently becoming <c>9007199254740992</c>, and
/// an exact decimal loses precision the same way. Web clients must therefore send
/// these values as text. With the built-in number-only scalars they cannot: a
/// bigint column is unfilterable AND its rows are unreachable, because the grid's
/// row links bind the primary key through the same scalar.
///
/// <para>
/// The replacement works by registering these instances from
/// <c>DbSchemaBuilder.PreConfigure</c> — that is, BEFORE the SDL's types are built,
/// which is what lets a registered instance win the scalar-name lookup. Registering
/// on an already-constructed schema is too late and silently loses to the built-in,
/// as does <c>ISchema.ReplaceScalar</c>, which additionally walks <c>AllTypes</c> and
/// so forces schema initialization — and initializing at build time breaks any model
/// whose SDL names a type registered later (file storage emits <c>Upload!</c>).
/// </para>
/// </summary>
public static class ExactNumericScalars
{
    /// <summary>
    /// Registers the exact scalars. MUST be called from a schema builder's
    /// PreConfigure: registering later loses the name to the built-in scalar.
    /// </summary>
    public static void Register(GraphQL.Types.Schema schema)
    {
        schema.RegisterType(new ExactBigIntGraphType());
        schema.RegisterType(new ExactDecimalGraphType());
    }
}

/// <summary>
/// <c>BigInt</c>, accepting a JSON number or a decimal string.
///
/// Values inside <see cref="long"/> range are returned as <see cref="long"/>, never
/// <see cref="BigInteger"/>: ADO.NET providers have no mapping for BigInteger and
/// throw at parameter binding. A value too wide for a long cannot be stored in a
/// bigint column at all, so it stays a BigInteger and is dealt with at the bind
/// seam rather than being truncated into a different, valid-looking key here.
/// </summary>
public sealed class ExactBigIntGraphType : ScalarGraphType
{
    public ExactBigIntGraphType()
    {
        Name = "BigInt";
        Description = "A signed integer of arbitrary width. Send it as a JSON number, or as a " +
                      "decimal string to carry a value a double cannot hold exactly.";
    }

    /// <inheritdoc />
    public override object? ParseValue(object? value) => value switch
    {
        null => null,
        BigInteger big => Narrow(big),
        string text => ParseText(text),
        sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ulong u => u <= long.MaxValue ? Convert.ToInt64(u) : (object)new BigInteger(u),
        _ => ThrowValueConversionError(value),
    };

    /// <inheritdoc />
    public override object? ParseLiteral(GraphQLValue value) => value switch
    {
        GraphQLNullValue => null,
        GraphQLIntValue i => ParseText(i.Value.ToString()),
        GraphQLStringValue s => ParseText(s.Value.ToString()),
        _ => ThrowLiteralConversionError(value),
    };

    /// <inheritdoc />
    public override bool CanParseLiteral(GraphQLValue value) => value switch
    {
        GraphQLNullValue => true,
        GraphQLIntValue i => CanParseText(i.Value.ToString()),
        GraphQLStringValue s => CanParseText(s.Value.ToString()),
        _ => false,
    };

    /// <inheritdoc />
    public override bool CanParseValue(object? value) => value switch
    {
        null => true,
        string text => CanParseText(text),
        BigInteger or sbyte or byte or short or ushort or int or uint or long or ulong => true,
        _ => false,
    };

    private static bool CanParseText(string text)
        => BigInteger.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    private object ParseText(string text)
        => BigInteger.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Narrow(parsed)
            : ThrowValueConversionError(text)!;

    // Both branches cast to object deliberately: `long` converts IMPLICITLY to
    // BigInteger, so a bare ternary types itself as BigInteger and boxes the
    // narrowed value straight back into the type this exists to avoid.
    private static object Narrow(BigInteger value)
        => value >= long.MinValue && value <= long.MaxValue ? (object)(long)value : (object)value;
}

/// <summary>
/// <c>Decimal</c>, accepting a JSON number or a decimal string — the string form
/// being the only way a browser can express an exact value a double rounds.
/// </summary>
public sealed class ExactDecimalGraphType : ScalarGraphType
{
    public ExactDecimalGraphType()
    {
        Name = "Decimal";
        Description = "An exact fixed-point number. Send it as a JSON number, or as a decimal " +
                      "string to preserve precision a double would round away.";
    }

    /// <inheritdoc />
    public override object? ParseValue(object? value) => value switch
    {
        null => null,
        decimal d => d,
        string text => ParseText(text),
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double
            => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        BigInteger big => (decimal)big,
        _ => ThrowValueConversionError(value),
    };

    /// <inheritdoc />
    public override object? ParseLiteral(GraphQLValue value) => value switch
    {
        GraphQLNullValue => null,
        GraphQLIntValue i => ParseText(i.Value.ToString()),
        GraphQLFloatValue f => ParseText(f.Value.ToString()),
        GraphQLStringValue s => ParseText(s.Value.ToString()),
        _ => ThrowLiteralConversionError(value),
    };

    /// <inheritdoc />
    public override bool CanParseLiteral(GraphQLValue value) => value switch
    {
        GraphQLNullValue => true,
        GraphQLIntValue i => CanParseText(i.Value.ToString()),
        GraphQLFloatValue f => CanParseText(f.Value.ToString()),
        GraphQLStringValue s => CanParseText(s.Value.ToString()),
        _ => false,
    };

    /// <inheritdoc />
    public override bool CanParseValue(object? value) => value switch
    {
        null => true,
        string text => CanParseText(text),
        decimal or BigInteger or sbyte or byte or short or ushort or int or uint or long or ulong or float or double => true,
        _ => false,
    };

    private static bool CanParseText(string text)
        => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private object ParseText(string text)
        => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : ThrowValueConversionError(text)!;
}
