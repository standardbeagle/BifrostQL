using System.Security.Claims;
using System.Text;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// End-to-end connection-loop tests for the RESP front door, driven over a real loopback
    /// socket with a hand-written frontend. Proves the plumbing commands and — the load-bearing
    /// security facts — that AUTH only establishes a session when the login projects to a real
    /// Bifrost identity, that identity-bearing commands are refused with NOAUTH before AUTH, and
    /// that a subject-less / unmapped-issuer identity is rejected (never anonymous).
    ///
    /// <para>These tests are REPRESENTATIVE of a real Redis client's wire shapes (redis-cli sends
    /// exactly these command arrays). Confirming an actual redis-cli connects is a manual runbook
    /// item and is not claimed here.</para>
    /// </summary>
    public sealed class RespConnectionHandlerTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
        private const string User = "alice";
        private const string Secret = "s3cret";

        [Fact]
        public async Task Auth_ThenPing_ReturnsPong()
        {
            // Arrange
            await using var fixture = await StartAsync(WithUser());

            // Act
            await fixture.Client.SendCommandAsync("AUTH", User, Secret);
            var auth = await ReadAsync(fixture);
            await fixture.Client.SendCommandAsync("PING");
            var pong = await ReadAsync(fixture);

            // Assert
            auth.Should().BeOfType<RespSimpleString>().Which.Value.Should().Be("OK");
            pong.Should().BeOfType<RespSimpleString>().Which.Value.Should().Be("PONG");
        }

        [Fact]
        public async Task PingWithArgument_EchoesArgument()
        {
            // Arrange
            await using var fixture = await StartAsync(WithUser());
            await Authenticate(fixture);

            // Act
            await fixture.Client.SendCommandAsync("PING", "hello");
            var reply = await ReadAsync(fixture);

            // Assert
            Bulk(reply).Should().Be("hello");
        }

        [Fact]
        public async Task Ping_BeforeAuth_ReturnsNoAuth()
        {
            // Arrange
            await using var fixture = await StartAsync(WithUser());

            // Act
            await fixture.Client.SendCommandAsync("PING");
            var reply = await ReadAsync(fixture);

            // Assert: an identity-bearing command is refused until AUTH succeeds.
            Error(reply).Should().StartWith("NOAUTH");
        }

        [Fact]
        public async Task Hello3_WithInlineAuth_NegotiatesResp3_AndReturnsInfoMap()
        {
            // Arrange
            await using var fixture = await StartAsync(WithUser());

            // Act: HELLO 3 with inline AUTH negotiates the protocol and authenticates at once.
            await fixture.Client.SendCommandAsync("HELLO", "3", "AUTH", User, Secret);
            var reply = await ReadAsync(fixture);

            // Assert: a RESP3 map advertising proto 3 and the server identity.
            var info = MapToDictionary(reply);
            info["proto"].Should().BeOfType<RespInteger>().Which.Value.Should().Be(3);
            Bulk(info["server"]).Should().Be("bifrostql");

            // And the session is authenticated: a following identity-bearing command succeeds.
            await fixture.Client.SendCommandAsync("PING");
            (await ReadAsync(fixture)).Should().BeOfType<RespSimpleString>().Which.Value.Should().Be("PONG");
        }

        [Fact]
        public async Task Hello_InlineAuth_Resp2_AuthenticatesAndReturnsPairArray()
        {
            // Arrange
            await using var fixture = await StartAsync(WithUser());

            // Act: HELLO (no protover) AUTH user pass authenticates and stays RESP2.
            await fixture.Client.SendCommandAsync("HELLO", "AUTH", User, Secret);
            var reply = await ReadAsync(fixture);

            // Assert: RESP2 reply is a flat pair array; the session is authenticated.
            var info = ArrayPairsToDictionary(reply);
            info["proto"].Should().BeOfType<RespInteger>().Which.Value.Should().Be(2);
            await fixture.Client.SendCommandAsync("PING");
            (await ReadAsync(fixture)).Should().BeOfType<RespSimpleString>().Which.Value.Should().Be("PONG");
        }

        [Fact]
        public async Task Hello_BeforeAuth_WithoutInlineAuth_ReturnsNoAuth()
        {
            // Arrange
            await using var fixture = await StartAsync(WithUser());

            // Act
            await fixture.Client.SendCommandAsync("HELLO", "3");
            var reply = await ReadAsync(fixture);

            // Assert
            Error(reply).Should().StartWith("NOAUTH");
        }

        [Fact]
        public async Task Auth_WrongPassword_ReturnsWrongPass_AndEstablishesNoIdentity()
        {
            // Arrange
            await using var fixture = await StartAsync(WithUser());

            // Act
            await fixture.Client.SendCommandAsync("AUTH", User, "nope");
            var auth = await ReadAsync(fixture);
            await fixture.Client.SendCommandAsync("PING");
            var ping = await ReadAsync(fixture);

            // Assert: WRONGPASS, and no identity was established (the connection remains usable).
            Error(auth).Should().StartWith("WRONGPASS");
            Error(ping).Should().StartWith("NOAUTH");
        }

        [Fact]
        public async Task Auth_UnknownUser_ReturnsWrongPass()
        {
            // Arrange
            await using var fixture = await StartAsync(WithUser());

            // Act
            await fixture.Client.SendCommandAsync("AUTH", "ghost", "whatever");
            var reply = await ReadAsync(fixture);

            // Assert
            Error(reply).Should().StartWith("WRONGPASS");
        }

        [Fact]
        public async Task SubjectLessIdentity_IsRejected_NeverAnonymous()
        {
            // Arrange: the password is correct, but the mapped principal has no subject — the
            // authentication succeeds yet the identity must not, so AUTH fails closed.
            var store = new FakeRespCredentialStore().Add("svc", "key", SubjectLessPrincipal());
            await using var fixture = await StartAsync(store);

            // Act
            await fixture.Client.SendCommandAsync("AUTH", "svc", "key");
            var auth = await ReadAsync(fixture);
            await fixture.Client.SendCommandAsync("PING");
            var ping = await ReadAsync(fixture);

            // Assert: rejected, and the session never became authenticated.
            Error(auth).Should().StartWith("WRONGPASS");
            Error(ping).Should().StartWith("NOAUTH");
        }

        [Fact]
        public async Task UnmappedOidcIssuer_IsRejected_NeverAnonymous()
        {
            // Arrange: an OIDC claim-mapper registry is present but has no mapper for the token's
            // issuer — projecting it would silently drop tenant/role claims, so it must fail closed.
            var services = new ServiceCollection()
                .AddSingleton(new OidcClaimMapperRegistry(
                    Enumerable.Empty<KeyValuePair<string, IOidcClaimMapper>>()))
                .BuildServiceProvider();
            var store = new FakeRespCredentialStore().Add("svc", "key", UnmappedIssuerPrincipal());
            await using var fixture = await StartAsync(store, services);

            // Act
            await fixture.Client.SendCommandAsync("AUTH", "svc", "key");
            var reply = await ReadAsync(fixture);

            // Assert
            Error(reply).Should().StartWith("WRONGPASS");
        }

        [Fact]
        public async Task Select0_IsAccepted_NonZeroRejected()
        {
            // Arrange
            await using var fixture = await StartAsync(WithUser());
            await Authenticate(fixture);

            // Act: index 0 accepted, any other index rejected honestly (single-namespace front door).
            await fixture.Client.SendCommandAsync("SELECT", "0");
            var ok = await ReadAsync(fixture);
            await fixture.Client.SendCommandAsync("SELECT", "3");
            var rejected = await ReadAsync(fixture);

            // Assert
            ok.Should().BeOfType<RespSimpleString>().Which.Value.Should().Be("OK");
            Error(rejected).Should().StartWith("ERR DB index is out of range");
        }

        [Fact]
        public async Task Info_ReturnsParseablePayload()
        {
            // Arrange
            await using var fixture = await StartAsync(WithUser());
            await Authenticate(fixture);

            // Act
            await fixture.Client.SendCommandAsync("INFO");
            var reply = await ReadAsync(fixture);

            // Assert: a bulk string with a parseable "# Server" section and key:value lines.
            var payload = Bulk(reply);
            payload.Should().Contain("# Server");
            payload.Should().Contain("redis_version:");
            payload.Should().Contain("server_name:bifrostql");
        }

        [Fact]
        public async Task UnknownCommand_ReturnsErr_ProvingTheDataDispatchSeam()
        {
            // Arrange
            await using var fixture = await StartAsync(WithUser());
            await Authenticate(fixture);

            // Act: no data command handler is registered in slice 1, so GET is unknown.
            await fixture.Client.SendCommandAsync("GET", "foo");
            var reply = await ReadAsync(fixture);

            // Assert
            Error(reply).Should().StartWith("ERR unknown command 'GET'");
        }

        [Fact]
        public async Task MalformedFrame_ClosesConnection_WithProtocolError()
        {
            // Arrange
            await using var fixture = await StartAsync(WithUser());

            // Act: a bogus type marker is a wire violation.
            await fixture.Client.SendRawAsync(Encoding.ASCII.GetBytes("?not a frame\r\n"));
            var reply = await ReadAsync(fixture);

            // Assert: a clean protocol-error reply, then the connection closes (EOF).
            Error(reply).Should().StartWith("ERR Protocol error");
            (await fixture.Client.ReadReplyAsync().WaitAsync(Timeout))
                .Should().BeNull("Redis closes the connection after a protocol error");
        }

        [Fact]
        public async Task AnonymousMode_WhenExplicitlyConfigured_AllowsPingWithoutAuth()
        {
            // Arrange: the deliberate anonymous opt-in — RequireAuthentication cleared.
            await using var fixture = await StartAsync(WithUser(),
                options: new RespWireOptions { RequireAuthentication = false });

            // Act
            await fixture.Client.SendCommandAsync("PING");
            var reply = await ReadAsync(fixture);

            // Assert
            reply.Should().BeOfType<RespSimpleString>().Which.Value.Should().Be("PONG");
        }

        // ---- fixtures / helpers ----------------------------------------------

        private static FakeRespCredentialStore WithUser()
            => new FakeRespCredentialStore().Add(User, Secret, TenantPrincipal("user-alice", "tenant-a"));

        private static Task<RespFixture> StartAsync(
            IRespCredentialStore store, IServiceProvider? services = null, RespWireOptions? options = null)
            => RespFixture.StartAsync(store, services ?? RespFixture.EmptyServices(), options ?? new RespWireOptions());

        private static async Task Authenticate(RespFixture fixture)
        {
            await fixture.Client.SendCommandAsync("AUTH", User, Secret);
            var reply = await ReadAsync(fixture);
            reply.Should().BeOfType<RespSimpleString>("authentication must succeed for the arrange step");
        }

        private static async Task<RespValue> ReadAsync(RespFixture fixture)
        {
            var reply = await fixture.Client.ReadReplyAsync().WaitAsync(Timeout);
            reply.Should().NotBeNull("the server must answer every command");
            return reply!;
        }

        private static string Error(RespValue value)
            => value.Should().BeOfType<RespError>().Subject.Message;

        private static string? Bulk(RespValue value)
        {
            var bytes = value.Should().BeOfType<RespBulkString>().Subject.Value;
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }

        private static IReadOnlyDictionary<string, RespValue> MapToDictionary(RespValue value)
        {
            var entries = value.Should().BeOfType<RespMap>().Subject.Entries;
            return entries.ToDictionary(e => Encoding.UTF8.GetString(((RespBulkString)e.Key).Value!), e => e.Value);
        }

        private static IReadOnlyDictionary<string, RespValue> ArrayPairsToDictionary(RespValue value)
        {
            var items = value.Should().BeOfType<RespArray>().Subject.Items!;
            var dict = new Dictionary<string, RespValue>();
            for (var i = 0; i + 1 < items.Count; i += 2)
                dict[Encoding.UTF8.GetString(((RespBulkString)items[i]).Value!)] = items[i + 1];
            return dict;
        }

        private static ClaimsPrincipal TenantPrincipal(string userId, string tenantId) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(LocalAuthClaims.Tenant, tenantId),
            }, authenticationType: "resp"));

        private static ClaimsPrincipal SubjectLessPrincipal() =>
            new(new ClaimsIdentity(new[] { new Claim("scope", "read") }, authenticationType: "resp"));

        private static ClaimsPrincipal UnmappedIssuerPrincipal() =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "svc-1"),
                new Claim("iss", "https://evil.example/"),
            }, authenticationType: "resp"));
    }
}
