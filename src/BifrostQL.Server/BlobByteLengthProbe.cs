using System.Globalization;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Server
{
    /// <summary>
    /// Measures a binary column's stored size on the database side, so a value over a mount's
    /// byte cap (<c>/_blob</c>'s <c>MaxBlobBytes</c>, the chat media route's <c>MaxMediaBytes</c>)
    /// is refused without ever entering process memory. The probe is a computed column over the
    /// allow-listed <c>BYTE_LEN</c> function (<c>DATALENGTH</c> on SQL Server, <c>LENGTH</c>
    /// elsewhere) that declares the measured column as its dependency, so it clears the same
    /// read guards as the value itself: a refused column refuses the probe, and a masked
    /// column masks it to null. On an encrypted column the probe measures the ciphertext,
    /// which is never smaller than the plaintext, so the pre-check errs toward refusing.
    ///
    /// <para>The probe and the value read are two statements. A row rewritten in between can
    /// still grow past the cap, so each caller keeps its post-read size check as a backstop.</para>
    /// </summary>
    internal static class BlobByteLengthProbe
    {
        /// <summary>The probe's result name. Not a <c>__</c> name: those are dropped from the select list.</summary>
        internal const string ResultName = "bifrost_probe_byte_length";

        /// <summary>A select column measuring <paramref name="column"/>'s stored bytes.</summary>
        public static GqlObjectColumn Column(ColumnDto column)
        {
            var definition = new ComputedColumnDefinition(
                ResultName, "Int", ComputedColumnKind.Expression, ExpressionOrProvider: "",
                Dependencies: new[] { column.DbName })
            {
                Expression = new SqlExpr.Fn("BYTE_LEN", new SqlExpr[] { new SqlExpr.Col(column.DbName) }),
            };
            return new GqlObjectColumn(definition, ResultName);
        }

        /// <summary>
        /// The measured size from a probe row, or null when the value is NULL or masked for this
        /// caller. A non-numeric result is a provider fault and throws.
        /// </summary>
        public static long? Read(IReadOnlyDictionary<string, object?> row)
        {
            if (!row.TryGetValue(ResultName, out var value))
                throw new InvalidOperationException("The byte-length probe returned no size column.");
            return value switch
            {
                null or DBNull => null,
                IConvertible number => number.ToInt64(CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException(
                    $"The byte-length probe returned a {value.GetType().Name}, not a number."),
            };
        }
    }
}
