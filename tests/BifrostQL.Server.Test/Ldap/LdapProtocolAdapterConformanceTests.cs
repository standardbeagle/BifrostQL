using System.Security.Claims;
using System.Text;
using BifrostQL.AdapterConformance;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Ldap;
using BifrostQL.Server.Test;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// The shared protocol-adapter security-conformance kit, run against the LDAP front door.
    ///
    /// <para><b>Through the wire, not around it.</b> Each conformance read is a real bind followed
    /// by a real SearchRequest over a loopback socket, decoded from the bytes the server wrote —
    /// the same shape the pgwire derivation uses. Nothing is injected past the bind: the caller's
    /// principal reaches the pipeline only by being what the credential store resolves the bind DN
    /// to, and is projected by the same <see cref="IBifrostAuthContextFactory"/> every transport
    /// shares. A fact that passed by handing the executor an identity directly would prove nothing
    /// about the front door.</para>
    ///
    /// <para><b>Read-only.</b> LDAP add/modify/delete are non-goals of this adapter — there is no
    /// write verb on the wire at all — so <see cref="AdapterSupportsMutations"/> stays false and
    /// the mutation facts are skipped. That is an honest reflection of the adapter's surface, not
    /// an opt-out: the day this front door grows a write operation, this flag must flip with it.</para>
    ///
    /// <para><b>Sanitized rejections.</b> LDAP carries a numeric result code, and this adapter
    /// deliberately blanks the diagnostic string rather than forwarding internal exception text
    /// (invariant 3) — a denial names neither the table nor the column nor the context key. So the
    /// rejection text the kit matches on is the adapter's own wire signal, the result code, and
    /// <see cref="ExpectedRejectionFragment"/> maps every canonical server fragment onto it. The
    /// fact still requires a THROW and zero rows; only the expected text is adapter-relative.</para>
    /// </summary>
    public sealed class LdapProtocolAdapterConformanceTests : ProtocolAdapterConformanceTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private const string BaseDn = "dc=conformance,dc=test";
        private const string BindDn = "cn=conformance,ou=service," + BaseDn;

        /// <summary>
        /// The kit's two fixture tables, additionally mapped into the directory. The
        /// tenant-filter / soft-delete / policy-read-deny semantics the kit asserts are untouched;
        /// only the LDAP opt-in is added, plus the model-level base DN the directory roots at.
        ///
        /// <para>The attribute names for non-key columns are deliberately custom
        /// (<c>orderTenant</c>, <c>docBody</c>) rather than well-known ones: a well-known attribute
        /// carries a required LDAP syntax, and mapping e.g. <c>description</c> onto an INTEGER
        /// column is rejected at model load. The mapping under test is the pipeline's, not the
        /// schema registry's.</para>
        /// </summary>
        protected override IReadOnlyList<string> MetadataRules => new[]
        {
            $":root {{ {Core.Model.MetadataKeys.Ldap.BaseDn}: {BaseDn} }}",
            "*.orders { tenant-filter: tenant_id; soft-delete: deleted_at; "
                + "ldap-object-class: bifrostOrder; ldap-dn-template: cn={name},ou=orders; "
                + "ldap-attributes: cn=name,orderId=id,orderTenant=tenant_id }",
            "*.documents { policy-actions: read; policy-read-deny: body; "
                + "ldap-object-class: bifrostDocument; ldap-dn-template: cn={title},ou=documents; "
                + "ldap-attributes: cn=title,docId=id,docBody=body }",
        };

        // The adapter is driven on its own loopback front door, bound to the fixture's real
        // executor; nothing is registered on the HTTP endpoint options. The base host still builds
        // the transformer-pipeline executor and the SQL-capture observer these facts rely on.
        protected override void RegisterAdapter(BifrostMultiDbOptions options) { }

        // No write verb exists on the LDAP wire; the mutation facts are correctly skipped.
        protected override bool AdapterSupportsMutations => false;

        /// <summary>
        /// Every fail-closed AUTHORIZATION condition reaches the client as
        /// <c>insufficientAccessRights</c> with an empty diagnostic — a tenant denial and a policy
        /// denial are deliberately indistinguishable on the wire, mapped by CONDITION through the
        /// executor's single funnel (protocol-adapter-security invariant 10): the transformer chain
        /// tags both with <c>ACCESS_DENIED</c>, and the funnel maps that code here. The client
        /// learns it was refused and nothing about what it was refused. An untagged server FAULT
        /// still maps to <c>operationsError</c>.
        /// </summary>
        protected override string ExpectedRejectionFragment(string canonicalServerFragment) =>
            LdapResultCode.InsufficientAccessRights.ToString();

        // Omit, not reject: the caller's own subschema projection hides the denied attribute, so on
        // this wire it does not exist — the entry is returned with docBody simply absent, and an
        // explicit denial would be the hidden-vs-nonexistent oracle (invariant 9 amendment, M20).
        protected override DeniedColumnSelectionExpectation DeniedColumnSelection => DeniedColumnSelectionExpectation.Omit;

        /// <summary>
        /// Per-column mappings, one direction each. Kept beside the metadata rules above so the
        /// two cannot drift: the kit speaks DB column names, the wire speaks attribute types.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> AttributeByColumn =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = "cn",
                ["id"] = "orderId",
                ["tenant_id"] = "orderTenant",
                ["title"] = "cn",
                ["body"] = "docBody",
            };

        private static IReadOnlyDictionary<string, string> ColumnsByAttribute(
            IReadOnlyList<string> columns, string table)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
            {
                var attribute = string.Equals(table, "documents", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(column, "id", StringComparison.OrdinalIgnoreCase)
                        ? "docId"
                        : AttributeByColumn[column];
                map[attribute] = column;
            }
            return map;
        }

        protected override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReadAsync(
            ConformanceReadRequest request)
        {
            var options = new LdapWireOptions
            {
                Endpoint = request.Endpoint,
                PagedResultsCookieSecret = "conformance-cookie-secret",
            };

            // A null principal models "no tenant identity". LDAP cannot bind "nobody" meaningfully,
            // so it binds an identity that simply carries NO tenant claim — the tenant transformer
            // then fails closed exactly as the kit intends.
            var principal = request.Principal ?? NoTenantPrincipal();

            var reads = Host.Services.GetRequiredService<IQueryIntentExecutor>();
            var search = new LdapSearchExecutor(reads, options);
            var authenticator = new LdapBindAuthenticator(
                new ConformanceCredentialStore(principal),
                new ConformanceHasher(),
                BifrostAuthContextFactory.Instance,
                options,
                services: Host.Services);

            await using var fixture = await LdapFixture.StartAsync(
                options, authenticator: authenticator, tls: true, search: search);

            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(
                name: BindDn, password: ConformanceCredentialStore.Secret)));
            var bind = await ReadAsync(fixture);
            if (bind.ResultCode != LdapResultCode.Success)
                throw new LdapConformanceException(bind.ResultCode!.Value);

            var columnByAttribute = ColumnsByAttribute(request.Columns, request.Table);
            await fixture.Client.SendAsync(LdapWire.Message(2, LdapWire.SearchRequest(
                baseObject: $"ou={request.Table},{BaseDn}",
                scope: LdapSearchScope.SingleLevel,
                filter: BuildFilter(request.Filter),
                attributes: columnByAttribute.Keys.ToArray())));

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            while (true)
            {
                var response = await ReadAsync(fixture);
                if (response.OpTag == LdapProtocol.SearchResultDone)
                {
                    // A refusal must SURFACE, never be swallowed into an empty result set: an
                    // adapter that returned zero rows here would look fail-closed while proving
                    // nothing about whether the pipeline actually rejected the read.
                    if (response.ResultCode != LdapResultCode.Success)
                        throw new LdapConformanceException(response.ResultCode!.Value);
                    return rows;
                }

                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var (attribute, values) in response.Attributes)
                {
                    if (columnByAttribute.TryGetValue(attribute, out var column))
                        row[column] = values.FirstOrDefault();
                }
                rows.Add(row);
            }
        }

        /// <summary>
        /// Translates the kit's single <c>{ column: { _eq: value } }</c> predicate into the
        /// equality filter a client would encode. Anything else is refused loudly rather than
        /// silently dropped — a filter that quietly did nothing would make the parameterization
        /// fact pass for the wrong reason.
        /// </summary>
        private static byte[]? BuildFilter(IReadOnlyDictionary<string, object?>? filter)
        {
            if (filter is null || filter.Count == 0)
                return null;
            if (filter.Count != 1)
                throw new NotSupportedException("the LDAP conformance wire sends one predicate at a time.");

            var (column, condition) = filter.First();
            if (condition is not IReadOnlyDictionary<string, object?> ops || ops.Count != 1
                || !ops.TryGetValue("_eq", out var value))
                throw new NotSupportedException("the LDAP conformance wire sends only _eq predicates.");

            return LdapWire.FilterEquality(
                AttributeByColumn[column], Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
        }

        private static async Task<LdapResponse> ReadAsync(LdapFixture fixture)
        {
            var response = await fixture.Client.ReadResponseAsync().WaitAsync(Timeout);
            return response
                ?? throw new InvalidOperationException("the LDAP front door closed without answering.");
        }

        /// <summary>An authenticated identity with no tenant claim — the wire's equivalent of the kit's null principal.</summary>
        private static ClaimsPrincipal NoTenantPrincipal() =>
            new(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, "user-no-tenant") }, authenticationType: "ldap"));

        // ---- (f) total-frame byte cap ---------------------------------------
        //
        // LdapMessageReader bounds the declared body length of one LDAPMessage envelope
        // (MaxMessageLength) BEFORE allocating or pulling the payload. The probe declares a
        // 64 KiB body under a 4 KiB cap through a counting stream, then decodes two in-budget
        // messages on one stream to show the per-message budget is independent per frame.

        // ---- (a2) malformed pre-auth wire input ------------------------------
        //
        // BER is a recursive, length-prefixed codec on an unauthenticated wire — the shape
        // invariant 6 is about (a depth cap must refuse before recursing; a StackOverflowException
        // is uncatchable and takes every front door in the process down with it). The kit's corpus
        // includes a deep nested-SEQUENCE-header prefix for exactly that reason.

        protected override bool AdapterSupportsMalformedFrameProbe => true;

        protected override async Task<MalformedFrameOutcome> ProbeMalformedFrameAsync(byte[] frame)
        {
            var handler = new LdapConnectionHandler(new LdapWireOptions());
            return await ProbeAsync(frame, (wire, ct) => handler.HandleConnectionAsync(wire, ct));
        }

        protected override bool AdapterSupportsFrameLimit => true;

        protected override async Task<FrameLimitProbe> ProbeFrameLimitAsync()
        {
            const int budget = 4096;
            const int declared = 64 * 1024;

            // SEQUENCE + long-form 4-byte length declaring 64 KiB, with the WHOLE declared
            // payload really on the wire. A stub behind the header would make the probe vacuous:
            // a reader with no cap at all reads to EOF, throws the same LdapProtocolException, and
            // pulls fewer bytes than declared — indistinguishable from a cap. With the payload
            // present, only a cap checked BEFORE ReadExactAsync keeps BytesRead under `declared`.
            var wire = new MemoryStream();
            wire.WriteByte(LdapProtocol.Sequence);
            wire.WriteByte(0x84);
            wire.WriteByte((byte)((declared >> 24) & 0xFF));
            wire.WriteByte((byte)((declared >> 16) & 0xFF));
            wire.WriteByte((byte)((declared >> 8) & 0xFF));
            wire.WriteByte((byte)(declared & 0xFF));
            wire.Write(new byte[declared]);
            wire.Position = 0;

            var counting = new CountingStream(wire);
            var reader = new LdapMessageReader(budget, maxNestingDepth: 32, maxFilterComponents: 100, maxSearchAttributes: 100);
            var refused = false;
            try
            {
                await reader.ReadRequestAsync(counting, default);
            }
            catch (LdapProtocolException)
            {
                refused = true;
            }

            // Two legal messages, each within the cap, on ONE stream: both decode.
            var legalWire = new MemoryStream();
            var first = LdapWire.Message(1, LdapWire.BindRequest(name: BindDn, password: "x"));
            var second = LdapWire.Message(2, LdapWire.BindRequest(name: BindDn, password: "y"));
            legalWire.Write(first);
            legalWire.Write(second);
            legalWire.Position = 0;
            var legalReader = new LdapMessageReader(budget, maxNestingDepth: 32, maxFilterComponents: 100, maxSearchAttributes: 100);
            var firstOk = await legalReader.ReadRequestAsync(legalWire, default);
            var secondOk = await legalReader.ReadRequestAsync(legalWire, default);

            return new FrameLimitProbe
            {
                OversizedRefused = refused,
                OversizedBytesRead = counting.BytesRead,
                OversizedDeclaredBytes = declared,
                TopLevelBudgetResetVerified = firstOk is not null && secondOk is not null,
            };
        }

        /// <summary>
        /// The rejection as the client actually receives it: a result code and nothing else. The
        /// message carries no table, column, or context-key name because the wire carries none.
        /// </summary>
        private sealed class LdapConformanceException : Exception
        {
            public LdapConformanceException(LdapResultCode code)
                : base($"the LDAP front door refused the operation: {code}")
            {
            }
        }

        // ---- (e) pre-auth attempt limiter -----------------------------------
        //
        // The LDAP bind path is bounded by LdapBindRateLimiter (per-source + per-account) BEFORE
        // the credential store is consulted, and every bind failure — unknown DN, wrong password,
        // rate-limited — yields the SAME wire shape (InvalidCredentials, empty diagnostic) by
        // construction (LdapBindResult). The kit's byte-equality assertion is therefore trivially
        // satisfied here; the load-bearing assertion is CredentialResolved == false past the cap,
        // observed through the counting store. The limiter lives on the authenticator, so one
        // authenticator is shared across the fact's per-attempt connections.

        protected override bool AdapterSupportsAuthRateLimit => true;

        protected override int AuthAttemptBudget => 3;

        protected override string KnownAuthAccount => BindDn;

        protected override string UnknownAuthAccount => "cn=nobody,ou=service," + BaseDn;

        private CountingLdapCredentialStore? _authStore;
        private LdapBindAuthenticator? _authAuthenticator;
        private LdapWireOptions? _authOptions;

        protected override async Task<AuthAttemptOutcome> AttemptAuthAsync(string account, string secret)
        {
            _authStore ??= new CountingLdapCredentialStore(KnownAuthAccount, TenantPrincipal("user-a", "tenant-a"));
            _authOptions ??= new LdapWireOptions
            {
                Endpoint = EndpointPath,
                MaxBindAttemptsPerSource = AuthAttemptBudget,
                MaxBindAttemptsPerAccount = AuthAttemptBudget,
                PagedResultsCookieSecret = "conformance-cookie-secret",
            };
            _authAuthenticator ??= new LdapBindAuthenticator(
                _authStore, new ConformanceHasher(), BifrostAuthContextFactory.Instance, _authOptions,
                services: Host.Services);

            // TLS: a credential-bearing bind on a cleartext connection is refused before any of
            // the machinery under test runs (that refusal is a different fact's subject).
            await using var fixture = await LdapFixture.StartAsync(
                _authOptions, authenticator: _authAuthenticator, tls: true);

            var lookupsBefore = _authStore.Lookups;
            await fixture.Client.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: account, password: secret)));
            var response = await fixture.Client.ReadResponseAsync().WaitAsync(Timeout)
                ?? throw new InvalidOperationException("the LDAP front door closed without answering the bind.");
            return new AuthAttemptOutcome
            {
                Refused = response.ResultCode != LdapResultCode.Success,
                WireText = response.ResultCode?.ToString(),
                CredentialResolved = _authStore.Lookups > lookupsBefore,
            };
        }

        /// <summary>A credential store that counts resolutions, so the kit can prove an over-cap refusal never reaches it.</summary>
        private sealed class CountingLdapCredentialStore : ILdapCredentialStore
        {
            private readonly string _knownDn;
            private readonly ClaimsPrincipal _principal;

            public CountingLdapCredentialStore(string knownDn, ClaimsPrincipal principal)
            {
                _knownDn = knownDn;
                _principal = principal;
            }

            public int Lookups { get; private set; }

            public Task<LdapCredentialRecord?> FindAsync(string bindDn, CancellationToken ct)
            {
                Lookups++;
                return Task.FromResult<LdapCredentialRecord?>(
                    string.Equals(bindDn, _knownDn, StringComparison.OrdinalIgnoreCase)
                        ? new LdapCredentialRecord("hash:" + ConformanceCredentialStore.Secret, _principal, Enabled: true)
                        : null);
            }
        }

        /// <summary>Resolves the one service DN to whichever principal the current fact is exercising.</summary>
        private sealed class ConformanceCredentialStore : ILdapCredentialStore
        {
            public const string Secret = "conformance-secret";

            private readonly ClaimsPrincipal _principal;

            public ConformanceCredentialStore(ClaimsPrincipal principal) => _principal = principal;

            public Task<LdapCredentialRecord?> FindAsync(string bindDn, CancellationToken ct) =>
                Task.FromResult<LdapCredentialRecord?>(
                    string.Equals(bindDn, BindDn, StringComparison.OrdinalIgnoreCase)
                        ? new LdapCredentialRecord("hash:" + Secret, _principal, Enabled: true)
                        : null);
        }

        private sealed class ConformanceHasher : ILdapPasswordHasher
        {
            public string DecoyHash => "hash:$decoy$";

            public bool Verify(ReadOnlySpan<byte> password, string passwordHash) =>
                passwordHash != DecoyHash && passwordHash == "hash:" + Encoding.UTF8.GetString(password);
        }

        // ---- (g) continuation-token integrity --------------------------------
        //
        // LdapPageCookie MACs every paged-results cookie against a binding re-derived from the
        // LIVE continuation request (search shape hash, page size, identity fingerprint); the
        // wire refusal for any invalid cookie is the single UnavailableCriticalExtension result
        // with an empty diagnostic from LdapSearchExecutor, so all six tampers surface
        // byte-identically.

        // ---- kit fact (b): admission before the TLS handshake ----------------
        //
        // LDAPS is the implicit-TLS listener: the handshake precedes the first LDAP byte, so the
        // slot must be taken ahead of it. Both LDAP listeners share ONE counter (MaxConnections is
        // this front door's total), so a cap of one is filled by whichever port the peer reaches.

        protected override bool AdapterSupportsTlsAdmissionProbe => true;

        protected override async Task<TlsAdmissionProbe> ProbeTlsAdmissionAsync()
        {
            var (slots, closed, handshook) = await ProtocolTlsAdmissionHarness.ProbeAsync(
                async (port, certificate) =>
                {
                    var cleartextPort = ProtocolTlsAdmissionHarness.FreePort();
                    var host = await new HostBuilder().ConfigureWebHost(web =>
                    {
                        web.UseKestrel();
                        web.UseUrls();
                        web.ConfigureServices(services =>
                        {
                            services.AddSingleton<ILdapCredentialStore, LdapTestIdentity.Store>();
                            services.AddSingleton<ILdapPasswordHasher, LdapTestIdentity.Hasher>();
                            services.AddSingleton<IBifrostAuthContextFactory, LdapTestIdentity.Factory>();
                            services.AddBifrostLdap(o =>
                            {
                                o.Port = cleartextPort;
                                // The probe connects here: the implicit-TLS port.
                                o.LdapsPort = port;
                                o.MaxConnections = 1;
                                o.ServerCertificate = certificate;
                                o.PagedResultsCookieSecret = "conformance-tls-admission-secret";
                            });
                        });
                        web.Configure(_ => { });
                    }).StartAsync();

                    return ((IAsyncDisposable)new ProtocolTlsAdmissionHarness.HostStopper(host),
                        () => host.Services.GetRequiredService<LdapConnectionLimiter>().Count);
                },
                ProtocolTlsAdmissionHarness.TryImplicitTlsHandshakeAsync);

            return new TlsAdmissionProbe
            {
                SlotsHeldBySilentPeer = slots,
                OverCapPeerClosed = closed,
                OverCapPeerCompletedTlsHandshake = handshook,
            };
        }

        protected override bool AdapterSupportsContinuationTokens => true;

        protected override Task<ContinuationReplayOutcome> ReplayContinuationAsync(ContinuationTamper tamper)
        {
            var secret = new byte[32];
            for (var i = 0; i < secret.Length; i++) secret[i] = (byte)(i + 1);
            var wrongSecret = new byte[32]; // a key the server does not hold
            var now = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
            var ttl = TimeSpan.FromHours(1);
            var binding = new LdapPageBinding("orders-search-shape", 100, "fingerprint-user-a");
            var replayBinding = tamper switch
            {
                // Cross-context target: a DIFFERENT search shape. The cookie position is a fixed
                // (targetIndex, offset) pair for EVERY shape, so the payload arity is identical by
                // construction and only the MAC over the re-derived shape hash can refuse the replay.
                ContinuationTamper.CrossContext => binding with { SearchShapeHash = "documents-search-shape" },
                ContinuationTamper.CrossIdentity => binding with { IdentityFingerprint = "fingerprint-user-b" },
                _ => binding,
            };
            byte[] Issue(byte[] key, DateTimeOffset at) =>
                LdapPageCookie.Issue(new LdapPagePosition(1, 5), at, binding, key);
            var cookie = tamper switch
            {
                ContinuationTamper.Forged => Issue(wrongSecret, now),
                ContinuationTamper.Tampered => TamperCookie(Issue(secret, now)),
                ContinuationTamper.Expired => Issue(secret, now - ttl - ttl),
                ContinuationTamper.Unparseable => Encoding.ASCII.GetBytes("not-a-cookie"),
                _ => Issue(secret, now),
            };

            var accepted = LdapPageCookie.TryDecode(cookie, replayBinding, secret, now, ttl, out var position);
            return Task.FromResult(new ContinuationReplayOutcome
            {
                Refused = !accepted,
                WireText = accepted ? null : LdapResultCode.UnavailableCriticalExtension.ToString(),
                ResumedFromStart = accepted && position.Equals(new LdapPagePosition(0, 0)),
            });
        }

        /// <summary>Flips one payload byte, keeping the cookie well-formed base64url text.</summary>
        private static byte[] TamperCookie(byte[] cookie)
        {
            var tampered = (byte[])cookie.Clone();
            tampered[0] = tampered[0] == (byte)'A' ? (byte)'B' : (byte)'A';
            return tampered;
        }
    }
}
