using System.Globalization;
using System.Text;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// RESP frame encoder. Serializes a <see cref="RespValue"/> by pattern match — the exact
    /// inverse of <see cref="RespReader"/>, so any value the reader produces re-encodes
    /// byte-for-byte. A value is encoded into a single buffer and written+flushed in one
    /// call, matching the protocol's frame-at-a-time flushing. RESP3-only types are encoded
    /// on request; whether it is legal to send them is the connection loop's decision (only
    /// after the client negotiated <c>HELLO 3</c>), not the codec's.
    /// </summary>
    internal static class RespWriter
    {
        public static async Task WriteAsync(Stream stream, RespValue value, CancellationToken ct)
        {
            var bytes = EncodeToArray(value);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        public static byte[] EncodeToArray(RespValue value)
        {
            using var buffer = new MemoryStream();
            Encode(value, buffer);
            return buffer.ToArray();
        }

        private static void Encode(RespValue value, MemoryStream buffer)
        {
            switch (value)
            {
                case RespSimpleString s:
                    WriteLine(buffer, RespProtocol.SimpleString, s.Value);
                    break;
                case RespError e:
                    WriteLine(buffer, RespProtocol.Error, e.Message);
                    break;
                case RespInteger i:
                    WriteLine(buffer, RespProtocol.Integer, i.Value.ToString(CultureInfo.InvariantCulture));
                    break;
                case RespBoolean b:
                    WriteLine(buffer, RespProtocol.Boolean, b.Value ? "t" : "f");
                    break;
                case RespNull:
                    WriteLine(buffer, RespProtocol.Null, string.Empty);
                    break;
                case RespDouble d:
                    WriteLine(buffer, RespProtocol.Double, FormatDouble(d.Value));
                    break;
                case RespBigNumber bn:
                    WriteLine(buffer, RespProtocol.BigNumber, bn.Digits);
                    break;
                case RespBulkString bs:
                    EncodeBulk(buffer, bs.Value);
                    break;
                case RespVerbatimString vs:
                    EncodeVerbatim(buffer, vs);
                    break;
                case RespArray a:
                    EncodeAggregate(buffer, RespProtocol.Array, a.Items);
                    break;
                case RespSet st:
                    EncodeAggregate(buffer, RespProtocol.Set, st.Items);
                    break;
                case RespPush p:
                    EncodeAggregate(buffer, RespProtocol.Push, p.Items);
                    break;
                case RespMap m:
                    EncodeMap(buffer, m.Entries);
                    break;
                default:
                    throw new ArgumentException($"unencodable RESP value {value.GetType().Name}.", nameof(value));
            }
        }

        private static void EncodeBulk(MemoryStream buffer, byte[]? data)
        {
            if (data is null)
            {
                WriteLine(buffer, RespProtocol.BulkString, "-1");
                return;
            }
            WriteLine(buffer, RespProtocol.BulkString, data.Length.ToString(CultureInfo.InvariantCulture));
            buffer.Write(data);
            WriteCrlf(buffer);
        }

        private static void EncodeVerbatim(MemoryStream buffer, RespVerbatimString value)
        {
            var content = Encoding.UTF8.GetBytes($"{value.Format}:{value.Value}");
            WriteLine(buffer, RespProtocol.VerbatimString, content.Length.ToString(CultureInfo.InvariantCulture));
            buffer.Write(content);
            WriteCrlf(buffer);
        }

        private static void EncodeAggregate(MemoryStream buffer, byte marker, IReadOnlyList<RespValue>? items)
        {
            if (items is null)
            {
                WriteLine(buffer, marker, "-1"); // only the array marker legally reaches here with null
                return;
            }
            WriteLine(buffer, marker, items.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var item in items)
                Encode(item, buffer);
        }

        private static void EncodeMap(MemoryStream buffer, IReadOnlyList<KeyValuePair<RespValue, RespValue>> entries)
        {
            WriteLine(buffer, RespProtocol.Map, entries.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var (key, value) in entries)
            {
                Encode(key, buffer);
                Encode(value, buffer);
            }
        }

        private static void WriteLine(MemoryStream buffer, byte marker, string payload)
        {
            buffer.WriteByte(marker);
            buffer.Write(Encoding.UTF8.GetBytes(payload));
            WriteCrlf(buffer);
        }

        private static void WriteCrlf(MemoryStream buffer)
        {
            buffer.WriteByte(RespProtocol.Cr);
            buffer.WriteByte(RespProtocol.Lf);
        }

        private static string FormatDouble(double value)
        {
            if (double.IsNaN(value)) return "nan";
            if (double.IsPositiveInfinity(value)) return "inf";
            if (double.IsNegativeInfinity(value)) return "-inf";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
