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

        // ---- HELLO option keywords ----
        public const string HelloAuthOption = "AUTH";
        public const string HelloSetNameOption = "SETNAME";

        /// <summary>The implicit username an <c>AUTH &lt;password&gt;</c> (no username) authenticates as, per Redis.</summary>
        public const string DefaultUser = "default";

        // ---- Canonical replies ----
        public const string Pong = "PONG";
        public const string Ok = "OK";
        public const string ResetReply = "RESET";

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
