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

            /// <summary>
            /// Sends one SearchRequest and drains the whole response sequence: every
            /// SearchResultEntry up to and including the single SearchResultDone that terminates
            /// it. Asserting on the collected entries rather than on one read is what makes a
            /// "this DN never appeared" claim mean anything.
            /// </summary>
            public async Task<SearchResult> SearchAsync(byte[] request, int messageId = 2, byte[]? controls = null)
            {
                await Fixture.Client.SendAsync(LdapWire.Message(messageId, request, controls));

                var entries = new List<LdapResponse>();
                while (true)
                {
                    var response = await ReadAsync(Fixture);
                    response.MessageId.Should().Be(messageId, "every response belongs to the request that asked for it");
                    if (response.OpTag == LdapProtocol.SearchResultDone)
                        return new SearchResult(entries, response);
                    response.OpTag.Should().Be(LdapProtocol.SearchResultEntry);
                    entries.Add(response);
                }
            }

            public ValueTask DisposeAsync() => Fixture.DisposeAsync();
        }

        /// <summary>One search's entries plus its terminating Done.</summary>
        private sealed record SearchResult(IReadOnlyList<LdapResponse> Entries, LdapResponse Done)
        {
            public LdapResultCode ResultCode => Done.ResultCode!.Value;

            public IEnumerable<string> Dns => Entries.Select(e => e.ObjectName!);
        }

        /// <summary>A whole-subtree search of the directory's base DN — the shape every client sends.</summary>
        private static byte[] Subtree(
            string baseObject = "dc=example,dc=com", byte[]? filter = null, string[]? attributes = null,
            int sizeLimit = 0) =>
            LdapWire.SearchRequest(
                baseObject, LdapSearchScope.WholeSubtree, filter, attributes, sizeLimit);

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
            await directory.Fixture.Client.SendAsync(LdapWire.Message(2, Subtree(), malformed));

            var response = await ReadAsync(directory.Fixture);
            response.MessageId.Should().Be(2);
            response.OpTag.Should().Be(LdapProtocol.SearchResultDone);
            response.ResultCode.Should().Be(LdapResultCode.ProtocolError);

            // A malformed control is a fault of ONE operation, not a framing desync: the message
            // was consumed whole, so the session is still in sync and usable.
            var next = await directory.SearchAsync(Subtree(), messageId: 3);
            next.ResultCode.Should().Be(LdapResultCode.Success);
            next.Entries.Should().HaveCount(2, "the session is still in sync and serving");
        }

        [Fact]
        public async Task Search_BeforeAnyBind_AndForAnUnknownBase_AreBothRefusedWithoutRevealingWhichIsWhich()
        {
            // Two different reasons to refuse, reached by two different code paths. If they
            // answered differently, an unauthenticated peer could map the directory's namespace
            // by watching which refusal it got — the anti-oracle property, asserted across op
            // paths rather than within one.
            await using var directory = await Directory.StartAsync(seed: p => p.WithPeople(2, tenant: "acme"));

            var known = await directory.SearchAsync(Subtree(), messageId: 1);
            var unknown = await directory.SearchAsync(Subtree("dc=nowhere,dc=invalid"), messageId: 2);

            known.ResultCode.Should().Be(LdapResultCode.InsufficientAccessRights);
            known.Entries.Should().BeEmpty();
            unknown.ResultCode.Should().Be(known.ResultCode,
                "an unbound peer learns nothing about which bases exist");
            unknown.Entries.Should().BeEmpty();
        }

        // ---- cross-tenant: the whole front door, not one class ----

        [Fact]
        public async Task Search_OverTheWholeSubtree_NeverNamesAnotherTenantsEntry()
        {
            await using var directory = await Directory.StartAsync(seed: p => p
                .WithPeople(2, tenant: "acme", startId: 1)
                .WithPeople(2, tenant: "globex", startId: 10));
            await directory.BindAsync("alice"); // tenant acme

            var result = await directory.SearchAsync(Subtree());

            result.ResultCode.Should().Be(LdapResultCode.Success);
            result.Dns.Should().BeEquivalentTo(new[]
            {
                "uid=user0001,ou=people,dc=example,dc=com",
                "uid=user0002,ou=people,dc=example,dc=com",
            });
            result.Dns.Should().NotContain(dn => dn.Contains("user0010", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Search_ForAnotherTenantsEntryByItsExactDn_IsIndistinguishableFromOneThatDoesNotExist()
        {
            // The strongest cross-tenant shape: the caller already KNOWS the DN. A directory that
            // answered noSuchObject for a fabricated DN but anything else for a real-but-foreign
            // one would confirm the account exists — the classic existence oracle.
            await using var directory = await Directory.StartAsync(seed: p => p
                .WithPeople(1, tenant: "acme", startId: 1)
                .WithPeople(1, tenant: "globex", startId: 10));
            await directory.BindAsync("alice"); // tenant acme

            var foreign = await directory.SearchAsync(
                LdapWire.SearchRequest(
                    "uid=user0010,ou=people,dc=example,dc=com", LdapSearchScope.BaseObject),
                messageId: 2);
            var fabricated = await directory.SearchAsync(
                LdapWire.SearchRequest(
                    "uid=nobody-at-all,ou=people,dc=example,dc=com", LdapSearchScope.BaseObject),
                messageId: 3);

            foreign.Entries.Should().BeEmpty();
            fabricated.Entries.Should().BeEmpty();
            foreign.ResultCode.Should().Be(fabricated.ResultCode,
                "a real foreign entry and a fabricated one answer identically");
            foreign.Done.Attributes.Should().BeEmpty();
        }

        [Fact]
        public async Task Search_FilteringForAnotherTenantsAttributeValue_MatchesNothing()
        {
            // A filter is the caller's own input; it can only ever NARROW what the identity was
            // already permitted to read. Naming a foreign row's value in it widens nothing.
            await using var directory = await Directory.StartAsync(seed: p => p
                .WithPeople(1, tenant: "acme", startId: 1)
                .WithPeople(1, tenant: "globex", startId: 10));
            await directory.BindAsync("alice");

            var result = await directory.SearchAsync(
                Subtree(filter: LdapWire.FilterEquality("uid", "user0010")));

            result.ResultCode.Should().Be(LdapResultCode.Success);
            result.Entries.Should().BeEmpty();
        }

        [Fact]
        public async Task Search_TheSameRequestOnTwoBoundIdentities_ReturnsDisjointEntries()
        {
            // Two sessions, one directory, one request text. The only difference is who bound —
            // which is the whole claim tenant isolation makes.
            var seed = new Action<LdapFakeIntentExecutor>(p => p
                .WithPeople(2, tenant: "acme", startId: 1)
                .WithPeople(2, tenant: "globex", startId: 10));

            await using var acme = await Directory.StartAsync(seed: seed);
            await acme.BindAsync("alice");
            var acmeResult = await acme.SearchAsync(Subtree());

            await using var globex = await Directory.StartAsync(seed: seed);
            await globex.BindAsync("bob");
            var globexResult = await globex.SearchAsync(Subtree());

            acmeResult.Dns.Should().NotBeEmpty();
            globexResult.Dns.Should().NotBeEmpty();
            acmeResult.Dns.Should().NotIntersectWith(globexResult.Dns);
        }

        [Fact]
        public async Task Search_NeverReturnsTheCredentialAttribute_WhateverTheClientAsksFor()
        {
            // The credential column is bind-verification input only. Asking for it by every name
            // it could plausibly carry must not produce it.
            await using var directory = await Directory.StartAsync(seed: p => p.WithPeople(1, tenant: "acme"));
            await directory.BindAsync("alice");

            var result = await directory.SearchAsync(
                Subtree(attributes: new[] { "uid", "userPassword", "password_hash", "passwordHash" }));

            result.ResultCode.Should().Be(LdapResultCode.Success);
            result.Entries.Should().HaveCount(1);
            var entry = result.Entries[0];
            entry.Value("uid").Should().Be("user0001");
            entry.Attributes.Keys.Should().NotContain(k =>
                k.Contains("password", StringComparison.OrdinalIgnoreCase));
            entry.Attributes.Values.SelectMany(v => v).Should().NotContain(v =>
                v.Contains("$2y$", StringComparison.Ordinal), "no attribute carries the stored hash");
        }

        // ---- bounds and paging, end to end ----

        [Fact]
        public async Task Search_PastTheServerCeiling_ReportsSizeLimitExceededRatherThanLookingComplete()
        {
            await using var directory = await Directory.StartAsync(
                configure: o => o.MaxSearchResults = 3,
                seed: p => p.WithPeople(10, tenant: "acme"));
            await directory.BindAsync("alice");

            var result = await directory.SearchAsync(Subtree());

            result.Entries.Should().HaveCount(3);
            result.ResultCode.Should().Be(LdapResultCode.SizeLimitExceeded,
                "a truncated answer that reports success is worse than an explicitly partial one");
        }

        [Fact]
        public async Task Search_PagedToTheEnd_VisitsEveryEntryExactlyOnce_AndEndsWithAnEmptyCookie()
        {
            await using var directory = await Directory.StartAsync(seed: p => p.WithPeople(7, tenant: "acme"));
            await directory.BindAsync("alice");

            var seen = new List<string>();
            byte[]? cookie = null;
            var messageId = 2;
            while (true)
            {
                var page = await directory.SearchAsync(
                    Subtree(), messageId++, LdapWire.Controls(LdapWire.PagedControl(3, cookie)));
                page.ResultCode.Should().Be(LdapResultCode.Success);
                seen.AddRange(page.Dns);

                cookie = page.Done.PagedCookie;
                cookie.Should().NotBeNull("a paged search always answers with a paged-results control");
                if (cookie!.Length == 0)
                    break;
                messageId.Should().BeLessThan(12, "seven entries in pages of three must terminate");
            }

            seen.Should().HaveCount(7).And.OnlyHaveUniqueItems();
        }

        [Fact]
        public async Task Search_WithATamperedPagingCookie_IsRefusedAndReturnsNothing()
        {
            // A cookie is integrity-protected. A forged one must not silently degrade into a
            // full re-scan from the beginning, which would hide the tampering from both the
            // client and the operator.
            await using var directory = await Directory.StartAsync(seed: p => p.WithPeople(7, tenant: "acme"));
            await directory.BindAsync("alice");

            var first = await directory.SearchAsync(
                Subtree(), 2, LdapWire.Controls(LdapWire.PagedControl(3, null)));
            var cookie = first.Done.PagedCookie;
            cookie.Should().NotBeNullOrEmpty();

            var forged = cookie!.ToArray();
            forged[^1] ^= 0xFF; // flip a bit of the authentication tag

            var replay = await directory.SearchAsync(
                Subtree(), 3, LdapWire.Controls(LdapWire.PagedControl(3, forged)));

            replay.Entries.Should().BeEmpty();
            replay.ResultCode.Should().Be(LdapResultCode.UnavailableCriticalExtension);
        }

        [Fact]
        public async Task Search_APagingCookieIssuedToAnotherIdentity_IsRefused()
        {
            // The cookie binds the identity that was issued it. Otherwise a captured cookie
            // would be a portable, replayable read capability across sessions.
            var seed = new Action<LdapFakeIntentExecutor>(p => p
                .WithPeople(7, tenant: "acme", startId: 1)
                .WithPeople(7, tenant: "globex", startId: 20));

            await using var acme = await Directory.StartAsync(seed: seed);
            await acme.BindAsync("alice");
            var page = await acme.SearchAsync(Subtree(), 2, LdapWire.Controls(LdapWire.PagedControl(3, null)));
            var cookie = page.Done.PagedCookie;
            cookie.Should().NotBeNullOrEmpty();

            await using var globex = await Directory.StartAsync(seed: seed);
            await globex.BindAsync("bob");
            var replay = await globex.SearchAsync(
                Subtree(), 2, LdapWire.Controls(LdapWire.PagedControl(3, cookie)));

            replay.Entries.Should().BeEmpty();
            replay.ResultCode.Should().Be(LdapResultCode.UnavailableCriticalExtension);
        }

        [Fact]
        public async Task Search_WithAnUnsupportedCriticalControl_IsRefusedEntirelyOnTheWire()
        {
            await using var directory = await Directory.StartAsync(seed: p => p.WithPeople(3, tenant: "acme"));
            await directory.BindAsync("alice");

            // The server-side sort control: understood by name, not implemented here.
            var result = await directory.SearchAsync(
                Subtree(), 2, LdapWire.Controls(LdapWire.Control("1.2.840.113556.1.4.473", criticality: true)));

            result.Entries.Should().BeEmpty("a critical control the server cannot honour makes the operation unserviceable");
            result.ResultCode.Should().Be(LdapResultCode.UnavailableCriticalExtension);
        }

        [Fact]
        public async Task Search_WithAnUnsupportedNonCriticalControl_IsServedWithTheControlIgnored()
        {
            await using var directory = await Directory.StartAsync(seed: p => p.WithPeople(3, tenant: "acme"));
            await directory.BindAsync("alice");

            var result = await directory.SearchAsync(
                Subtree(), 2, LdapWire.Controls(LdapWire.Control("1.2.840.113556.1.4.473", criticality: false)));

            result.ResultCode.Should().Be(LdapResultCode.Success);
            result.Entries.Should().HaveCount(3);
        }

        // ---- scopes and the filter grammar, over the socket ----

        [Fact]
        public async Task Search_AtOneLevelUnderAContainer_ReturnsOnlyThatContainersFamily()
        {
            await using var directory = await Directory.StartAsync(
                builder: LdapModelBuilder.Create().WithPeople().WithGroups(),
                seed: p => p
                    .WithPeople(2, tenant: "acme")
                    .WithRows("groups", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["id"] = 1, ["name"] = "admins", ["description"] = "operators", ["tenant"] = "acme",
                    }));
            await directory.BindAsync("alice");

            var people = await directory.SearchAsync(
                LdapWire.SearchRequest("ou=people,dc=example,dc=com", LdapSearchScope.SingleLevel), 2);
            var groups = await directory.SearchAsync(
                LdapWire.SearchRequest("ou=groups,dc=example,dc=com", LdapSearchScope.SingleLevel), 3);

            people.Dns.Should().OnlyContain(dn => dn.Contains("ou=people", StringComparison.Ordinal));
            groups.Dns.Should().OnlyContain(dn => dn.Contains("ou=groups", StringComparison.Ordinal));
            groups.Entries.Should().HaveCount(1);
            groups.Entries[0].Value("cn").Should().Be("admins");
        }

        [Theory]
        // The filter grammar as a client actually encodes it, each shape decided over the socket.
        [InlineData("equality", 1)]
        [InlineData("substring-initial", 1)]
        [InlineData("and", 1)]
        [InlineData("or", 2)]
        [InlineData("not", 1)]
        [InlineData("present", 2)]
        public async Task Search_TheFilterGrammar_IsDecidedOverTheWire(string shape, int expected)
        {
            await using var directory = await Directory.StartAsync(seed: p => p.WithPeople(2, tenant: "acme"));
            await directory.BindAsync("alice");

            var filter = shape switch
            {
                "equality" => LdapWire.FilterEquality("uid", "user0001"),
                "substring-initial" => LdapWire.FilterSubstrings("uid", initial: "user0001"),
                "and" => LdapWire.FilterAnd(
                    LdapWire.FilterEquality("uid", "user0001"),
                    LdapWire.FilterEquality("cn", "User 0001")),
                "or" => LdapWire.FilterOr(
                    LdapWire.FilterEquality("uid", "user0001"),
                    LdapWire.FilterEquality("uid", "user0002")),
                "not" => LdapWire.FilterNot(LdapWire.FilterEquality("uid", "user0002")),
                "present" => LdapWire.FilterPresent("uid"),
                _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown filter shape"),
            };

            var result = await directory.SearchAsync(Subtree(filter: filter));

            result.ResultCode.Should().Be(LdapResultCode.Success);
            result.Entries.Should().HaveCount(expected);
        }

        [Fact]
        public async Task Search_AFilterValueCarryingSqlPunctuation_IsAParameterAndMatchesNothing()
        {
            await using var directory = await Directory.StartAsync(seed: p => p.WithPeople(2, tenant: "acme"));
            await directory.BindAsync("alice");

            var result = await directory.SearchAsync(
                Subtree(filter: LdapWire.FilterEquality("uid", "user0001' OR '1'='1")));

            result.ResultCode.Should().Be(LdapResultCode.Success);
            result.Entries.Should().BeEmpty("the value is bound, never interpolated");

            var executed = directory.Pipeline.Intents.Last();
            executed.Query.Filter.Should().NotBeNull();
        }

        // ---- discovery, over the socket ----

        [Fact]
        public async Task Search_TheRootDse_IsReadableAndAdvertisesTheSubschemaItActuallyServes()
        {
            await using var directory = await Directory.StartAsync(
                configure: o => o.AnonymousBindEnabled = true);

            await directory.Fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "", password: "")));
            (await ReadAsync(directory.Fixture)).ResultCode.Should().Be(LdapResultCode.Success);

            var root = await directory.SearchAsync(
                LdapWire.SearchRequest(string.Empty, LdapSearchScope.BaseObject), 2);

            root.ResultCode.Should().Be(LdapResultCode.Success);
            root.Entries.Should().HaveCount(1);
            root.Entries[0].ObjectName.Should().BeEmpty();
            root.Entries[0].Value("subschemaSubentry").Should().Be("cn=subschema");
            root.Entries[0].Value("namingContexts").Should().Be("dc=example,dc=com");

            var subschema = await directory.SearchAsync(
                LdapWire.SearchRequest("cn=subschema", LdapSearchScope.BaseObject), 3);
            subschema.ResultCode.Should().Be(LdapResultCode.Success);
            subschema.Entries.Should().HaveCount(1);
        }

        [Fact]
        public async Task Search_AnAnonymousSession_ReachesDiscoveryButNeverData()
        {
            await using var directory = await Directory.StartAsync(
                configure: o => o.AnonymousBindEnabled = true,
                seed: p => p.WithPeople(3, tenant: "acme"));

            await directory.Fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "", password: "")));
            (await ReadAsync(directory.Fixture)).ResultCode.Should().Be(LdapResultCode.Success);

            var root = await directory.SearchAsync(
                LdapWire.SearchRequest(string.Empty, LdapSearchScope.BaseObject), 2);
            root.ResultCode.Should().Be(LdapResultCode.Success);

            var data = await directory.SearchAsync(Subtree(), 3);
            data.Entries.Should().BeEmpty();
            data.ResultCode.Should().Be(LdapResultCode.InsufficientAccessRights);
        }

        // ---- lifecycle over a live session ----

        [Fact]
        public async Task Abandon_IsASilentNoOp_AndTheSessionKeepsAnswering()
        {
            // Abandon carries no response by protocol. The risk is a loop that answers it anyway
            // and thereby shifts every later response one message out of step.
            await using var directory = await Directory.StartAsync(seed: p => p.WithPeople(2, tenant: "acme"));
            await directory.BindAsync("alice");

            await directory.Fixture.Client.SendAsync(LdapWire.Message(7, LdapWire.AbandonRequest(2)));

            var result = await directory.SearchAsync(Subtree(), messageId: 8);
            result.ResultCode.Should().Be(LdapResultCode.Success);
            result.Entries.Should().HaveCount(2);
        }

        [Fact]
        public async Task Unbind_ClosesTheSession_WithNoResponse()
        {
            await using var directory = await Directory.StartAsync(seed: p => p.WithPeople(1, tenant: "acme"));
            await directory.BindAsync("alice");

            await directory.Fixture.Client.SendAsync(LdapWire.Message(9, LdapWire.UnbindRequest()));

            (await directory.Fixture.Client.ReadResponseAsync().WaitAsync(Timeout))
                .Should().BeNull("unbind carries no response; the server closes");
            await directory.Fixture.ServerTask.WaitAsync(Timeout);
        }
    }
}
