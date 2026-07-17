using System.Security.Claims;

namespace BifrostQL.Server.S3
{
    /// <summary>
    /// A resolved S3 access-key credential: the shared secret SigV4 verification proves
    /// knowledge of, the candidate identity it maps to, and whether the key is currently
    /// usable. The principal is a <i>candidate</i> only — it is still projected through
    /// <see cref="IBifrostAuthContextFactory"/>, which is where a subject-less or
    /// unmapped-issuer principal is rejected. A store must never hand back an ambient or
    /// ambient/anonymous identity to stand in for a failed lookup; unknown keys resolve to
    /// <c>null</c> from <see cref="IS3AccessKeyStore.FindAsync"/> instead.
    /// </summary>
    /// <param name="AccessKeyId">The access key id presented in the request's credential scope.</param>
    /// <param name="SecretAccessKey">
    /// The shared secret used to derive the SigV4 signing key. Never transmitted; the client
    /// proves knowledge of it via the HMAC chain, so this never crosses the wire either.
    /// </param>
    /// <param name="Principal">The authenticated identity this key maps to when enabled.</param>
    /// <param name="Enabled">
    /// Whether the key is currently usable. A disabled key must fail the same way an unknown
    /// key does (fail closed) — never distinguish "disabled" from "unknown" in the response.
    /// </param>
    public sealed record S3AccessKey(string AccessKeyId, string SecretAccessKey, ClaimsPrincipal Principal, bool Enabled);

    /// <summary>
    /// Resolves an S3 access key id to its <see cref="S3AccessKey"/>. This is the pluggable
    /// identity source the S3 front door authenticates against. Single hard rule: an unknown
    /// access key id resolves to <c>null</c> (auth fails), never to an ambient/anonymous
    /// identity. There is deliberately no default registration — a deployment that enables
    /// the S3 endpoint must supply one.
    /// </summary>
    public interface IS3AccessKeyStore
    {
        /// <summary>
        /// Resolves the key for <paramref name="accessKeyId"/>, or <c>null</c> when no such
        /// key exists. Never returns a fallback identity.
        /// </summary>
        Task<S3AccessKey?> FindAsync(string accessKeyId, CancellationToken cancellationToken);
    }
}
