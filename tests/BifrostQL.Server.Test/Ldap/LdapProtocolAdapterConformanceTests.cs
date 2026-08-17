using System.Security.Claims;
using System.Text;
using BifrostQL.AdapterConformance;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Ldap;
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
            "*.documents { policy-read-deny: body; "
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
        /// Every fail-closed condition reaches the client as a result code with an empty
        /// diagnostic — a tenant denial, a policy denial, and a pipeline fault are deliberately
        /// indistinguishable on the wire. Both the "selected a denied column" and the "no tenant
        /// identity" facts therefore expect the SAME sanitized signal, which is the point: the
        /// client learns it was refused and nothing about what it was refused.
        /// </summary>
        protected override string ExpectedRejectionFragment(string canonicalServerFragment) =>
            LdapResultCode.OperationsError.ToString();

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
    }
}
