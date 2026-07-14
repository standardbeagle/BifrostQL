using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Claims;
using System.Text;
using BifrostQL.Server.Pgwire;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>The outcome of driving a handshake to completion or rejection.</summary>
    internal sealed record HandshakeResult(bool ReadyForQuery, string? ErrorSqlState, string? ErrorMessage)
    {
        public bool WasRejected => ErrorSqlState is not null;
    }

    /// <summary>
    /// A hand-written PostgreSQL frontend that drives the pgwire handler over a stream,
    /// exactly as psql/a driver would: SSLRequest + TLS, StartupMessage, cleartext or
    /// SCRAM auth, then reads to ReadyForQuery or ErrorResponse. Independent of the
    /// server codec so it genuinely exercises the wire.
    /// </summary>
    internal sealed class PgHandshakeClient
    {
        private Stream _stream;

        public PgHandshakeClient(Stream stream) => _stream = stream;

        /// <summary>Backend PID from the captured BackendKeyData (0 until the handshake completes).</summary>
        public int BackendPid { get; private set; }

        /// <summary>Backend secret key from the captured BackendKeyData, echoed in a CancelRequest.</summary>
        public int BackendSecret { get; private set; }

        public async Task NegotiateTlsAsync()
        {
            // SSLRequest: [Int32 len=8][Int32 code].
            var packet = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), 8);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), PgWireProtocol.SslRequestCode);
            await _stream.WriteAsync(packet);
            await _stream.FlushAsync();

            var response = new byte[1];
            await _stream.ReadExactlyAsync(response);
            if (response[0] != (byte)'S')
                throw new InvalidOperationException($"Server declined TLS with '{(char)response[0]}'.");

            var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            });
            _stream = ssl;
        }

        public async Task SendStartupAsync(string user, string database = "bifrost")
        {
            using var body = new MemoryStream();
            var version = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(version, PgWireProtocol.ProtocolVersion3);
            body.Write(version);
            WriteCString(body, "user"); WriteCString(body, user);
            WriteCString(body, "database"); WriteCString(body, database);
            body.WriteByte(0); // params terminator

            var payload = body.ToArray();
            var packet = new byte[4 + payload.Length];
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), packet.Length);
            payload.CopyTo(packet.AsSpan(4));
            await _stream.WriteAsync(packet);
            await _stream.FlushAsync();
        }

        /// <summary>Sends a malformed startup packet (unsupported protocol code).</summary>
        public async Task SendBadStartupAsync()
        {
            var packet = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), 8);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), 999999);
            await _stream.WriteAsync(packet);
            await _stream.FlushAsync();
        }

        public async Task DoCleartextAsync(string password)
        {
            var (type, body) = await ReadBackendAsync();
            RequireAuth(type, body, PgWireProtocol.AuthCleartextPassword);

            using var msg = new MemoryStream();
            WriteCString(msg, password);
            await WriteFrontendAsync(PgWireProtocol.PasswordMessage, msg.ToArray());
        }

        public async Task DoScramAsync(string password)
        {
            var (type, body) = await ReadBackendAsync();
            RequireAuth(type, body, PgWireProtocol.AuthSasl);

            var client = new ScramTestClient();
            var clientFirst = client.ClientFirstMessage();

            using var initial = new MemoryStream();
            WriteCString(initial, PgWireProtocol.ScramSha256);
            var clientFirstBytes = Encoding.UTF8.GetBytes(clientFirst);
            var len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, clientFirstBytes.Length);
            initial.Write(len);
            initial.Write(clientFirstBytes);
            await WriteFrontendAsync(PgWireProtocol.PasswordMessage, initial.ToArray());

            var (contType, contBody) = await ReadBackendAsync();
            var serverFirst = RequireAuthText(contType, contBody, PgWireProtocol.AuthSaslContinue);

            var clientFinal = client.ClientFinalMessage(serverFirst, password, out _);
            await WriteFrontendAsync(PgWireProtocol.PasswordMessage, Encoding.UTF8.GetBytes(clientFinal));

            var (finType, finBody) = await ReadBackendAsync();
            RequireAuthText(finType, finBody, PgWireProtocol.AuthSaslFinal);
        }

        /// <summary>
        /// Drives the SASL handshake but sends a malformed client-first-message (a valid
        /// GS2 header with no <c>r=</c> nonce) so the server rejects it as a protocol
        /// violation instead of continuing the exchange.
        /// </summary>
        public async Task SendMalformedScramFirstAsync()
        {
            var (type, body) = await ReadBackendAsync();
            RequireAuth(type, body, PgWireProtocol.AuthSasl);

            const string malformedClientFirst = "n,,n=user"; // GS2 header present, r= nonce missing
            using var initial = new MemoryStream();
            WriteCString(initial, PgWireProtocol.ScramSha256);
            var clientFirstBytes = Encoding.UTF8.GetBytes(malformedClientFirst);
            var len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, clientFirstBytes.Length);
            initial.Write(len);
            initial.Write(clientFirstBytes);
            await WriteFrontendAsync(PgWireProtocol.PasswordMessage, initial.ToArray());
        }

        /// <summary>Sends a simple Query ('Q') message: the SQL string as a C-string body.</summary>
        public async Task SendQueryAsync(string sql)
        {
            using var body = new MemoryStream();
            WriteCString(body, sql);
            await WriteFrontendAsync(PgWireProtocol.Query, body.ToArray());
        }

        /// <summary>
        /// Reads a full simple-query response cycle: the RowDescription (if any), every
        /// DataRow, the CommandComplete tag or an ErrorResponse, up to and including the
        /// terminating ReadyForQuery. Decodes each DataRow value as UTF-8 text (NULL → null).
        /// </summary>
        public async Task<SimpleQueryResult> ReadQueryResultAsync()
        {
            var fields = new List<PgFieldDescription>();
            var rows = new List<IReadOnlyList<string?>>();
            string? commandTag = null;
            string? errorSqlState = null;
            string? errorMessage = null;

            while (true)
            {
                var (type, body) = await ReadBackendAsync();
                switch (type)
                {
                    case PgWireProtocol.RowDescription:
                        fields.AddRange(ParseRowDescription(body));
                        break;
                    case PgWireProtocol.DataRow:
                        rows.Add(ParseDataRow(body));
                        break;
                    case PgWireProtocol.CommandComplete:
                        commandTag = ReadCString(body);
                        break;
                    case PgWireProtocol.ErrorResponse:
                        (errorSqlState, errorMessage) = ParseError(body);
                        break;
                    case PgWireProtocol.ReadyForQuery:
                        return new SimpleQueryResult(fields, rows, commandTag, errorSqlState, errorMessage,
                            TransactionStatus: (char)body[0]);
                }
            }
        }

        private static List<PgFieldDescription> ParseRowDescription(byte[] body)
        {
            var count = BinaryPrimitives.ReadInt16BigEndian(body.AsSpan(0, 2));
            var fields = new List<PgFieldDescription>(count);
            var offset = 2;
            for (var i = 0; i < count; i++)
            {
                var start = offset;
                while (body[offset] != 0) offset++;
                var name = Encoding.UTF8.GetString(body, start, offset - start);
                offset++; // terminator
                offset += 4; // table OID
                offset += 2; // column attribute number
                var typeOid = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(offset, 4)); offset += 4;
                var typeLen = BinaryPrimitives.ReadInt16BigEndian(body.AsSpan(offset, 2)); offset += 2;
                offset += 4; // type modifier
                var format = BinaryPrimitives.ReadInt16BigEndian(body.AsSpan(offset, 2)); offset += 2;
                fields.Add(new PgFieldDescription(name, typeOid, typeLen, format));
            }
            return fields;
        }

        private static List<string?> ParseDataRow(byte[] body)
        {
            var count = BinaryPrimitives.ReadInt16BigEndian(body.AsSpan(0, 2));
            var values = new List<string?>(count);
            var offset = 2;
            for (var i = 0; i < count; i++)
            {
                var len = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(offset, 4)); offset += 4;
                if (len < 0) { values.Add(null); continue; }
                values.Add(Encoding.UTF8.GetString(body, offset, len));
                offset += len;
            }
            return values;
        }

        private static string ReadCString(byte[] body)
        {
            var end = Array.IndexOf(body, (byte)0);
            return Encoding.UTF8.GetString(body, 0, end < 0 ? body.Length : end);
        }

        /// <summary>Reads backend messages until ReadyForQuery (success) or ErrorResponse (rejection).</summary>
        public async Task<HandshakeResult> WaitForReadyOrErrorAsync()
        {
            while (true)
            {
                var (type, body) = await ReadBackendAsync();
                switch (type)
                {
                    case PgWireProtocol.ReadyForQuery:
                        return new HandshakeResult(true, null, null);
                    case PgWireProtocol.ErrorResponse:
                        var (code, message) = ParseError(body);
                        return new HandshakeResult(false, code, message);
                    case PgWireProtocol.BackendKeyData:
                        BackendPid = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(0, 4));
                        BackendSecret = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(4, 4));
                        break;
                    // AuthenticationOk, ParameterStatus: skip.
                }
            }
        }

        private static void RequireAuth(byte type, byte[] body, int expectedSubCode)
        {
            if (type != PgWireProtocol.AuthenticationRequest)
                throw new InvalidOperationException($"Expected AuthenticationRequest 'R', got '{(char)type}'.");
            var subCode = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(0, 4));
            if (subCode != expectedSubCode)
                throw new InvalidOperationException($"Expected auth sub-code {expectedSubCode}, got {subCode}.");
        }

        private static string RequireAuthText(byte type, byte[] body, int expectedSubCode)
        {
            RequireAuth(type, body, expectedSubCode);
            return Encoding.UTF8.GetString(body, 4, body.Length - 4);
        }

        private async Task<(byte Type, byte[] Body)> ReadBackendAsync()
        {
            var header = new byte[5];
            await _stream.ReadExactlyAsync(header);
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
            var body = new byte[length - 4];
            if (body.Length > 0)
                await _stream.ReadExactlyAsync(body);
            return (header[0], body);
        }

        private async Task WriteFrontendAsync(byte type, byte[] body)
        {
            var frame = new byte[5 + body.Length];
            frame[0] = type;
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), 4 + body.Length);
            body.CopyTo(frame.AsSpan(5));
            await _stream.WriteAsync(frame);
            await _stream.FlushAsync();
        }

        private static (string Code, string Message) ParseError(byte[] body)
        {
            string code = "", message = "";
            var offset = 0;
            while (offset < body.Length && body[offset] != 0)
            {
                var field = body[offset++];
                var start = offset;
                while (offset < body.Length && body[offset] != 0) offset++;
                var value = Encoding.UTF8.GetString(body, start, offset - start);
                if (offset < body.Length) offset++;
                if (field == PgWireProtocol.ErrorFieldCode) code = value;
                else if (field == PgWireProtocol.ErrorFieldMessage) message = value;
            }
            return (code, message);
        }

        // ---- Extended query protocol (slice 5) ------------------------------

        /// <summary>Parse (P): statement name, SQL, and optional parameter type OIDs.</summary>
        public async Task SendParseAsync(string statementName, string sql, params int[] paramTypeOids)
        {
            using var body = new MemoryStream();
            WriteCString(body, statementName);
            WriteCString(body, sql);
            WriteInt16(body, (short)paramTypeOids.Length);
            foreach (var oid in paramTypeOids) WriteInt32(body, oid);
            await WriteFrontendAsync(PgWireProtocol.ParseMessage, body.ToArray());
        }

        /// <summary>
        /// Bind (B): binds text-format parameter values into a portal. A null entry encodes a
        /// SQL NULL. Uses the all-text format (no format codes) for params and results.
        /// </summary>
        public async Task SendBindAsync(string portalName, string statementName, params string?[] textValues)
        {
            using var body = new MemoryStream();
            WriteCString(body, portalName);
            WriteCString(body, statementName);
            WriteInt16(body, 0); // parameter format codes: none → all text
            WriteInt16(body, (short)textValues.Length);
            foreach (var value in textValues)
            {
                if (value is null) { WriteInt32(body, -1); continue; }
                var bytes = Encoding.UTF8.GetBytes(value);
                WriteInt32(body, bytes.Length);
                body.Write(bytes);
            }
            WriteInt16(body, 0); // result format codes: none → all text
            await WriteFrontendAsync(PgWireProtocol.BindMessage, body.ToArray());
        }

        /// <summary>Bind that requests a BINARY result format (to exercise the honest rejection).</summary>
        public async Task SendBindBinaryResultAsync(string portalName, string statementName)
        {
            using var body = new MemoryStream();
            WriteCString(body, portalName);
            WriteCString(body, statementName);
            WriteInt16(body, 0);   // param format codes: none
            WriteInt16(body, 0);   // param values: none
            WriteInt16(body, 1);   // one result format code…
            WriteInt16(body, 1);   // …= binary
            await WriteFrontendAsync(PgWireProtocol.BindMessage, body.ToArray());
        }

        public async Task SendDescribeStatementAsync(string statementName)
            => await SendDescribeAsync(PgWireProtocol.DescribeStatement, statementName);

        public async Task SendDescribePortalAsync(string portalName)
            => await SendDescribeAsync(PgWireProtocol.DescribePortal, portalName);

        private async Task SendDescribeAsync(byte target, string name)
        {
            using var body = new MemoryStream();
            body.WriteByte(target);
            WriteCString(body, name);
            await WriteFrontendAsync(PgWireProtocol.DescribeMessage, body.ToArray());
        }

        /// <summary>Execute (E): run a portal, optionally capped at <paramref name="maxRows"/> (0 = all).</summary>
        public async Task SendExecuteAsync(string portalName, int maxRows = 0)
        {
            using var body = new MemoryStream();
            WriteCString(body, portalName);
            WriteInt32(body, maxRows);
            await WriteFrontendAsync(PgWireProtocol.ExecuteMessage, body.ToArray());
        }

        public async Task SendCloseStatementAsync(string statementName)
        {
            using var body = new MemoryStream();
            body.WriteByte(PgWireProtocol.DescribeStatement);
            WriteCString(body, statementName);
            await WriteFrontendAsync(PgWireProtocol.CloseMessage, body.ToArray());
        }

        public async Task SendSyncAsync() => await WriteFrontendAsync(PgWireProtocol.SyncMessage, Array.Empty<byte>());

        /// <summary>
        /// Reads a whole extended-protocol response cycle up to and including ReadyForQuery,
        /// decoding each backend message into <see cref="ExtendedResult"/>.
        /// </summary>
        public async Task<ExtendedResult> ReadExtendedUntilReadyAsync()
        {
            var order = new List<byte>();
            var fields = new List<PgFieldDescription>();
            var rows = new List<IReadOnlyList<string?>>();
            var paramOids = new List<int>();
            string? commandTag = null, errorSqlState = null, errorMessage = null;
            bool parseComplete = false, bindComplete = false, closeComplete = false, noData = false, portalSuspended = false;

            while (true)
            {
                var (type, body) = await ReadBackendAsync();
                order.Add(type);
                switch (type)
                {
                    case PgWireProtocol.ParseComplete: parseComplete = true; break;
                    case PgWireProtocol.BindComplete: bindComplete = true; break;
                    case PgWireProtocol.CloseComplete: closeComplete = true; break;
                    case PgWireProtocol.NoData: noData = true; break;
                    case PgWireProtocol.PortalSuspended: portalSuspended = true; break;
                    case PgWireProtocol.ParameterDescription: paramOids.AddRange(ParseParameterDescription(body)); break;
                    case PgWireProtocol.RowDescription: fields.AddRange(ParseRowDescription(body)); break;
                    case PgWireProtocol.DataRow: rows.Add(ParseDataRow(body)); break;
                    case PgWireProtocol.CommandComplete: commandTag = ReadCString(body); break;
                    case PgWireProtocol.ErrorResponse: (errorSqlState, errorMessage) = ParseError(body); break;
                    case PgWireProtocol.ReadyForQuery:
                        return new ExtendedResult(order, parseComplete, bindComplete, closeComplete, noData,
                            portalSuspended, paramOids, fields, rows, commandTag, errorSqlState, errorMessage,
                            TransactionStatus: (char)body[0]);
                }
            }
        }

        private static List<int> ParseParameterDescription(byte[] body)
        {
            var count = BinaryPrimitives.ReadInt16BigEndian(body.AsSpan(0, 2));
            var oids = new List<int>(count);
            var offset = 2;
            for (var i = 0; i < count; i++) { oids.Add(BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(offset, 4))); offset += 4; }
            return oids;
        }

        /// <summary>
        /// Opens a fresh connection to <paramref name="endpoint"/> and sends a bare
        /// CancelRequest (<c>[Int32 len=16][Int32 code][Int32 pid][Int32 secret]</c>), then
        /// closes — exactly as a client's cancel path does on a second socket.
        /// </summary>
        public static async Task SendCancelRequestAsync(System.Net.IPEndPoint endpoint, int pid, int secret)
        {
            using var socket = new System.Net.Sockets.TcpClient();
            await socket.ConnectAsync(endpoint.Address, endpoint.Port);
            await using var stream = socket.GetStream();
            var packet = new byte[16];
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), 16);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), PgWireProtocol.CancelRequestCode);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), pid);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12, 4), secret);
            await stream.WriteAsync(packet);
            await stream.FlushAsync();
        }

        private static void WriteInt16(Stream s, short value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(buffer, value);
            s.Write(buffer);
        }

        private static void WriteInt32(Stream s, int value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            s.Write(buffer);
        }

        private static void WriteCString(Stream s, string text)
        {
            s.Write(Encoding.UTF8.GetBytes(text));
            s.WriteByte(0);
        }
    }

    /// <summary>A fully decoded extended-protocol response cycle (through ReadyForQuery).</summary>
    internal sealed record ExtendedResult(
        IReadOnlyList<byte> MessageOrder,
        bool ParseComplete,
        bool BindComplete,
        bool CloseComplete,
        bool NoData,
        bool PortalSuspended,
        IReadOnlyList<int> ParameterTypeOids,
        IReadOnlyList<PgFieldDescription> Fields,
        IReadOnlyList<IReadOnlyList<string?>> Rows,
        string? CommandTag,
        string? ErrorSqlState,
        string? ErrorMessage,
        char TransactionStatus)
    {
        public bool HasError => ErrorSqlState is not null;
    }

    /// <summary>One decoded RowDescription field: name and advertised pg type facts.</summary>
    internal sealed record PgFieldDescription(string Name, int TypeOid, short TypeLength, short FormatCode);

    /// <summary>A fully decoded simple-query response cycle (through ReadyForQuery).</summary>
    internal sealed record SimpleQueryResult(
        IReadOnlyList<PgFieldDescription> Fields,
        IReadOnlyList<IReadOnlyList<string?>> Rows,
        string? CommandTag,
        string? ErrorSqlState,
        string? ErrorMessage,
        char TransactionStatus)
    {
        public bool HasError => ErrorSqlState is not null;
    }

    /// <summary>A test credential store: an in-memory username → (secret, principal) map.</summary>
    internal sealed class FakePgCredentialStore : IPgCredentialStore
    {
        private readonly Dictionary<string, PgLogin> _logins = new(StringComparer.Ordinal);

        public FakePgCredentialStore Add(string username, string secret, ClaimsPrincipal principal)
        {
            _logins[username] = new PgLogin(secret, principal);
            return this;
        }

        public Task<PgLogin?> FindAsync(string username, CancellationToken cancellationToken)
            => Task.FromResult(_logins.TryGetValue(username, out var login) ? login : null);
    }
}
