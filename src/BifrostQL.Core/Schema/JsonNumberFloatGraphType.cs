using System.Globalization;
using System.Text.Json;
using GraphQL.Types;

namespace BifrostQL.Core.Schema;

/// <summary>
/// The built-in <c>Float</c>, also accepting a JSON number token that
/// <see cref="ExactNumberInputsJsonConverter"/> kept as a <see cref="JsonElement"/>
/// because neither a decimal nor a double holds it exactly. A <c>Float</c> rounds
/// such a number to the nearest double, as it did when the stock converter handed it
/// over as a rounded decimal. Registered from <c>DbSchemaBuilder.PreConfigure</c>,
/// like <see cref="ExactNumericScalars"/>.
/// </summary>
public sealed class JsonNumberFloatGraphType : FloatGraphType
{
    public JsonNumberFloatGraphType()
    {
        // Explicit: a derived scalar's default name comes from its class name.
        Name = "Float";
    }

    /// <inheritdoc />
    public override object? ParseValue(object? value)
    {
        if (value is not JsonElement { ValueKind: JsonValueKind.Number } element)
            return base.ParseValue(value);
        // Past double's range the parse yields infinity, which no Float carries.
        var parsed = double.Parse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
        return double.IsFinite(parsed) ? parsed : ThrowValueConversionError(value);
    }

    /// <inheritdoc />
    public override bool CanParseValue(object? value)
        => value is JsonElement { ValueKind: JsonValueKind.Number } || base.CanParseValue(value);
}
