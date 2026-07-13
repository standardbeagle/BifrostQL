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
                    // AuthenticationOk, ParameterStatus, BackendKeyData: skip.
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

        private static void WriteCString(Stream s, string text)
        {
            s.Write(Encoding.UTF8.GetBytes(text));
            s.WriteByte(0);
        }
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
