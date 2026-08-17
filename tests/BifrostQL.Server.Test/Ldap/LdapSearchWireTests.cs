using System.Security.Claims;
using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Search over a REAL loopback socket, from the client's first BER byte to the last
    /// SearchResultDone. <see cref="LdapSearchExecutorTests"/> proves the executor's decisions;
    /// what is proven here is that those decisions actually reach the wire through the connection
    /// loop — the seam where an op class can answer differently from its siblings, or fail to
    /// answer at all (protocol-adapter-security invariants 9 and 10).
    ///
    /// <para>The pipeline stand-in scopes every intent by the bound identity's tenant, so a
    /// cross-tenant fact here is a statement about the whole front door, not about one class.</para>
    /// </summary>
    public sealed class LdapSearchWireTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
        private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

        // ---- identity: two accounts in two tenants, one directory ----

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
                var tenant = bindDn switch
                {
                    "uid=alice,ou=people,dc=example,dc=com" => "acme",
                    "uid=bob,ou=people,dc=example,dc=com" => "globex",
                    _ => null,
                };
                if (tenant is null)
                    return Task.FromResult<LdapCredentialRecord?>(null);

                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.Name, bindDn),
                        new Claim("tenant", tenant),
                    }, "ldap"));
                return Task.FromResult<LdapCredentialRecord?>(
                    new LdapCredentialRecord("hash:s3cret", principal, Enabled: true));
            }
        }

        private sealed class Factory : IBifrostAuthContextFactory
        {
            public IDictionary<string, object?> CreateUserContext(HttpContext context)
            {
                var sub = context.User.FindFirst(ClaimTypes.Name)?.Value;
                if (string.IsNullOrEmpty(sub))
                    return new Dictionary<string, object?>();
                return new Dictionary<string, object?>
                {
                    ["sub"] = sub,
                    ["tenant"] = context.User.FindFirst("tenant")?.Value,
                };
            }

            public IDictionary<string, object?> CreateUserContext(HttpContext context, IDictionary<string, object?> existing)
                => CreateUserContext(context);
        }

        /// <summary>
        /// A whole front door: options, the directory model, a tenant-scoping pipeline stand-in, a
        /// bind authenticator, and a loopback connection already confidential (the LDAPS shape).
        /// </summary>
        private sealed class Directory : IAsyncDisposable
        {
            private Directory(LdapFixture fixture, LdapFakeIntentExecutor pipeline, LdapWireOptions options)
            {
                Fixture = fixture;
                Pipeline = pipeline;
                Options = options;
            }

            public LdapFixture Fixture { get; }
            public LdapFakeIntentExecutor Pipeline { get; }
            public LdapWireOptions Options { get; }

            public static async Task<Directory> StartAsync(
                Action<LdapWireOptions>? configure = null,
                Action<LdapFakeIntentExecutor>? seed = null,
                LdapModelBuilder? builder = null)
            {
                var options = new LdapWireOptions { PagedResultsCookieSecret = "wire-test-cookie-secret" };
                configure?.Invoke(options);

                var model = (builder ?? LdapModelBuilder.Create().WithPeople().WithGroups()).Build();
                var pipeline = new LdapFakeIntentExecutor(model);
                seed?.Invoke(pipeline);

                var search = new LdapSearchExecutor(pipeline, options, clock: () => Now);
                var authenticator = new LdapBindAuthenticator(new Store(), new Hasher(), new Factory(), options);
                var fixture = await LdapFixture.StartAsync(
                    options, authenticator: authenticator, tls: true, search: search);

                return new Directory(fixture, pipeline, options);
            }

            /// <summary>Binds as one of the two seeded accounts, asserting the bind succeeded.</summary>
            public async Task BindAsync(string uid, int messageId = 1)
            {
                await Fixture.Client.SendAsync(LdapWire.Message(messageId, LdapWire.BindRequest(
                    name: $"uid={uid},ou=people,dc=example,dc=com", password: "s3cret")));
                var response = await ReadAsync(Fixture);
                response.OpTag.Should().Be(LdapProtocol.BindResponse);
                response.ResultCode.Should().Be(LdapResultCode.Success);
            }

            public ValueTask DisposeAsync() => Fixture.DisposeAsync();
        }

        private static async Task<LdapResponse> ReadAsync(LdapFixture fixture)
        {
            var response = await fixture.Client.ReadResponseAsync().WaitAsync(Timeout);
            response.Should().NotBeNull("the server must answer every request rather than leaving the client waiting");
            return response!;
        }

        // ---- op-class error symmetry (invariants 9 + 10) ----

        [Fact]
        public async Task Search_WithAMalformedControlValue_IsAnsweredOnTheSearchOpClass_AndTheSessionSurvives()
        {
            // A supported control OID carrying no value is malformed. The decode of a control's
            // opaque VALUE happens inside the search op class, AFTER the message reader's own
            // catch clause has been left behind — so an adapter protocol exception raised there
            // has no catch between it and the host unless the search path answers it itself.
            //
            // Every sibling op class answers its own faults: a malformed message answers a Notice
            // of Disconnection, a bad credential answers a BindResponse, a refused StartTLS
            // answers an ExtendedResponse. Search must likewise answer a SearchResultDone — its
            // own contract is exactly one Done on every path, success, refusal, or fault.
            await using var directory = await Directory.StartAsync(seed: p => p.WithPeople(2, tenant: "acme"));
            await directory.BindAsync("alice");

            var malformed = LdapWire.Controls(LdapWire.Control(
                "1.2.840.113556.1.4.319", criticality: false)); // supported OID, absent value
            await directory.Fixture.Client.SendAsync(
                LdapWire.Message(2, LdapWire.SearchRequest(baseObject: "dc=example,dc=com"), malformed));

            var response = await ReadAsync(directory.Fixture);
            response.MessageId.Should().Be(2);
            response.OpTag.Should().Be(LdapProtocol.SearchResultDone);
            response.ResultCode.Should().Be(LdapResultCode.ProtocolError);

            // A malformed control is a fault of ONE operation, not a framing desync: the message
            // was consumed whole, so the session is still in sync and usable.
            await directory.Fixture.Client.SendAsync(
                LdapWire.Message(3, LdapWire.SearchRequest(baseObject: "dc=example,dc=com")));
            var next = await ReadAsync(directory.Fixture);
            next.MessageId.Should().Be(3);
            next.ResultCode.Should().Be(LdapResultCode.Success);
        }
    }
}
