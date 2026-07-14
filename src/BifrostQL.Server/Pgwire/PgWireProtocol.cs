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

        // ---- Simple query protocol message type bytes (slice 2) ----
        public const byte RowDescription = (byte)'T';   // backend: result column layout
        public const byte DataRow = (byte)'D';          // backend: one result row (text values)
        public const byte CommandComplete = (byte)'C';  // backend: command tag, e.g. "SELECT 3"

        // ---- Frontend (client → server) message type bytes ----
        public const byte PasswordMessage = (byte)'p'; // also SASLInitialResponse / SASLResponse
        public const byte Query = (byte)'Q';           // simple query: a single SQL string
        public const byte Terminate = (byte)'X';       // client asks to close the session

        /// <summary>
        /// ReadyForQuery transaction-status byte for autocommit: 'I' = idle, not inside a
        /// transaction block. Slice 2 is autocommit only, so every ReadyForQuery is idle.
        /// </summary>
        public const byte TransactionStatusIdle = (byte)'I';

        /// <summary>
        /// Format code for a result column value in the simple query protocol. Simple
        /// query has no binary format negotiation — every value is text (0).
        /// </summary>
        public const short FormatText = 0;

        /// <summary>ErrorResponse/RowDescription/DataRow use a NULL value length of -1.</summary>
        public const int NullValueLength = -1;

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

        // SQLSTATE codes used by the simple query path (slice 2). A query-phase error
        // is non-fatal: it surfaces as a "ERROR"-severity ErrorResponse and the session
        // stays usable (autocommit), unlike the "FATAL" handshake rejections above.
        public const string SqlStateSyntaxError = "42601";    // syntax_error (unrecognized SQL)
        public const string SqlStateInternalError = "XX000";  // internal_error (execution fault)

        /// <summary>ErrorResponse severity for a handshake rejection: the connection closes.</summary>
        public const string SeverityFatal = "FATAL";

        /// <summary>ErrorResponse severity for a query error: the connection stays alive.</summary>
        public const string SeverityError = "ERROR";
    }
}
