using System.Security.Claims;
using BifrostQL.Server;

namespace BifrostQL.Mcp.Test
{
    /// <summary>
    /// User-context providers for in-memory MCP sessions, built on the stdio adapter's production
    /// projection (<see cref="BifrostMcpAdapter.CreateUserContextProvider"/>): every tool call
    /// projects the principal afresh through the shared <see cref="IBifrostAuthContextFactory"/>.
    /// A principal is presented as a bearer token the host validator maps to it; no principal is
    /// the <see cref="McpAuthMode.AnonymousDev"/> opt-in, the only posture that serves an empty
    /// context.
    /// </summary>
    internal static class McpTestIdentities
    {
        private const string PrincipalToken = "test-principal-token";

        internal static Func<CancellationToken, ValueTask<IDictionary<string, object?>>> ProviderFor(
            IBifrostAuthContextFactory factory, IServiceProvider services, ClaimsPrincipal? principal)
            => BifrostMcpAdapter.CreateUserContextProvider(factory, services, principal is null
                ? new McpAuthOptions { Mode = McpAuthMode.AnonymousDev }
                : new McpAuthOptions
                {
                    Mode = McpAuthMode.Bearer,
                    BearerToken = PrincipalToken,
                    ValidateBearerToken = token => token == PrincipalToken ? principal : null,
                });

        internal static ClaimsPrincipal WithRoles(params string[] roles)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "user-" + string.Join("-", roles)) };
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
        }
    }
}
