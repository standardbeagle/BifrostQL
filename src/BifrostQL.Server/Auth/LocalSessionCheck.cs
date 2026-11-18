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

        /// <summary>
        /// The session is live but enrollment-only: the user owes a second factor they have not yet
        /// enrolled. Only the enrollment and logout endpoints serve it; every other surface (the
        /// binary WebSocket, the chat stream, any other HTTP route) refuses it like
        /// <see cref="Rejected"/>, without ending the session. <see cref="LocalSessionCheck.Principal"/>
        /// is the rebuilt principal when the row was re-read, else null.
        /// </summary>
        EnrollmentOnly,

        /// <summary>The session ended (or never existed). Stop serving this principal.</summary>
        Rejected,
    }

    /// <summary>The outcome of one local-auth session check, taken at <paramref name="CheckedAt"/>.</summary>
    internal readonly record struct LocalSessionCheck(
        LocalSessionVerdict Verdict, ClaimsPrincipal? Principal, DateTimeOffset CheckedAt);
}
