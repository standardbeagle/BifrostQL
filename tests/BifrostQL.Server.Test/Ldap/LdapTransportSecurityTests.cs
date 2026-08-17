using System.Security.Claims;
using System.Text;
using BifrostQL.Server;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Transport-security tests for the LDAP front door: credentials never cross a cleartext wire.
    /// A credentialed simple bind is refused BEFORE the presented credential is read, looked up, or
    /// compared unless the connection is confidential (LDAPS or a completed StartTLS) — the refusal
    /// is structural, not a remembered check inside the verifier.
    /// </summary>
    public sealed class LdapTransportSecurityTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        /// <summary>
        /// A credential store that records every lookup. The point of the recording is the negative
        /// assertion: on a cleartext connection the bind must be refused with the store never
        /// consulted — proving the gate runs ahead of the credential path rather than inside it.
        /// </summary>
        private sealed class RecordingStore : ILdapCredentialStore
        {
            public readonly List<string> Lookups = new();

            public Task<LdapCredentialRecord?> FindAsync(string bindDn, CancellationToken ct)
            {
                Lookups.Add(bindDn);
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.Name, "alice") }, "ldap"));
                return Task.FromResult<LdapCredentialRecord?>(
                    new LdapCredentialRecord("hash:s3cret", principal, Enabled: true));
            }
        }

        private sealed class Hasher : ILdapPasswordHasher
        {
            public string DecoyHash => "hash:$decoy$";
            public bool Verify(ReadOnlySpan<byte> password, string passwordHash) =>
                passwordHash != DecoyHash && passwordHash == "hash:" + Encoding.UTF8.GetString(password);
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

        /// <summary>confidentialityRequired (RFC 4511 §4.1.9); named as a literal until the enum carries it.</summary>
        private const LdapResultCode ConfidentialityRequired = (LdapResultCode)13;

        /// <summary>Collects warning-level log lines so a startup posture warning can be asserted.</summary>
        private sealed class CapturingLogger<T> : ILogger<T>
        {
            public List<string> Warnings { get; } = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                    Warnings.Add(formatter(state, exception));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }

        private static LdapBindAuthenticator Authenticator(RecordingStore store, LdapWireOptions options) =>
            new(store, new Hasher(), new Factory(), options);

        private static async Task<LdapResponse> ReadAsync(LdapFixture fixture)
        {
            var response = await fixture.Client.ReadResponseAsync().WaitAsync(Timeout);
            response.Should().NotBeNull("the server must answer this request");
            return response!;
        }

        [Fact]
        public async Task CredentialedSimpleBind_OverCleartext_IsRefused_WithoutConsultingTheCredentialStore()
        {
            var options = new LdapWireOptions();
            var store = new RecordingStore();
            await using var fixture = await LdapFixture.StartAsync(options, authenticator: Authenticator(store, options));

            // A bind whose DN and password WOULD verify, presented on a cleartext connection.
            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "uid=alice", password: "s3cret")));
            var response = await ReadAsync(fixture);

            response.OpTag.Should().Be(LdapProtocol.BindResponse);
            response.ResultCode.Should().Be(ConfidentialityRequired,
                "credentials must never be accepted over a cleartext transport");
            store.Lookups.Should().BeEmpty(
                "the transport gate must run BEFORE the presented credential is read or resolved");

            // The connection stays open so the client can StartTLS and retry.
            await fixture.Client.SendAsync(LdapWire.Message(2, LdapWire.SearchRequest()));
            (await ReadAsync(fixture)).MessageId.Should().Be(2);
        }

        [Fact]
        public async Task CredentialedSimpleBind_OverAConfidentialTransport_IsVerified()
        {
            var options = new LdapWireOptions();
            var store = new RecordingStore();
            await using var fixture = await LdapFixture.StartAsync(
                options, authenticator: Authenticator(store, options), tls: true);

            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "uid=alice", password: "s3cret")));
            var response = await ReadAsync(fixture);

            response.ResultCode.Should().Be(LdapResultCode.Success);
            store.Lookups.Should().ContainSingle("the credential path runs normally once the transport is confidential");
        }

        [Fact]
        public async Task AnonymousBind_OverCleartext_IsExemptFromTheGate()
        {
            // An anonymous bind carries no secret, so requiring confidentiality for it would protect
            // nothing while breaking RootDSE discovery — the one thing a cleartext client may do.
            var options = new LdapWireOptions { AnonymousBindEnabled = true };
            var store = new RecordingStore();
            await using var fixture = await LdapFixture.StartAsync(options, authenticator: Authenticator(store, options));

            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "", password: "")));
            var response = await ReadAsync(fixture);

            response.ResultCode.Should().Be(LdapResultCode.Success);
        }

        [Fact]
        public async Task CredentialedSimpleBind_OverCleartext_IsAdmitted_OnlyByTheExplicitInsecureOverride()
        {
            var options = new LdapWireOptions { AllowInsecureSimpleBind = true };
            var store = new RecordingStore();
            await using var fixture = await LdapFixture.StartAsync(options, authenticator: Authenticator(store, options));

            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "uid=alice", password: "s3cret")));

            (await ReadAsync(fixture)).ResultCode.Should().Be(LdapResultCode.Success);
            store.Lookups.Should().ContainSingle();
        }

        [Fact]
        public void InsecureSimpleBind_IsOffByDefault()
        {
            new LdapWireOptions().AllowInsecureSimpleBind.Should().BeFalse(
                "a cleartext credential path is never the default posture");
        }

        [Fact]
        public async Task InsecureSimpleBind_WhenEnabled_IsAnnouncedAtStartup()
        {
            var quiet = new CapturingLogger<LdapWireAdapter>();
            await new LdapWireAdapter(new LdapWireOptions(), quiet).StartAsync(CancellationToken.None);
            quiet.Warnings.Should().BeEmpty("the secure default needs no warning");

            var loud = new CapturingLogger<LdapWireAdapter>();
            await new LdapWireAdapter(new LdapWireOptions { AllowInsecureSimpleBind = true }, loud)
                .StartAsync(CancellationToken.None);

            loud.Warnings.Should().ContainSingle()
                .Which.Should().Contain("AllowInsecureSimpleBind",
                    "an operator must be told the front door accepts passwords in the clear");
        }
    }
}
