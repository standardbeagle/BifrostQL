using System.Security.Claims;
using BifrostQL.Core.Auth;

namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// Projects an authenticated OIDC <see cref="ClaimsPrincipal"/> into the
    /// provider-agnostic <see cref="AppIdentity"/> contract from Auth 1/4. Each provider
    /// (Microsoft 365, Google) ships its own implementation that knows which raw claim
    /// types carry the subject, email, name, tenant, and group memberships, but every
    /// implementation produces the identical <see cref="AppIdentity"/> shape local auth
    /// produces — so the security modules downstream never see a provider difference.
    /// </summary>
    public interface IOidcClaimMapper
    {
        /// <summary>
        /// The provider name written into <see cref="AppIdentity.Provider"/>
        /// (e.g. <c>oidc:google</c>). Also the key used to select a mapper for an
        /// authenticated principal.
        /// </summary>
        string Provider { get; }

        /// <summary>
        /// Maps <paramref name="principal"/>'s claims into an <see cref="AppIdentity"/>.
        /// Throws <see cref="ArgumentNullException"/> when the principal is null and
        /// <see cref="ArgumentException"/> when no usable subject claim is present.
        /// </summary>
        AppIdentity Map(ClaimsPrincipal principal);
    }

    /// <summary>
    /// Per-provider configuration for which raw claim type supplies the tenant/org id.
    /// Microsoft 365 carries the tenant in <c>tid</c>; Google has no tenant claim by
    /// default, so a deployment can point the mapper at a custom claim (e.g. a hosted
    /// domain or an app-specific org claim) instead.
    /// </summary>
    public sealed record OidcClaimMapping
    {
        /// <summary>
        /// Raw claim type that carries the primary tenant/org identifier. When null the
        /// mapped identity has no tenant id.
        /// </summary>
        public string? TenantClaimType { get; init; }

        /// <summary>
        /// Raw claim type that carries group/org memberships, projected into
        /// <see cref="AppIdentity.OrgIds"/>. When null no org ids are mapped.
        /// </summary>
        public string? GroupsClaimType { get; init; }
    }

    /// <summary>
    /// Shared OIDC claim-mapping logic. Concrete providers supply their provider name and
    /// the default <see cref="OidcClaimMapping"/>; the base resolves subject/email/name in
    /// a provider-neutral way (standard OIDC claim types with ASP.NET-mapped fallbacks).
    /// </summary>
    public abstract class OidcClaimMapperBase : IOidcClaimMapper
    {
        private readonly OidcClaimMapping _mapping;

        /// <summary>
        /// Creates a mapper with an optional override of the provider's default claim
        /// mapping. A null <paramref name="mapping"/> falls back to <see cref="DefaultMapping"/>.
        /// </summary>
        protected OidcClaimMapperBase(OidcClaimMapping? mapping)
        {
            _mapping = mapping ?? DefaultMapping;
        }

        /// <inheritdoc />
        public abstract string Provider { get; }

        /// <summary>The provider's default tenant/groups claim mapping.</summary>
        protected abstract OidcClaimMapping DefaultMapping { get; }

        /// <inheritdoc />
        public AppIdentity Map(ClaimsPrincipal principal)
        {
            if (principal == null)
                throw new ArgumentNullException(nameof(principal));

            var id = FindFirst(principal, "sub", ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException(
                    $"OIDC principal for provider '{Provider}' has no subject claim.", nameof(principal));

            var email = FindFirst(principal, "email", ClaimTypes.Email);
            var displayName = FindFirst(principal, "name", ClaimTypes.Name);

            string? tenantId = null;
            if (_mapping.TenantClaimType != null)
            {
                var rawTenant = principal.FindFirstValue(_mapping.TenantClaimType);
                tenantId = string.IsNullOrWhiteSpace(rawTenant) ? null : rawTenant;
            }

            IReadOnlyList<string> orgIds = Array.Empty<string>();
            if (_mapping.GroupsClaimType != null)
            {
                orgIds = principal.FindAll(_mapping.GroupsClaimType)
                    .Select(c => c.Value)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToArray();
            }

            var roles = principal.FindAll(ClaimTypes.Role)
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToArray();

            return new AppIdentity(
                id: id,
                provider: Provider,
                email: string.IsNullOrWhiteSpace(email) ? null : email,
                displayName: string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                tenantId: tenantId,
                orgIds: orgIds,
                roles: roles);
        }

        /// <summary>
        /// Returns the first non-empty value across the supplied claim types, in order.
        /// Lets a mapper read the raw OIDC claim type and still work when ASP.NET's inbound
        /// claim mapping has already rewritten it to a <see cref="ClaimTypes"/> URI.
        /// </summary>
        private static string? FindFirst(ClaimsPrincipal principal, params string[] claimTypes)
        {
            foreach (var claimType in claimTypes)
            {
                var value = principal.FindFirstValue(claimType);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return null;
        }
    }

    /// <summary>
    /// Holds the OIDC claim mappers registered for a deployment and selects the one that
    /// applies to an authenticated principal. Selection is by the principal's issuer
    /// (<c>iss</c>) claim: each mapper is registered against the issuer URL of the OIDC
    /// provider it maps, so a deployment can wire Google and Microsoft 365 side by side.
    /// </summary>
    public sealed class OidcClaimMapperRegistry
    {
        private readonly IReadOnlyDictionary<string, IOidcClaimMapper> _byIssuer;

        /// <summary>
        /// Creates a registry from issuer-to-mapper pairs. Throws
        /// <see cref="ArgumentException"/> when an issuer is blank or registered twice.
        /// </summary>
        public OidcClaimMapperRegistry(IEnumerable<KeyValuePair<string, IOidcClaimMapper>> mappers)
        {
            if (mappers == null)
                throw new ArgumentNullException(nameof(mappers));

            var byIssuer = new Dictionary<string, IOidcClaimMapper>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in mappers)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    throw new ArgumentException("OIDC mapper issuer must not be blank.", nameof(mappers));
                if (pair.Value == null)
                    throw new ArgumentException($"OIDC mapper for issuer '{pair.Key}' is null.", nameof(mappers));
                if (byIssuer.ContainsKey(pair.Key))
                    throw new ArgumentException($"An OIDC mapper is already registered for issuer '{pair.Key}'.", nameof(mappers));
                byIssuer[pair.Key] = pair.Value;
            }
            _byIssuer = byIssuer;
        }

        /// <summary>
        /// Returns the mapper for <paramref name="principal"/>'s issuer, or <c>null</c> when
        /// the principal carries no issuer claim or no mapper is registered for it (e.g. a
        /// local-auth principal, which the caller maps through the local claim path instead).
        /// </summary>
        public IOidcClaimMapper? ResolveFor(ClaimsPrincipal principal)
        {
            if (principal == null)
                throw new ArgumentNullException(nameof(principal));

            var issuer = principal.FindFirstValue("iss");
            if (string.IsNullOrWhiteSpace(issuer))
                return null;

            return _byIssuer.TryGetValue(issuer, out var mapper) ? mapper : null;
        }
    }

    /// <summary>
    /// Fluent builder for the issuer-to-mapper pairs consumed by an
    /// <see cref="OidcClaimMapperRegistry"/>. Used by <c>AddBifrostOidcClaimMappers</c> so a
    /// deployment registers each OIDC provider it accepts alongside the issuer URL the
    /// provider stamps into the <c>iss</c> claim.
    /// </summary>
    public sealed class OidcClaimMapperBuilder
    {
        private readonly List<KeyValuePair<string, IOidcClaimMapper>> _mappers = new();

        /// <summary>
        /// Registers <paramref name="mapper"/> for principals whose <c>iss</c> claim equals
        /// <paramref name="issuer"/>. Throws <see cref="ArgumentException"/> on a blank issuer
        /// and <see cref="ArgumentNullException"/> on a null mapper.
        /// </summary>
        public OidcClaimMapperBuilder Add(string issuer, IOidcClaimMapper mapper)
        {
            if (string.IsNullOrWhiteSpace(issuer))
                throw new ArgumentException("Issuer must not be blank.", nameof(issuer));
            if (mapper == null)
                throw new ArgumentNullException(nameof(mapper));

            _mappers.Add(new KeyValuePair<string, IOidcClaimMapper>(issuer, mapper));
            return this;
        }

        /// <summary>
        /// Registers a <see cref="Microsoft365ClaimMapper"/> for the given Entra issuer URL.
        /// </summary>
        public OidcClaimMapperBuilder AddMicrosoft365(string issuer, OidcClaimMapping? mapping = null)
            => Add(issuer, new Microsoft365ClaimMapper(mapping));

        /// <summary>
        /// Registers a <see cref="GoogleClaimMapper"/> for the given Google issuer URL
        /// (typically <c>https://accounts.google.com</c>).
        /// </summary>
        public OidcClaimMapperBuilder AddGoogle(string issuer, OidcClaimMapping? mapping = null)
            => Add(issuer, new GoogleClaimMapper(mapping));

        /// <summary>Returns the configured issuer-to-mapper pairs.</summary>
        public IReadOnlyList<KeyValuePair<string, IOidcClaimMapper>> Build() => _mappers;
    }

    /// <summary>
    /// Maps Microsoft 365 / Entra ID OIDC claims into <see cref="AppIdentity"/>. The Entra
    /// tenant id arrives in the <c>tid</c> claim and directory group memberships in the
    /// <c>groups</c> claim; both are configurable through the constructor for deployments
    /// that present a custom claim shape.
    /// </summary>
    public sealed class Microsoft365ClaimMapper : OidcClaimMapperBase
    {
        /// <summary>Provider name written into <see cref="AppIdentity.Provider"/>.</summary>
        public const string ProviderName = "oidc:microsoft365";

        /// <summary>
        /// Creates a Microsoft 365 mapper. Pass a custom <paramref name="mapping"/> to point
        /// the tenant/groups projection at non-default claim types.
        /// </summary>
        public Microsoft365ClaimMapper(OidcClaimMapping? mapping = null)
            : base(mapping)
        {
        }

        /// <inheritdoc />
        public override string Provider => ProviderName;

        /// <inheritdoc />
        protected override OidcClaimMapping DefaultMapping => new()
        {
            TenantClaimType = "tid",
            GroupsClaimType = "groups",
        };
    }

    /// <summary>
    /// Maps Google OIDC claims into <see cref="AppIdentity"/>. Google issues no tenant
    /// claim by default, so the default mapping leaves the tenant unmapped; a deployment
    /// that wants Workspace-domain isolation can supply a mapping pointed at the Google
    /// hosted-domain claim (<c>hd</c>) or an app-specific org claim.
    /// </summary>
    public sealed class GoogleClaimMapper : OidcClaimMapperBase
    {
        /// <summary>Provider name written into <see cref="AppIdentity.Provider"/>.</summary>
        public const string ProviderName = "oidc:google";

        /// <summary>
        /// Creates a Google mapper. Pass a custom <paramref name="mapping"/> to map a
        /// tenant/org claim (Google supplies none by default).
        /// </summary>
        public GoogleClaimMapper(OidcClaimMapping? mapping = null)
            : base(mapping)
        {
        }

        /// <inheritdoc />
        public override string Provider => ProviderName;

        /// <inheritdoc />
        protected override OidcClaimMapping DefaultMapping => new()
        {
            TenantClaimType = null,
            GroupsClaimType = null,
        };
    }
}
