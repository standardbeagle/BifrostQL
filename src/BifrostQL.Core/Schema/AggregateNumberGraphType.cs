using System.Globalization;
using System.Numerics;
using GraphQL.Types;
using GraphQLParser.AST;

namespace BifrostQL.Core.Schema;

/// <summary>
/// <c>AggregateNumber</c>: the output-only result type of the per-row
/// <c>_agg(operation, value)</c> field.
///
/// <c>_agg</c> names the aggregated column through its <c>value</c> argument, so one
/// SDL field covers integer, float, bigint and decimal columns and both COUNT and
/// SUM/AVG/MIN/MAX. No single built-in scalar fits: <c>Float</c> rounds a decimal or
/// bigint SUM through a double, and <c>Decimal</c> would write COUNT as a string and
/// turn a float SUM past decimal's range into an error. This scalar writes each value
/// in the form the grouped <c>&lt;table&gt;Aggregate</c> surface gives the same
/// aggregate (<see cref="AggregateSurface.RowAggregateScalar"/>): COUNT as a JSON
/// integer, an exact (decimal/bigint) column's aggregate as a decimal string, any
/// other numeric column's as a JSON number.
///
/// <para>
/// The resolver picks the form with <see cref="Coerce"/>: provider types do not say
/// it (MySQL returns SUM of an INT column as DECIMAL, SQL Server returns SUM of a
/// BIGINT as long), so the column's GraphQL type does.
/// </para>
/// </summary>
public sealed class AggregateNumberGraphType : ScalarGraphType
{
    public const string TypeName = "AggregateNumber";

    public AggregateNumberGraphType()
    {
        Name = TypeName;
        Description = "A per-row aggregate result. COUNT is an integer; SUM/AVG/MIN/MAX over a Decimal or BigInt column " +
                      "is a decimal string (exact); over any other numeric column it is a JSON number.";
    }

    /// <summary>
    /// Converts a provider value to the CLR type whose serialization is the wire form
    /// <paramref name="resultScalar"/> (<c>Int</c>, <c>Decimal</c> or <c>Float</c>, from
    /// <see cref="AggregateSurface.RowAggregateScalar"/>) names. A non-numeric value
    /// under <c>Float</c> (MIN of a text column) passes through unchanged, and
    /// <see cref="Serialize"/> refuses it, as the <c>Float</c> field did before.
    /// </summary>
    public static object? Coerce(object? value, string resultScalar)
    {
        if (value is null)
            return null;
        return resultScalar switch
        {
            "Int" => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            "Decimal" => ToDecimal(value),
            "Float" => value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                ? Convert.ToDouble(value, CultureInfo.InvariantCulture)
                : value,
            _ => throw new ArgumentOutOfRangeException(nameof(resultScalar), resultScalar, "Unknown aggregate result scalar."),
        };
    }

    /// <summary>
    /// A <c>Decimal</c> result no <see cref="decimal"/> holds: text that is not a number
    /// (SQLite keeps <c>'N/A'</c> in a DECIMAL column, and MAX returns it) or a double past
    /// decimal's range (SQLite SUM/AVG over such a column). The grouped surface's
    /// <c>Decimal</c> field refuses both, so this refuses them too, through
    /// <see cref="Serialize"/>; a double fallback would type the field by its data.
    /// </summary>
    private static object ToDecimal(object value)
    {
        switch (value)
        {
            case decimal d:
                return d;
            case BigInteger big:
                return big >= (BigInteger)decimal.MinValue && big <= (BigInteger)decimal.MaxValue
                    ? (decimal)big
                    : NotADecimal.Instance;
            case string text:
                return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : NotADecimal.Instance;
            case double or float:
                var asDouble = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return double.IsFinite(asDouble) && Math.Abs(asDouble) < (double)decimal.MaxValue
                    ? (decimal)asDouble
                    : NotADecimal.Instance;
            default:
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Stands in for a <c>Decimal</c> aggregate value <see cref="Coerce"/> could not convert.
    /// It carries no part of the cell, so the serialization error names no data.
    /// </summary>
    private sealed class NotADecimal
    {
        public static readonly NotADecimal Instance = new();
        private NotADecimal() { }
        public override string ToString() => "a value outside decimal's range or not a number";
    }

    /// <summary>
    /// Refuses anything else through <see cref="ScalarGraphType.ThrowSerializationError"/>
    /// with the value's type only: the stock message embeds the value, and an aggregate's
    /// value is cell data (MIN of a text column, a DECIMAL cell holding text).
    /// </summary>
    public override object? Serialize(object? value) => value switch
    {
        null => null,
        sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double d when double.IsFinite(d) => d,
        float f when float.IsFinite(f) => (double)f,
        NotADecimal => ThrowSerializationError(value),
        _ => ThrowSerializationError($"a {value.GetType().Name} value"),
    };

    /// <summary>Output-only: an aggregate result is never an argument.</summary>
    public override object? ParseValue(object? value) => ThrowValueConversionError(value);

    /// <summary>Output-only: an aggregate result is never an argument.</summary>
    public override object? ParseLiteral(GraphQLValue value) => ThrowLiteralConversionError(value);

    /// <inheritdoc />
    public override bool CanParseLiteral(GraphQLValue value) => false;

    /// <inheritdoc />
    public override bool CanParseValue(object? value) => false;
}
