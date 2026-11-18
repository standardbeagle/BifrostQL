using System.Globalization;
using System.Numerics;

namespace BifrostQL.Core.Schema;

/// <summary>
/// Converts a provider cell to the <see cref="decimal"/> a <c>Decimal</c> output field writes,
/// for both scalars that write one: <see cref="ExactDecimalGraphType"/> (column values and the
/// grouped <c>&lt;table&gt;Aggregate</c> surface) and <see cref="AggregateNumberGraphType"/>
/// (the per-row <c>_agg</c> field).
///
/// A cell no decimal holds comes back as <see cref="NotADecimal.Instance"/>: text that is not a
/// number (SQLite keeps <c>'N/A'</c> in a DECIMAL column, and MAX returns it) or a double past
/// decimal's range or not finite (SQLite SUM/AVG over such a column). Each scalar refuses the
/// marker through <c>ThrowSerializationError</c>, so the error carries the marker's text and
/// never the cell's; a raw <c>Convert.ToDecimal</c> would throw an OverflowException instead,
/// and the stock conversion error embeds the cell.
/// </summary>
internal static class DecimalCell
{
    public static object ToDecimal(object value)
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
    /// Stands in for a cell <see cref="ToDecimal"/> could not convert. It carries no part of the
    /// cell, so the serialization error names no data.
    /// </summary>
    internal sealed class NotADecimal
    {
        public static readonly NotADecimal Instance = new();
        private NotADecimal() { }
        public override string ToString() => "a value outside decimal's range or not a number";
    }
}
