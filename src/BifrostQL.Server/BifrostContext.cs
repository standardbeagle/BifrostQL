using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Server.Auth;

namespace BifrostQL.Server
{
    internal class BifrostContext : Dictionary<string, object?>
    {
        public ClaimsPrincipal? User { get; init; }

        public BifrostContext(HttpContext context)
        {
            User = context.User;
            if (User == null) return;

            // Project the authenticated ClaimsPrincipal into the provider-agnostic
            // AppIdentity contract, then delegate to the Core IdentityContextMapper so
            // local-user logins and OIDC logins both populate the user context the same
            // way (tenant/roles/audit keys consumed by the security modules).
            var identity = BuildAppIdentity(User);
            var mapper = new IdentityContextMapper();
            foreach (var kv in mapper.ToUserContext(identity))
                this[kv.Key] = kv.Value;

            // Preserve the raw principal and the legacy per-claim-type arrays so existing
            // consumers that read claims directly keep working.
            this["user"] = User;
            foreach (var g in User.Claims.GroupBy(c => c.Type))
            {
                if (!ContainsKey(g.Key))
                    this[g.Key] = g.Select(c => c.Value).ToArray();
            }
        }

        /// <summary>
        /// Builds an <see cref="AppIdentity"/> from an authenticated <see cref="ClaimsPrincipal"/>.
        /// When an <see cref="IOidcClaimMapper"/> is supplied (the principal came from an OIDC
        /// provider rather than local auth) the mapper owns the projection so Google and
        /// Microsoft 365 principals normalize to the identical contract local auth produces;
        /// otherwise the shared local-auth claim shape is read directly.
        /// </summary>
        internal static AppIdentity BuildAppIdentity(ClaimsPrincipal principal, IOidcClaimMapper? oidcMapper = null)
        {
            if (oidcMapper != null)
                return oidcMapper.Map(principal);

            var id = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue("sub")
                ?? principal.Identity?.Name
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
            {
                // An authenticated principal with no subject claim is a misconfigured
                // token (bad OIDC mapping, malformed cookie). Collapsing it to the
                // "anonymous" sentinel would merge distinct broken principals and run
                // tenant/row-scope checks against a bogus identity — fail instead.
                // OidcClaimMapperBase.Map is already strict here; keep the local path
                // symmetric. An unauthenticated principal legitimately has no subject.
                if (principal.Identity?.IsAuthenticated == true)
                    throw new InvalidOperationException(
                        "Authenticated principal has no subject claim (NameIdentifier/sub/Name); " +
                        "refusing to collapse it to an anonymous identity.");
                id = "anonymous";
            }

            var provider = principal.FindFirstValue(LocalAuthClaims.Provider) ?? "unknown";
            var email = principal.FindFirstValue(ClaimTypes.Email);
            var displayName = principal.FindFirstValue(ClaimTypes.Name);
            var tenantId = principal.FindFirstValue(LocalAuthClaims.Tenant);
            var orgIds = principal.FindAll(LocalAuthClaims.Org).Select(c => c.Value).ToArray();
            var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

            // Re-surface the household provider claim carried through the cookie so
            // the households policy row-scope resolves for the caller.
            IReadOnlyDictionary<string, object?>? claims = null;
            var household = principal.FindFirstValue(LocalAuthClaims.Household);
            if (!string.IsNullOrWhiteSpace(household))
                claims = new Dictionary<string, object?>
                {
                    [MetadataKeys.Auth.HouseholdClaimKey] = household,
                };

            return new AppIdentity(
                id: id,
                provider: provider,
                email: email,
                displayName: displayName,
                tenantId: string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
                orgIds: orgIds,
                roles: roles,
                claims: claims);
        }
    }
}
