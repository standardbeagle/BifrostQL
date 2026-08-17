using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BifrostQL.Server.Ldap;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// A decoded LDAP response envelope, reduced to what the connection-loop tests assert: the
    /// echoed message ID, the protocolOp application tag, and — for the result-bearing ops
    /// (BindResponse / SearchResultDone / ExtendedResponse) — the inlined result code.
    /// </summary>
    internal sealed record LdapResponse(int MessageId, byte OpTag, LdapResultCode? ResultCode);

    /// <summary>
    /// Hand-built LDAP request/BER bytes for the codec and connection tests. Requests are composed
    /// with the real <see cref="BerWriter"/> primitives (so the writer path is exercised too) but
    /// their protocolOp tags are LDAP request tags, so feeding them to <see cref="LdapMessageReader"/>
    /// or the connection handler travels the ACTUAL decode path a real client's bytes would.
    /// </summary>
    internal static class LdapWire
    {
        public static byte[] Message(int messageId, byte[] protocolOp, byte[]? controls = null)
            => controls is null
                ? BerWriter.Sequence(BerWriter.Integer(messageId), protocolOp)
                : BerWriter.Sequence(BerWriter.Integer(messageId), protocolOp, controls);

        public static byte[] BindRequest(int version = 3, string name = "", string password = "")
            => BerWriter.Tlv(LdapProtocol.BindRequest, BerWriter.Concat(
                BerWriter.Integer(version),
                BerWriter.OctetString(name),
                BerWriter.TaggedString(LdapProtocol.BindSimpleAuth, password)));

        public static byte[] UnbindRequest()
            => BerWriter.Tlv(LdapProtocol.UnbindRequest, Array.Empty<byte>());

        public static byte[] AbandonRequest(int targetMessageId)
            => BerWriter.Tlv(LdapProtocol.AbandonRequest, IntContent(targetMessageId));

        public static byte[] ExtendedRequest(string requestName)
            => BerWriter.Tlv(LdapProtocol.ExtendedRequest,
                BerWriter.TaggedString(LdapProtocol.ExtendedRequestName, requestName));

        /// <summary>A SearchRequest whose filter is supplied as pre-encoded BER (default: <c>(objectClass=*)</c>).</summary>
        public static byte[] SearchRequest(string baseObject = "", int scope = 0, byte[]? filter = null, string[]? attributes = null)
        {
            filter ??= FilterPresent("objectClass");
            var attrElements = (attributes ?? Array.Empty<string>())
                .Select(BerWriter.OctetString).ToArray();
            return BerWriter.Tlv(LdapProtocol.SearchRequest, BerWriter.Concat(
                BerWriter.OctetString(baseObject),
                BerWriter.Enumerated(scope),   // scope
                BerWriter.Enumerated(0),       // derefAliases
                BerWriter.Integer(0),          // sizeLimit
                BerWriter.Integer(0),          // timeLimit
                Boolean(false),                // typesOnly
                filter,
                BerWriter.Sequence(attrElements)));
        }

        // ---- filters ----
        public static byte[] FilterPresent(string attribute)
            => BerWriter.TaggedString(LdapProtocol.FilterPresent, attribute);

        public static byte[] FilterAnd(params byte[][] children)
            => BerWriter.Constructed(LdapProtocol.FilterAnd, children);

        public static byte[] FilterNot(byte[] child)
            => BerWriter.Constructed(LdapProtocol.FilterNot, child);

        /// <summary>A filter nested <paramref name="depth"/> connectives deep around a <c>present</c> leaf.</summary>
        public static byte[] NestedNotFilter(int depth)
        {
            var filter = FilterPresent("objectClass");
            for (var i = 0; i < depth; i++)
                filter = FilterNot(filter);
            return filter;
        }

        public static byte[] Boolean(bool value)
            => BerWriter.Tlv(LdapProtocol.Boolean, new[] { (byte)(value ? 0xFF : 0x00) });

        // ---- controls envelope ----
        public static byte[] Controls(params byte[][] controls)
            => BerWriter.Constructed(LdapProtocol.ControlsTag, controls);

        public static byte[] Control(string oid, bool criticality)
            => BerWriter.Sequence(BerWriter.OctetString(oid), Boolean(criticality));

        /// <summary>
        /// A control whose criticality Boolean carries ZERO content bytes (tag 0x01, len 0) — legal
        /// BER framing, empty primitive. Exercises the length-checked Boolean accessor: an unchecked
        /// <c>Content(...)[0]</c> would index-out-of-bounds on this input.
        /// </summary>
        public static byte[] ControlWithEmptyCriticalityBoolean(string oid)
            => BerWriter.Sequence(BerWriter.OctetString(oid), BerWriter.Tlv(LdapProtocol.Boolean, Array.Empty<byte>()));

        /// <summary>
        /// A control whose criticality Boolean carries MORE than one content byte. LDAP mandates the
        /// DER encoding of a BOOLEAN — exactly one octet — so this is a wire violation, not a second
        /// spelling of "true".
        /// </summary>
        public static byte[] ControlWithMultiByteCriticalityBoolean(string oid)
            => BerWriter.Sequence(BerWriter.OctetString(oid),
                BerWriter.Tlv(LdapProtocol.Boolean, new byte[] { 0xFF, 0x00, 0x00 }));

        // Minimal BER content bytes of a small non-negative integer (message IDs are small).
        private static byte[] IntContent(int value)
        {
            if (value is < 0 or > 0x7F)
                throw new ArgumentOutOfRangeException(nameof(value), "test helper handles small non-negative IDs only.");
            return new[] { (byte)value };
        }

        /// <summary>Parses one LDAP response envelope from raw bytes into <see cref="LdapResponse"/>.</summary>
        public static LdapResponse ParseResponse(byte[] envelope)
        {
            // The envelope is a SEQUENCE; step into its content and read messageID + protocolOp.
            var outer = new BerCursor(envelope, 0, envelope.Length);
            var seq = outer.Child(outer.ReadElement(LdapProtocol.Sequence));
            var messageId = seq.Int32(seq.ReadElement(LdapProtocol.Integer));
            var op = seq.ReadElement();

            LdapResultCode? resultCode = null;
            if (op.Tag is LdapProtocol.BindResponse or LdapProtocol.SearchResultDone or LdapProtocol.ExtendedResponse)
            {
                var opContent = seq.Child(op);
                resultCode = (LdapResultCode)opContent.Integer(opContent.ReadElement(LdapProtocol.Enumerated));
            }
            return new LdapResponse(messageId, op.Tag, resultCode);
        }
    }

    /// <summary>
    /// A hand-written LDAP frontend driving <see cref="LdapConnectionHandler"/> over a stream exactly
    /// as a real LDAP client would: request messages as raw BER, responses read a whole envelope at a
    /// time and decoded independently of the server's writer path.
    /// </summary>
    internal sealed class LdapTestClient
    {
        private Stream _stream;

        public LdapTestClient(Stream stream) => _stream = stream;

        /// <summary>
        /// Completes the client half of a TLS handshake on this connection, as a real client does
        /// after a successful StartTLS response (and as an LDAPS client does immediately). The
        /// server's certificate is self-signed in tests, so chain validation is accepted here — the
        /// facts under test are the server's state machine, not PKI.
        /// </summary>
        public async Task UpgradeToTlsAsync(CancellationToken ct = default)
        {
            var ssl = new SslStream(_stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, ct);
            _stream = ssl;
        }

        public async Task SendAsync(byte[] message)
        {
            await _stream.WriteAsync(message);
            await _stream.FlushAsync();
        }

        /// <summary>Reads one response envelope, or null on a clean EOF (the server closed).</summary>
        public async Task<LdapResponse?> ReadResponseAsync(CancellationToken ct = default)
        {
            var envelope = await ReadEnvelopeAsync(ct);
            return envelope is null ? null : LdapWire.ParseResponse(envelope);
        }

        private async Task<byte[]?> ReadEnvelopeAsync(CancellationToken ct)
        {
            var tag = await ReadByteAsync(ct);
            if (tag < 0)
                return null; // clean EOF
            if (tag != LdapProtocol.Sequence)
                throw new InvalidOperationException($"expected a SEQUENCE envelope, got 0x{tag:X2}.");

            var (length, lengthBytes) = await ReadLengthAsync(ct);
            var body = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await _stream.ReadAsync(body.AsMemory(offset, length - offset), ct);
                if (read == 0)
                    throw new InvalidOperationException("stream closed inside a response body.");
                offset += read;
            }

            // Re-materialize the whole envelope (tag + length + body) so ParseResponse sees a full element.
            var envelope = new byte[1 + lengthBytes.Length + length];
            envelope[0] = LdapProtocol.Sequence;
            lengthBytes.CopyTo(envelope, 1);
            body.CopyTo(envelope, 1 + lengthBytes.Length);
            return envelope;
        }

        private async Task<(int Length, byte[] Raw)> ReadLengthAsync(CancellationToken ct)
        {
            var first = await ReadByteAsync(ct);
            if (first < 0)
                throw new InvalidOperationException("stream closed inside a length.");
            if (first < 0x80)
                return (first, new[] { (byte)first });
            var count = first & 0x7F;
            var raw = new byte[1 + count];
            raw[0] = (byte)first;
            var length = 0;
            for (var i = 0; i < count; i++)
            {
                var b = await ReadByteAsync(ct);
                if (b < 0)
                    throw new InvalidOperationException("stream closed inside a long-form length.");
                raw[1 + i] = (byte)b;
                length = (length << 8) | b;
            }
            return (length, raw);
        }

        private async Task<int> ReadByteAsync(CancellationToken ct)
        {
            var one = new byte[1];
            var read = await _stream.ReadAsync(one.AsMemory(0, 1), ct);
            return read == 0 ? -1 : one[0];
        }
    }

    /// <summary>
    /// An ephemeral self-signed certificate for the loopback TLS tests, generated in-process so the
    /// suite carries no key material on disk and nothing to expire. One per test run.
    /// </summary>
    internal static class LdapTestCertificate
    {
        private static readonly Lazy<X509Certificate2> Lazy = new(Create, isThreadSafe: true);

        public static X509Certificate2 Instance => Lazy.Value;

        private static X509Certificate2 Create()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
            var subjectAlternativeName = new SubjectAlternativeNameBuilder();
            subjectAlternativeName.AddDnsName("localhost");
            request.CertificateExtensions.Add(subjectAlternativeName.Build());
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        }
    }

    /// <summary>Loopback socket pair with an <see cref="LdapConnectionHandler"/> pumping the server end.</summary>
    internal sealed class LdapFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _clientSocket;
        private readonly TcpClient _serverSocket;

        public LdapTestClient Client { get; }

        /// <summary>The server-side connection loop, so a test can assert it ends (cleanly, and without throwing).</summary>
        public Task ServerTask { get; }

        /// <summary>Cancels the server-side connection token, standing in for host shutdown.</summary>
        public CancellationTokenSource Cancellation { get; }

        private LdapFixture(
            TcpListener listener, TcpClient clientSocket, TcpClient serverSocket,
            Task serverTask, LdapTestClient client, CancellationTokenSource cancellation)
        {
            _listener = listener;
            _clientSocket = clientSocket;
            _serverSocket = serverSocket;
            ServerTask = serverTask;
            Client = client;
            Cancellation = cancellation;
        }

        /// <summary>
        /// Starts a loopback connection driven by a handler. Pass <paramref name="handler"/> to drive a
        /// handler composed elsewhere (e.g. resolved from DI, to prove the COMPOSED listener's
        /// behaviour); otherwise one is built from the supplied options/limiter/authenticator.
        /// </summary>
        public static async Task<LdapFixture> StartAsync(
            LdapWireOptions? options = null,
            LdapBoundedCounter? connectionLimiter = null,
            LdapBindAuthenticator? authenticator = null,
            LdapConnectionHandler? handler = null,
            bool tls = false)
        {
            options ??= new LdapWireOptions();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var clientSocket = new TcpClient();
            var connectTask = clientSocket.ConnectAsync(IPAddress.Loopback, port);
            var serverSocket = await listener.AcceptTcpClientAsync();
            await connectTask;

            // The handler gets whatever TLS the options describe, exactly as the registration builds
            // it: a fixture with a certificate can StartTLS, one without answers it unavailable.
            var connectionHandler = handler
                ?? new LdapConnectionHandler(options, connectionLimiter, authenticator, LdapTlsProvider.Create(options));
            var client = new LdapTestClient(clientSocket.GetStream());
            var cancellation = new CancellationTokenSource();

            // tls: the LDAPS shape — the handshake completes before the first LDAP byte, so the
            // session starts already confidential (no StartTLS involved).
            Stream serverStream = serverSocket.GetStream();
            if (tls)
            {
                var ssl = new SslStream(serverStream, leaveInnerStreamOpen: false);
                var serverHandshake = ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = LdapTestCertificate.Instance,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                });
                await Task.WhenAll(serverHandshake, client.UpgradeToTlsAsync());
                serverStream = ssl;
            }

            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await connectionHandler.HandleConnectionAsync(
                        serverStream, cancellation.Token, source: "test-client:1", tlsEstablished: tls);
                }
                finally { serverSocket.Close(); }
            });
            return new LdapFixture(listener, clientSocket, serverSocket, serverTask, client, cancellation);
        }

        public async ValueTask DisposeAsync()
        {
            _clientSocket.Dispose();
            try { await ServerTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* teardown races are expected on dispose */ }
            _serverSocket.Dispose();
            _listener.Stop();
            Cancellation.Dispose();
        }
    }
}
