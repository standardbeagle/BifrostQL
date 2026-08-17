using System.Security.Claims;
using System.Text;
using BifrostQL.Server.Ldap;
using Microsoft.AspNetCore.Http;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// The two-tenant directory identity the end-to-end suites bind as: <c>alice</c> in tenant
    /// <c>acme</c> and <c>bob</c> in tenant <c>globex</c>, resolved through the same
    /// <see cref="ILdapCredentialStore"/> / <see cref="ILdapPasswordHasher"/> seams a deployment
    /// must register (there is no ambient default for either).
    ///
    /// <para>Shared rather than duplicated per suite so a cross-tenant claim proven on the
    /// loopback handler and one proven against a real Kestrel listener are claims about the SAME
    /// identity, not about two fixtures that happen to look alike.</para>
    /// </summary>
    internal static class LdapTestIdentity
    {
        public const string Password = "s3cret";

        public static string Dn(string uid) => $"uid={uid},ou=people,dc=example,dc=com";

        /// <summary>Verifies against a stored hash, exactly as a bcrypt/Argon2id hasher would.</summary>
        public sealed class Hasher : ILdapPasswordHasher
        {
            public string DecoyHash => "hash:$decoy$";

            public bool Verify(ReadOnlySpan<byte> password, string passwordHash) =>
                passwordHash != DecoyHash && passwordHash == "hash:" + Encoding.UTF8.GetString(password);
        }

        /// <summary>Resolves a bind DN to its stored hash and the principal it authenticates as.</summary>
        public sealed class Store : ILdapCredentialStore
        {
            public Task<LdapCredentialRecord?> FindAsync(string bindDn, CancellationToken ct)
            {
                var tenant = TenantOf(bindDn);
                if (tenant is null)
                    return Task.FromResult<LdapCredentialRecord?>(null);

                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.Name, bindDn),
                        new Claim("tenant", tenant),
                    }, "ldap"));
                return Task.FromResult<LdapCredentialRecord?>(
                    new LdapCredentialRecord("hash:" + Password, principal, Enabled: true));
            }

            public static string? TenantOf(string bindDn) => bindDn switch
            {
                _ when string.Equals(bindDn, Dn("alice"), StringComparison.OrdinalIgnoreCase) => "acme",
                _ when string.Equals(bindDn, Dn("bob"), StringComparison.OrdinalIgnoreCase) => "globex",
                _ => null,
            };
        }

        /// <summary>
        /// Projects the bound principal into the user context every intent is scoped by — the same
        /// seam shape the HTTP and pgwire gates use. A principal with no subject projects to an
        /// EMPTY context, which the bind path treats as a rejection rather than as anonymous.
        /// </summary>
        public sealed class Factory : IBifrostAuthContextFactory
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

            public IDictionary<string, object?> CreateUserContext(
                HttpContext context, IDictionary<string, object?> existing) => CreateUserContext(context);
        }
    }
}
