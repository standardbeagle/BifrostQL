using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using BifrostQL.Core.Auth;
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
        /// Reads the same claim shape <see cref="LocalAuthEndpoint.BuildPrincipal"/> emits, and
        /// falls back to standard claim types so OIDC principals map cleanly too.
        /// </summary>
        internal static AppIdentity BuildAppIdentity(ClaimsPrincipal principal)
        {
            var id = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue("sub")
                ?? principal.Identity?.Name
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
                id = "anonymous";

            var provider = principal.FindFirstValue(LocalAuthClaims.Provider) ?? "unknown";
            var email = principal.FindFirstValue(ClaimTypes.Email);
            var displayName = principal.FindFirstValue(ClaimTypes.Name);
            var tenantId = principal.FindFirstValue(LocalAuthClaims.Tenant);
            var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

            return new AppIdentity(
                id: id,
                provider: provider,
                email: email,
                displayName: displayName,
                tenantId: string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
                roles: roles);
        }
    }
}
