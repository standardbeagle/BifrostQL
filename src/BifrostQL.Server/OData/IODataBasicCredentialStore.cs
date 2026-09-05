using System.Security.Claims;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// A resolved OData Basic credential: the username presented in the request, a one-way
    /// password hash the presented password is verified against, the candidate identity it
    /// maps to, and whether the credential is currently usable. The principal is a
    /// <i>candidate</i> only — it is still projected through
    /// <see cref="IBifrostAuthContextFactory"/>, which is where a subject-less or
    /// unmapped-issuer principal is rejected. A store must never hand back an ambient or
    /// anonymous identity to stand in for a failed lookup; unknown usernames resolve to
    /// <c>null</c> from <see cref="IODataBasicCredentialStore.FindAsync"/> instead.
    /// </summary>
    /// <param name="Username">The username presented in the Basic authorization header.</param>
    /// <param name="PasswordHash">
    /// A one-way hash of the password in ASP.NET Core <c>PasswordHasher&lt;TUser&gt;</c>
    /// format (the same contract <c>LocalUserStore</c> uses), verified with
    /// <c>IPasswordHasher&lt;string&gt;.VerifyHashedPassword</c>. The store therefore holds
    /// NO plaintext-equivalent: leaking the stored value does not leak a usable password.
    /// An unknown/disabled credential is verified against a fixed dummy hash, so the same
    /// PBKDF2 work runs regardless (no account-existence timing oracle).
    /// </param>
    /// <param name="Principal">The authenticated identity this credential maps to when enabled.</param>
    /// <param name="Enabled">
    /// Whether the credential is currently usable. A disabled credential must fail the same way
    /// an unknown one does (fail closed) — never distinguish "disabled" from "unknown".
    /// </param>
    public sealed record ODataBasicCredential(string Username, string PasswordHash, ClaimsPrincipal Principal, bool Enabled);

    /// <summary>
    /// Resolves an OData Basic username to its <see cref="ODataBasicCredential"/>. This is the
    /// pluggable identity source the OData front door authenticates Basic requests against.
    /// Basic auth is optional: a deployment that only accepts Bearer tokens registers no store,
    /// and a Basic request then fails closed with 401. Single hard rule: an unknown username
    /// resolves to <c>null</c> (auth fails), never to an ambient/anonymous identity.
    /// </summary>
    public interface IODataBasicCredentialStore
    {
        /// <summary>
        /// Resolves the credential for <paramref name="username"/>, or <c>null</c> when no such
        /// credential exists. Never returns a fallback identity.
        /// </summary>
        Task<ODataBasicCredential?> FindAsync(string username, CancellationToken cancellationToken);
    }
}
