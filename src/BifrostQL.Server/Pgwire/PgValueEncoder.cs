using System.Globalization;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Renders a materialized query value to its PostgreSQL <b>text</b> representation for
    /// a DataRow. The simple query protocol carries every value as text, so the client's
    /// driver parses these strings back into typed values using the column's advertised
    /// type OID (see <see cref="PgTypeMap"/>).
    ///
    /// <para>A <c>null</c> return means SQL NULL, which the DataRow encodes as a value of
    /// length <see cref="PgWireProtocol.NullValueLength"/> (-1) — distinct from an empty
    /// string (length 0).</para>
    /// </summary>
    internal static class PgValueEncoder
    {
        public static string? ToText(object? value)
        {
            return value switch
            {
                null => null,
                DBNull => null,

                // pg text bool literals are 't'/'f', not "True"/"False".
                bool b => b ? "t" : "f",

                // ISO-8601 without a 'T' separator matches pg's timestamp text output and
                // round-trips through Npgsql/psql. Preserve sub-second precision.
                DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
                DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

                Guid g => g.ToString("d", CultureInfo.InvariantCulture),

                // Byte arrays render as pg hex bytea ("\x...."); rare on this path but
                // deterministic rather than a culture-dependent ToString.
                byte[] bytes => "\\x" + Convert.ToHexString(bytes).ToLowerInvariant(),

                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
        }
    }
}
