namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Wire constants for the PostgreSQL frontend/backend protocol v3.0 as used by
    /// this front door's startup + authentication phase (slice 1). Only the message
    /// types the handshake exchanges are named here; the simple/extended query
    /// protocol constants arrive with the query-loop slice.
    ///
    /// <para>Reference: PostgreSQL protocol, section "Message Formats". Integers are
    /// big-endian; a typed message is <c>[Byte1 type][Int32 length incl. self][body]</c>;
    /// the untyped startup / SSLRequest packets are <c>[Int32 length incl. self][body]</c>.</para>
    /// </summary>
    internal static class PgWireProtocol
    {
        /// <summary>Protocol 3.0 version code sent in the StartupMessage (major 3, minor 0).</summary>
        public const int ProtocolVersion3 = 196608; // (3 << 16) | 0

        /// <summary>
        /// The magic version code an SSLRequest carries in place of a protocol version
        /// (80877103 = 1234 &lt;&lt; 16 | 5679). The packet is exactly 8 bytes.
        /// </summary>
        public const int SslRequestCode = 80877103;

        /// <summary>
        /// The magic code a GSSENCRequest carries (80877104). We do not offer GSSAPI
        /// encryption; the client is told 'N' and expected to fall back.
        /// </summary>
        public const int GssEncRequestCode = 80877104;

        /// <summary>The CancelRequest magic code (80877102); rejected during startup.</summary>
        public const int CancelRequestCode = 80877102;

        // ---- Backend (server → client) message type bytes ----
        public const byte AuthenticationRequest = (byte)'R';
        public const byte ErrorResponse = (byte)'E';
        public const byte ReadyForQuery = (byte)'Z';
        public const byte ParameterStatus = (byte)'S';
        public const byte BackendKeyData = (byte)'K';

        // ---- Frontend (client → server) message type bytes ----
        public const byte PasswordMessage = (byte)'p'; // also SASLInitialResponse / SASLResponse
        public const byte Terminate = (byte)'X';       // client asks to close the session

        // ---- Authentication request sub-codes (Int32 following the 'R' header) ----
        public const int AuthOk = 0;
        public const int AuthCleartextPassword = 3;
        public const int AuthSasl = 10;
        public const int AuthSaslContinue = 11;
        public const int AuthSaslFinal = 12;

        /// <summary>The only SASL mechanism this front door offers.</summary>
        public const string ScramSha256 = "SCRAM-SHA-256";

        // ---- ErrorResponse field codes (see protocol "Error and Notice Message Fields") ----
        public const byte ErrorFieldSeverity = (byte)'S';
        public const byte ErrorFieldSeverityNonLocalized = (byte)'V';
        public const byte ErrorFieldCode = (byte)'C';
        public const byte ErrorFieldMessage = (byte)'M';
        public const byte ErrorFieldTerminator = 0;

        // SQLSTATE codes used by the handshake.
        public const string SqlStateInvalidAuthorization = "28000"; // invalid_authorization_specification
        public const string SqlStateInvalidPassword = "28P01";      // invalid_password
        public const string SqlStateProtocolViolation = "08P01";    // protocol_violation
        public const string SqlStateFeatureNotSupported = "0A000";  // feature_not_supported
    }
}
