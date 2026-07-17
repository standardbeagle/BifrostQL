using System.Security.Claims;
using System.Text;
using BifrostQL.Server.Auth;
using BifrostQL.Server.OData;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// Shared fixtures for the OData auth tests: principal builders, an in-memory Basic
    /// credential store, and Basic authorization-header encoding.
    /// </summary>
    internal static class ODataTestAuth
    {
        public const string Username = "odata-user";
        public const string Password = "correct-horse-battery-staple";

        /// <summary>An authenticated local principal with a subject claim.</summary>
        public static ClaimsPrincipal Principal(string subject = "odata-user", string? tenant = null)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject) };
            if (tenant is not null) claims.Add(new Claim(LocalAuthClaims.Tenant, tenant));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "odata"));
        }

        /// <summary>A principal that authenticates but carries no subject claim — must fail closed on projection.</summary>
        public static ClaimsPrincipal SubjectlessPrincipal()
            => new(new ClaimsIdentity(Array.Empty<Claim>(), authenticationType: "odata"));

        /// <summary>An authenticated principal carrying an OIDC issuer no mapper is registered for.</summary>
        public static ClaimsPrincipal UnmappedIssuerPrincipal()
            => new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "user-1"),
                new Claim("iss", "https://idp.example.test"),
            }, authenticationType: "oidc"));

        public static string BasicHeader(string username, string password)
            => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
    }

    /// <summary>In-memory Basic credential store for tests; unknown usernames resolve to null (never a fallback).</summary>
    internal sealed class FakeODataBasicCredentialStore : IODataBasicCredentialStore
    {
        private readonly Dictionary<string, ODataBasicCredential> _credentials = new(StringComparer.Ordinal);

        public FakeODataBasicCredentialStore Add(string username, string secret, ClaimsPrincipal principal, bool enabled = true)
        {
            _credentials[username] = new ODataBasicCredential(username, secret, principal, enabled);
            return this;
        }

        public Task<ODataBasicCredential?> FindAsync(string username, CancellationToken cancellationToken)
            => Task.FromResult(_credentials.TryGetValue(username, out var credential) ? credential : null);
    }
}
