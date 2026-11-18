using System.Security.Claims;

namespace BifrostQL.Server.Auth
{
    /// <summary>What <see cref="LocalAuthEndpoint.RevalidateSessionAsync"/> decided about a principal.</summary>
    internal enum LocalSessionVerdict
    {
        /// <summary>Not a local-auth principal; local sessions do not govern it.</summary>
        NotLocal,

        /// <summary>The session is live and the principal's claims are recent enough to keep.</summary>
        Live,

        /// <summary>The session is live; adopt <see cref="LocalSessionCheck.Principal"/>, rebuilt from the user row.</summary>
        Refreshed,

        /// <summary>The session ended (or never existed). Stop serving this principal.</summary>
        Rejected,
    }

    /// <summary>The outcome of one local-auth session check, taken at <paramref name="CheckedAt"/>.</summary>
    internal readonly record struct LocalSessionCheck(
        LocalSessionVerdict Verdict, ClaimsPrincipal? Principal, DateTimeOffset CheckedAt);
}
