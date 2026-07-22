using System.Security.Claims;

namespace BifrostQL.Server.Feeds
{
    /// <summary>
    /// A feed credential resolved by an <see cref="IFeedCredentialStore"/>: the candidate identity a
    /// feed token maps to, the set of feed tables the token is scoped to, whether it is currently
    /// usable, and an optional expiry instant.
    ///
    /// <para>The <see cref="Principal"/> is a CANDIDATE only. It is still projected through
    /// <see cref="IBifrostAuthContextFactory"/>, which is where a subject-less or unmapped-issuer
    /// principal is rejected — a store must never hand back an ambient/anonymous identity to stand in
    /// for a failed lookup (return <c>null</c> from <see cref="IFeedCredentialStore.ResolveAsync"/>
    /// instead).</para>
    ///
    /// <para>This record carries NO token or secret material: token lookup and comparison live behind
    /// the store contract (see <see cref="IFeedCredentialStore"/>), so a resolved credential can never
    /// re-expose the token to a caller, a log, a cache key, or an ETag
    /// (.claude/rules/protocol-adapter-security.md — token values never leave the store boundary).</para>
    /// </summary>
    /// <param name="Principal">The authenticated identity this token maps to when usable.</param>
    /// <param name="AllowedTables">
    /// The feed tables this token may read, matched by ordinal equality against the requested table.
    /// An empty set matches nothing (fail closed): a token scoped to no table authenticates for no
    /// feed. The names must use the same convention the endpoint passes as its requested table.
    /// </param>
    /// <param name="Enabled">
    /// Whether the credential is currently usable. A disabled/revoked credential must fail the same
    /// way an unknown one does (fail closed) — never distinguish "revoked" from "unknown" on the wire.
    /// </param>
    /// <param name="ExpiresAt">
    /// Optional expiry. A credential whose expiry is at or before now fails closed identically to a
    /// revoked or unknown one. <c>null</c> means the store imposes no time bound (revocation is then
    /// the store's to own).
    /// </param>
    public sealed record FeedCredential(
        ClaimsPrincipal Principal,
        IReadOnlyCollection<string> AllowedTables,
        bool Enabled,
        DateTimeOffset? ExpiresAt = null);
}
