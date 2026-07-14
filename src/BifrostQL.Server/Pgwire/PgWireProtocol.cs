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

        // ---- Extended query protocol backend message type bytes (slice 5) ----
        // Direction disambiguates the reused letters: '1'/'2'/'3'/'t'/'n'/'s' are
        // backend-only here, while the frontend Parse/Bind/Describe/Execute/Sync/Close
        // messages below share letters with unrelated backend types (e.g. frontend
        // Execute 'E' vs backend ErrorResponse 'E') but never on the same direction.
        public const byte ParseComplete = (byte)'1';         // backend: Parse succeeded
        public const byte BindComplete = (byte)'2';          // backend: Bind succeeded
        public const byte CloseComplete = (byte)'3';         // backend: Close succeeded
        public const byte ParameterDescription = (byte)'t';  // backend: prepared-statement param type OIDs
        public const byte NoData = (byte)'n';                // backend: statement/portal yields no rows
        public const byte PortalSuspended = (byte)'s';       // backend: Execute row-limit reached, more rows remain

        // ---- Frontend (client → server) message type bytes ----
        public const byte PasswordMessage = (byte)'p'; // also SASLInitialResponse / SASLResponse
        public const byte Query = (byte)'Q';           // simple query: a single SQL string
        public const byte Terminate = (byte)'X';       // client asks to close the session

        // ---- Extended query protocol frontend message type bytes (slice 5) ----
        public const byte ParseMessage = (byte)'P';    // frontend: prepare a named/unnamed statement
        public const byte BindMessage = (byte)'B';     // frontend: bind params into a portal
        public const byte DescribeMessage = (byte)'D'; // frontend: describe a statement ('S') or portal ('P')
        public const byte ExecuteMessage = (byte)'E';  // frontend: run a portal
        public const byte SyncMessage = (byte)'S';     // frontend: close the extended sequence → ReadyForQuery
        public const byte CloseMessage = (byte)'C';    // frontend: drop a statement ('S') or portal ('P')
        public const byte FlushMessage = (byte)'H';    // frontend: flush buffered output (no-op here — we flush each frame)

        /// <summary>Describe/Close target discriminator: a prepared statement.</summary>
        public const byte DescribeStatement = (byte)'S';

        /// <summary>Describe/Close target discriminator: a portal.</summary>
        public const byte DescribePortal = (byte)'P';

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

        // SQLSTATE codes used by the extended query protocol + connection admission (slice 5).
        public const string SqlStateQueryCanceled = "57014";        // query_canceled (CancelRequest matched)
        public const string SqlStateTooManyConnections = "53300";   // too_many_connections (over the limit)
        public const string SqlStateInvalidSqlStatementName = "26000"; // reference to an unknown prepared statement/portal

        /// <summary>
        /// Client-safe ErrorResponse message for a query aborted by a matching CancelRequest.
        /// This is the standard PostgreSQL wording; it carries no server-internal detail.
        /// </summary>
        public const string QueryCanceledMessage = "canceling statement due to user request";

        /// <summary>Client-safe ErrorResponse message when the connection limit is reached.</summary>
        public const string TooManyConnectionsMessage = "too many connections for the pgwire endpoint";

        /// <summary>
        /// Generic, client-safe ErrorResponse message for a query that failed with an
        /// unexpected or non-user-facing execution fault. The raw exception text (driver/DB
        /// identifiers, schema names, stack detail) is logged server-side and NEVER sent on
        /// the wire — only a deliberately user-facing query error (a translation/syntax
        /// error) forwards its curated message. Fail-closed toward this string.
        /// </summary>
        public const string InternalQueryErrorMessage = "internal error during query execution.";

        /// <summary>ErrorResponse severity for a handshake rejection: the connection closes.</summary>
        public const string SeverityFatal = "FATAL";

        /// <summary>ErrorResponse severity for a query error: the connection stays alive.</summary>
        public const string SeverityError = "ERROR";
    }
}
