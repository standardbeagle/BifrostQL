namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// Wire constants for the Redis serialization protocol (RESP2 + the RESP3 additions
    /// negotiated by <c>HELLO 3</c>) and the plumbing command surface this front door's
    /// slice-1 connection loop understands. Only the type markers, caps, command names and
    /// canonical reply/error strings the handshake+auth phase exchanges are named here; the
    /// data command surface (GET/SET/HGETALL/SCAN…) attaches at the dispatch seam in later
    /// slices.
    ///
    /// <para>Reference: RESP2/RESP3 spec. Every reply is a type byte followed by a
    /// <c>\r\n</c>-terminated payload; aggregates prefix an element count. No magic
    /// strings elsewhere — codec, connection loop and commands all read from here.</para>
    /// </summary>
    internal static class RespProtocol
    {
        // ---- RESP2 type markers ----
        public const byte SimpleString = (byte)'+';
        public const byte Error = (byte)'-';
        public const byte Integer = (byte)':';
        public const byte BulkString = (byte)'$';
        public const byte Array = (byte)'*';

        // ---- RESP3 additional type markers (only sent to a RESP3-negotiated client) ----
        public const byte Null = (byte)'_';
        public const byte Boolean = (byte)'#';
        public const byte Double = (byte)',';
        public const byte BigNumber = (byte)'(';
        public const byte VerbatimString = (byte)'=';
        public const byte Map = (byte)'%';
        public const byte Set = (byte)'~';
        public const byte Push = (byte)'>';

        /// <summary>The length sentinel for a RESP2 null bulk string / null array (<c>$-1</c> / <c>*-1</c>).</summary>
        public const int NullLength = -1;

        public const byte Cr = (byte)'\r';
        public const byte Lf = (byte)'\n';

        // ---- Negotiated protocol versions ----
        public const int Resp2 = 2;
        public const int Resp3 = 3;

        // ---- Plumbing command names (dispatched case-insensitively; store the canonical upper form) ----
        public const string Ping = "PING";
        public const string Hello = "HELLO";
        public const string Auth = "AUTH";
        public const string Select = "SELECT";
        public const string Info = "INFO";
        public const string Quit = "QUIT";
        public const string Reset = "RESET";
        public const string Command = "COMMAND";
        public const string Client = "CLIENT";

        // ---- Data command names (slice 2 read surface; dispatched at the IRespCommandHandler seam) ----
        public const string Get = "GET";
        public const string MGet = "MGET";
        public const string Exists = "EXISTS";
        public const string Type = "TYPE";

        // ---- Hash command names (slice 3: a row projected as a field/value hash) ----
        /// <summary><c>HGETALL &lt;table&gt;:&lt;pk…&gt;</c> — the whole row as a field/value hash.</summary>
        public const string HGetAll = "HGETALL";

        /// <summary><c>HGET &lt;table&gt;:&lt;pk…&gt; &lt;field&gt;</c> — one visible column's value.</summary>
        public const string HGet = "HGET";

        // ---- Cursor-scan command (slice 4: PK enumeration over a table) ----
        /// <summary><c>SCAN &lt;cursor&gt; MATCH &lt;table&gt;:* [COUNT n] [TYPE t]</c> — cursor-paginated PK enumeration.</summary>
        public const string Scan = "SCAN";

        // ---- Write command names (slice 5: opt-in row mutation through IMutationIntentExecutor) ----
        /// <summary><c>SET &lt;table&gt;:&lt;pk…&gt; &lt;json&gt;</c> — update a row's columns from a JSON object.</summary>
        public const string SetCommand = "SET";

        /// <summary><c>HSET &lt;table&gt;:&lt;pk…&gt; &lt;field&gt; &lt;value&gt; […]</c> — update named columns of a row.</summary>
        public const string HSet = "HSET";

        /// <summary><c>DEL &lt;key&gt; […]</c> — delete the addressed rows.</summary>
        public const string Del = "DEL";

        /// <summary>SCAN option keyword: the glob that names the table to enumerate (only <c>&lt;table&gt;:*</c> is supported).</summary>
        public const string ScanMatchOption = "MATCH";

        /// <summary>SCAN option keyword: the caller's page-size hint, capped at <see cref="MaxScanPageSize"/>.</summary>
        public const string ScanCountOption = "COUNT";

        /// <summary>SCAN option keyword: a Redis type filter — accepted and ignored (a Bifrost row has no single Redis type).</summary>
        public const string ScanTypeOption = "TYPE";

        /// <summary>The <c>&lt;table&gt;:*</c> suffix that is the only MATCH pattern this front door supports.</summary>
        public const string ScanWildcardSuffix = ":*";

        /// <summary>The Redis SCAN start/terminal cursor sentinel: iteration begins at <c>0</c> and ends when the reply is <c>0</c>.</summary>
        public const string ScanStartCursor = "0";

        /// <summary>Default page size when the caller supplies no COUNT (Redis' own SCAN default).</summary>
        public const int DefaultScanPageSize = 10;

        /// <summary>Hard upper bound on a single SCAN page — COUNT is a hint, capped here to bound per-page latency/memory.</summary>
        public const int MaxScanPageSize = 1000;

        /// <summary>
        /// The Redis key-space separator. A data-command key is addressed as
        /// <c>&lt;table&gt;:&lt;pk1&gt;[:&lt;pk2&gt;…]</c>; the first segment is the table and the
        /// remaining segments are the primary-key values in schema order. A key value that itself
        /// contains this separator cannot be addressed — it splits into the wrong segment count and
        /// is refused, mirroring the Redis keyspace convention (callers encode such values).
        /// </summary>
        public const char KeySeparator = ':';

        // ---- HELLO option keywords ----
        public const string HelloAuthOption = "AUTH";
        public const string HelloSetNameOption = "SETNAME";

        /// <summary>The implicit username an <c>AUTH &lt;password&gt;</c> (no username) authenticates as, per Redis.</summary>
        public const string DefaultUser = "default";

        // ---- Canonical replies ----
        public const string Pong = "PONG";
        public const string Ok = "OK";
        public const string ResetReply = "RESET";

        // ---- TYPE command replies (a Bifrost row is modeled as a JSON string value) ----
        /// <summary>TYPE reply for an existing, visible row: it is exposed as a JSON string value.</summary>
        public const string TypeString = "string";

        /// <summary>TYPE reply for a missing key OR a row the caller's identity cannot see (indistinguishable).</summary>
        public const string TypeNone = "none";

        // ---- Canonical error strings (first token is the Redis error code) ----
        /// <summary>Refusal for a command that needs an established identity before AUTH.</summary>
        public const string NoAuthError = "NOAUTH Authentication required.";

        /// <summary>Refusal for a bad username/password pair — Redis' verbatim WRONGPASS wording.</summary>
        public const string WrongPassError =
            "WRONGPASS invalid username-password pair or user is disabled.";

        /// <summary>Refusal when HELLO is issued unauthenticated on an auth-required front door.</summary>
        public const string HelloNoAuthError =
            "NOAUTH HELLO must be called with the client already authenticated, otherwise the " +
            "HELLO <proto> AUTH <user> <pass> option can be used to authenticate the client and " +
            "select the RESP protocol version at the same time.";

        /// <summary>Refusal for an unsupported RESP protocol version in HELLO.</summary>
        public const string NoProtoError =
            "NOPROTO unsupported protocol version";

        /// <summary>Refusal for a SELECT index this single-namespace front door does not expose.</summary>
        public const string DbIndexOutOfRangeError = "ERR DB index is out of range";

        /// <summary>Generic, client-safe wording for a wire-framing / protocol violation before the connection closes.</summary>
        public const string ProtocolErrorPrefix = "ERR Protocol error: ";

        /// <summary>The Redis generic-error prefix for a client-facing, honestly-worded command error.</summary>
        public const string ErrPrefix = "ERR ";

        /// <summary>
        /// Refusal for a write command (SET/HSET/DEL) while the write surface is disabled — the
        /// default posture (<see cref="RespWireOptions.EnableWrites"/> is false). Honest and clean:
        /// the command executes nothing and no mutation intent is ever built.
        /// </summary>
        public const string WritesDisabledError = "ERR write commands are disabled";

        /// <summary>
        /// Sanitized, client-safe wording for an unexpected server-side failure while executing a data
        /// command. The real exception is logged server-side; its message never reaches the wire, per the
        /// protocol-adapter security invariant that Bifrost-internal exception text is untrusted on any
        /// client-facing wire (it can carry schema/driver detail).
        /// </summary>
        public const string InternalError = "ERR internal error";

        /// <summary>The Redis wrong-argument-count error for <paramref name="command"/> (lower-cased, quoted).</summary>
        public static string WrongArgCount(string command) =>
            $"ERR wrong number of arguments for '{command.ToLowerInvariant()}' command";

        // ---- HELLO server-info map keys ----
        public const string HelloServer = "server";
        public const string HelloVersion = "version";
        public const string HelloProto = "proto";
        public const string HelloId = "id";
        public const string HelloMode = "mode";
        public const string HelloRole = "role";
        public const string HelloModules = "modules";

        /// <summary>Server identity advertised in HELLO / INFO. Honest — this is not a real Redis.</summary>
        public const string ServerName = "bifrostql";
        public const string ServerVersion = "1.0.0";
        public const string ServerMode = "standalone";
        public const string ServerRole = "master";
    }
}
