using System.Security.Claims;
using System.Text;
using BifrostQL.Server;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// End-to-end bind tests over a real loopback socket, proving the authenticator is wired into the
    /// connection loop: a valid simple bind answers Success, every failure answers the SAME uniform
    /// invalidCredentials on the wire, and the connection stays usable for retry. The codec-only
    /// listener (no authenticator) still refuses binds with unwillingToPerform — covered by
    /// <see cref="LdapConnectionHandlerTests"/>.
    /// </summary>
    public sealed class LdapBindWireTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private sealed class Hasher : ILdapPasswordHasher
        {
            public string DecoyHash => "hash:$decoy$";
            public bool Verify(ReadOnlySpan<byte> password, string passwordHash) =>
                passwordHash != DecoyHash && passwordHash == "hash:" + Encoding.UTF8.GetString(password);
        }

        private sealed class Store : ILdapCredentialStore
        {
            public Task<LdapCredentialRecord?> FindAsync(string bindDn, CancellationToken ct)
            {
                if (!string.Equals(bindDn, "uid=alice", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<LdapCredentialRecord?>(null);
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.Name, "alice") }, "ldap"));
                return Task.FromResult<LdapCredentialRecord?>(
                    new LdapCredentialRecord("hash:s3cret", principal, Enabled: true));
            }
        }

        private sealed class Factory : IBifrostAuthContextFactory
        {
            public IDictionary<string, object?> CreateUserContext(HttpContext context)
            {
                var sub = context.User.FindFirst(ClaimTypes.Name)?.Value;
                return string.IsNullOrEmpty(sub)
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?> { ["sub"] = sub };
            }

            public IDictionary<string, object?> CreateUserContext(HttpContext context, IDictionary<string, object?> existing)
                => CreateUserContext(context);
        }

        private static LdapBindAuthenticator Authenticator(LdapWireOptions? options = null) =>
            new(new Store(), new Hasher(), new Factory(), options ?? new LdapWireOptions());

        private static async Task<LdapResponse> ReadAsync(LdapFixture fixture)
        {
            var response = await fixture.Client.ReadResponseAsync().WaitAsync(Timeout);
            response.Should().NotBeNull();
            return response!;
        }

        [Fact]
        public async Task ValidSimpleBind_AnswersSuccess_ConnectionStaysOpen()
        {
            await using var fixture = await LdapFixture.StartAsync(authenticator: Authenticator(), tls: true);

            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "uid=alice", password: "s3cret")));
            var response = await ReadAsync(fixture);

            response.MessageId.Should().Be(1);
            response.OpTag.Should().Be(LdapProtocol.BindResponse);
            response.ResultCode.Should().Be(LdapResultCode.Success);

            // Still usable after a successful bind.
            await fixture.Client.SendAsync(LdapWire.Message(2, LdapWire.SearchRequest()));
            (await ReadAsync(fixture)).MessageId.Should().Be(2);
        }

        [Theory]
        [InlineData("uid=ghost", "whatever")] // unknown DN
        [InlineData("uid=alice", "wrong")]    // wrong password
        public async Task FailedSimpleBind_AnswersUniform_InvalidCredentials_ConnectionStaysOpen(string dn, string password)
        {
            await using var fixture = await LdapFixture.StartAsync(authenticator: Authenticator(), tls: true);

            await fixture.Client.SendAsync(LdapWire.Message(9, LdapWire.BindRequest(name: dn, password: password)));
            var response = await ReadAsync(fixture);

            response.OpTag.Should().Be(LdapProtocol.BindResponse);
            response.ResultCode.Should().Be(LdapResultCode.InvalidCredentials,
                "every failure class is byte-identical on the wire");

            // A failed bind leaves the connection open for a retry.
            await fixture.Client.SendAsync(LdapWire.Message(10, LdapWire.BindRequest(name: "uid=alice", password: "s3cret")));
            var retry = await ReadAsync(fixture);
            retry.MessageId.Should().Be(10);
            retry.ResultCode.Should().Be(LdapResultCode.Success);
        }

        [Fact]
        public async Task UnauthenticatedConnection_IsClosed_AtThePreAuthDeadline()
        {
            // A peer holds an admission slot from ACCEPT. Failing binds is traffic, so the idle
            // timeout never fires — without a pre-auth deadline such a peer keeps its slot for the
            // whole idle window (30 s here) while never authenticating.
            var options = new LdapWireOptions
            {
                AuthenticationTimeout = TimeSpan.FromMilliseconds(300),
                IdleTimeout = TimeSpan.FromSeconds(30),
            };
            await using var fixture = await LdapFixture.StartAsync(options, authenticator: Authenticator(options), tls: true);

            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "uid=alice", password: "wrong")));
            (await ReadAsync(fixture)).ResultCode.Should().Be(LdapResultCode.InvalidCredentials);

            (await fixture.Client.ReadResponseAsync().WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().BeNull("a connection that has not authenticated is closed at the pre-auth deadline");
        }

        [Fact]
        public async Task AuthenticatedConnection_SurvivesPastThePreAuthDeadline()
        {
            // The deadline reclaims slots from UNAUTHENTICATED peers only; an authenticated session is
            // a legitimate client session, bounded thereafter by the idle timeout.
            var options = new LdapWireOptions
            {
                AuthenticationTimeout = TimeSpan.FromMilliseconds(300),
                IdleTimeout = TimeSpan.FromSeconds(30),
            };
            await using var fixture = await LdapFixture.StartAsync(options, authenticator: Authenticator(options), tls: true);

            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "uid=alice", password: "s3cret")));
            (await ReadAsync(fixture)).ResultCode.Should().Be(LdapResultCode.Success);

            await Task.Delay(TimeSpan.FromMilliseconds(900));

            await fixture.Client.SendAsync(LdapWire.Message(2, LdapWire.SearchRequest(baseObject: "dc=example,dc=com")));
            (await ReadAsync(fixture)).MessageId.Should().Be(2, "an authenticated session outlives the pre-auth deadline");
        }

        [Fact]
        public async Task AnonymousBind_DefaultOff_AnswersInvalidCredentials()
        {
            await using var fixture = await LdapFixture.StartAsync(authenticator: Authenticator(), tls: true);

            await fixture.Client.SendAsync(LdapWire.Message(3, LdapWire.BindRequest(name: "", password: "")));
            var response = await ReadAsync(fixture);

            response.OpTag.Should().Be(LdapProtocol.BindResponse);
            response.ResultCode.Should().Be(LdapResultCode.InvalidCredentials);
        }

        [Fact]
        public async Task AnonymousBind_WhenEnabled_AnswersSuccess()
        {
            await using var fixture = await LdapFixture.StartAsync(
                authenticator: Authenticator(new LdapWireOptions { AnonymousBindEnabled = true }), tls: true);

            await fixture.Client.SendAsync(LdapWire.Message(4, LdapWire.BindRequest(name: "", password: "")));
            var response = await ReadAsync(fixture);

            response.OpTag.Should().Be(LdapProtocol.BindResponse);
            response.ResultCode.Should().Be(LdapResultCode.Success);
        }

        [Fact]
        public async Task AnonymousSession_SearchingDirectoryData_IsRefused_InsufficientAccessRights()
        {
            // Criterion 4's second half: an ADMITTED anonymous session reads only the RootDSE and the
            // subschema. A data-scoped base is refused for lack of rights — a session-state decision,
            // distinct from the listener-wide "search is not enabled" refusal.
            var options = new LdapWireOptions { AnonymousBindEnabled = true };
            await using var fixture = await LdapFixture.StartAsync(options, authenticator: Authenticator(options), tls: true);

            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "", password: "")));
            (await ReadAsync(fixture)).ResultCode.Should().Be(LdapResultCode.Success);

            await fixture.Client.SendAsync(LdapWire.Message(2, LdapWire.SearchRequest(baseObject: "dc=example,dc=com")));
            var response = await ReadAsync(fixture);

            response.OpTag.Should().Be(LdapProtocol.SearchResultDone);
            response.ResultCode.Should().Be(LdapResultCode.InsufficientAccessRights,
                "an anonymous session may not reach directory data");
        }

        [Theory]
        [InlineData("")]              // RootDSE
        [InlineData("cn=subschema")]  // subschema subentry
        [InlineData("CN=SubSchema")]  // DNs are case-insensitive
        public async Task AnonymousSession_ReadingDiscoverySurface_IsNotRefusedForRights(string baseObject)
        {
            var options = new LdapWireOptions { AnonymousBindEnabled = true };
            await using var fixture = await LdapFixture.StartAsync(options, authenticator: Authenticator(options), tls: true);

            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "", password: "")));
            (await ReadAsync(fixture)).ResultCode.Should().Be(LdapResultCode.Success);

            await fixture.Client.SendAsync(LdapWire.Message(2, LdapWire.SearchRequest(baseObject: baseObject)));
            var response = await ReadAsync(fixture);

            // Search execution itself arrives with the search slice; what is pinned here is that the
            // discovery surface is NOT the rights-refused path an anonymous data search takes.
            response.ResultCode.Should().Be(LdapResultCode.UnwillingToPerform);
        }

        [Fact]
        public async Task CredentialedSession_SearchingDirectoryData_IsNotRefusedForRights()
        {
            // The restriction is anonymous-specific: an authenticated identity is not rights-refused
            // here (its data access is the search slice's policy decision, not this gate's).
            await using var fixture = await LdapFixture.StartAsync(authenticator: Authenticator(), tls: true);

            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "uid=alice", password: "s3cret")));
            (await ReadAsync(fixture)).ResultCode.Should().Be(LdapResultCode.Success);

            await fixture.Client.SendAsync(LdapWire.Message(2, LdapWire.SearchRequest(baseObject: "dc=example,dc=com")));
            (await ReadAsync(fixture)).ResultCode.Should().Be(LdapResultCode.UnwillingToPerform);
        }
    }
}
