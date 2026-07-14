using System.Globalization;
using System.Text;
using BifrostQL.Server.Resp;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// The exception a RESP command raises when the wire answers with a <c>-ERR</c> reply. It
    /// carries the client-facing wire text verbatim — the sanitized <c>ERR internal error</c> for an
    /// execution fault (tenant-context-required, policy-read-deny), the curated <c>ERR …</c> for a
    /// parse/validation fault — so the conformance kit can assert the adapter surfaced the rejection
    /// rather than swallowing it or returning rows. Mirrors <c>PgWireQueryException</c>.
    /// </summary>
    internal sealed class RespWireException : Exception
    {
        public RespWireException(string wireMessage)
            : base($"resp command rejected: {wireMessage}") { }
    }

    /// <summary>
    /// The full data-command handler set the RESP front door registers (see
    /// <c>BifrostRespExtensions.AddBifrostResp</c>), constructed for a loopback so the conformance /
    /// integration / smoke harnesses drive the SAME dispatch surface production does — reads through
    /// <c>IQueryIntentExecutor</c>, writes through <c>IMutationIntentExecutor</c>, gated by the session
    /// identity and <c>RespWireOptions.EnableWrites</c>.
    /// </summary>
    internal static class RespDataHandlers
    {
        public static IRespCommandHandler[] All() => new IRespCommandHandler[]
        {
            new RespGetCommandHandler(),
            new RespMGetCommandHandler(),
            new RespExistsCommandHandler(),
            new RespTypeCommandHandler(),
            new RespHGetAllCommandHandler(),
            new RespHGetCommandHandler(),
            new RespScanCommandHandler(),
            new RespSetCommandHandler(),
            new RespHSetCommandHandler(),
            new RespDelCommandHandler(),
        };
    }

    /// <summary>
    /// High-level RESP wire operations layered over the raw <see cref="RespTestClient"/>: real
    /// commands encoded as bulk-string arrays and replies decoded with the real codec, so every call
    /// travels the ACTUAL RESP protocol path into the connection handler. A <c>-ERR</c> reply becomes a
    /// thrown <see cref="RespWireException"/> — the conformance contract that a server-side rejection
    /// must not be swallowed. Used by the conformance derivation, the tenant-filter integration test,
    /// and the representative SE.Redis wire-shape test.
    /// </summary>
    internal static class RespWire
    {
        /// <summary>AUTH &lt;user&gt; &lt;pass&gt;; throws if the login is refused (so a mis-set fixture fails loudly).</summary>
        public static async Task AuthenticateAsync(RespTestClient client, string user, string pass)
        {
            await client.SendCommandAsync(RespProtocol.Auth, user, pass);
            var reply = await ReadOrThrowAsync(client);
            if (reply is not RespSimpleString { Value: RespProtocol.Ok })
                throw new RespWireException($"AUTH did not return +OK: {Describe(reply)}");
        }

        /// <summary>
        /// SCAN &lt;table&gt;:* paging through the opaque cursor until it returns to <c>0</c>, collecting every
        /// emitted key. Each page is a real <c>SCAN</c> over the wire, so only PKs of rows the identity may
        /// see are ever returned. A <c>-ERR</c> on any page throws (e.g. a missing tenant identity fails closed).
        /// </summary>
        public static async Task<IReadOnlyList<string>> ScanAllKeysAsync(
            RespTestClient client, string table, int count = 1000)
        {
            var keys = new List<string>();
            var cursor = RespProtocol.ScanStartCursor;
            do
            {
                await client.SendCommandAsync(
                    RespProtocol.Scan, cursor, RespProtocol.ScanMatchOption, $"{table}{RespProtocol.ScanWildcardSuffix}",
                    RespProtocol.ScanCountOption, count.ToString(CultureInfo.InvariantCulture));
                var reply = await ReadOrThrowAsync(client);
                var page = reply as RespArray
                           ?? throw new RespWireException($"SCAN did not return an array: {Describe(reply)}");
                cursor = Text(page.Items![0]) ?? RespProtocol.ScanStartCursor;
                var pageKeys = (RespArray)page.Items[1];
                foreach (var item in pageKeys.Items!)
                    keys.Add(Text(item)!);
            }
            while (cursor != RespProtocol.ScanStartCursor);
            return keys;
        }

        /// <summary>
        /// HGETALL &lt;key&gt; decoded to a column→value map (DB name → text, SQL NULL → null). An empty hash
        /// (missing OR tenant-hidden row) is an empty map. A <c>-ERR</c> (e.g. a policy-denied column making
        /// the row unreadable) throws.
        /// </summary>
        public static async Task<IReadOnlyDictionary<string, string?>> HGetAllAsync(RespTestClient client, string key)
        {
            await client.SendCommandAsync(RespProtocol.HGetAll, key);
            var reply = await ReadOrThrowAsync(client);
            var array = reply as RespArray
                        ?? throw new RespWireException($"HGETALL did not return an array: {Describe(reply)}");
            var map = new Dictionary<string, string?>(StringComparer.Ordinal);
            var items = array.Items ?? Array.Empty<RespValue>();
            for (var i = 0; i + 1 < items.Count; i += 2)
                map[Text(items[i])!] = Text(items[i + 1]);
            return map;
        }

        /// <summary>GET &lt;key&gt; → the row JSON bulk string, or null for a missing/hidden row; a <c>-ERR</c> throws.</summary>
        public static async Task<string?> GetAsync(RespTestClient client, string key)
        {
            await client.SendCommandAsync(RespProtocol.Get, key);
            var reply = await ReadOrThrowAsync(client);
            return Text(reply);
        }

        /// <summary>HSET &lt;key&gt; &lt;field&gt; &lt;value&gt;… → the field count; a <c>-ERR</c> (disabled/denied) throws.</summary>
        public static async Task<long> HSetAsync(
            RespTestClient client, string key, IReadOnlyDictionary<string, object?> fields)
        {
            var args = new List<string> { RespProtocol.HSet, key };
            foreach (var (column, value) in fields)
            {
                args.Add(column);
                args.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            }
            await client.SendCommandAsync(args.ToArray());
            var reply = await ReadOrThrowAsync(client);
            return reply is RespInteger integer
                ? integer.Value
                : throw new RespWireException($"HSET did not return an integer: {Describe(reply)}");
        }

        /// <summary>DEL &lt;key&gt;… → the count of rows actually deleted; a <c>-ERR</c> throws.</summary>
        public static async Task<long> DelAsync(RespTestClient client, params string[] keys)
        {
            var args = new string[keys.Length + 1];
            args[0] = RespProtocol.Del;
            keys.CopyTo(args, 1);
            await client.SendCommandAsync(args);
            var reply = await ReadOrThrowAsync(client);
            return reply is RespInteger integer
                ? integer.Value
                : throw new RespWireException($"DEL did not return an integer: {Describe(reply)}");
        }

        /// <summary>Reads one reply, converting a server <c>-ERR</c> into a thrown <see cref="RespWireException"/>.</summary>
        private static async Task<RespValue> ReadOrThrowAsync(RespTestClient client)
        {
            var reply = await client.ReadReplyAsync()
                        ?? throw new RespWireException("connection closed before a reply arrived");
            if (reply is RespError error)
                throw new RespWireException(error.Message);
            return reply;
        }

        /// <summary>Text of a bulk/simple value; null for a RESP null bulk or RESP3 Null.</summary>
        private static string? Text(RespValue value) => value switch
        {
            RespBulkString { Value: { } bytes } => Encoding.UTF8.GetString(bytes),
            RespBulkString => null,
            RespSimpleString simple => simple.Value,
            RespNull => null,
            _ => throw new RespWireException($"expected a string value, got {Describe(value)}"),
        };

        private static string Describe(RespValue value) => value switch
        {
            RespError error => $"-ERR {error.Message}",
            _ => value.GetType().Name,
        };
    }
}
