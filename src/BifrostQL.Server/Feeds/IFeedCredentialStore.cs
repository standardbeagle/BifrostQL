namespace BifrostQL.Server.Feeds
{
    /// <summary>
    /// Resolves a raw feed token to its <see cref="FeedCredential"/>. This is the pluggable,
    /// host-supplied identity source the feed front door authenticates scoped-token requests against.
    /// BifrostQL invents NO built-in persistent issuer or token store: minting, revocation, expiry,
    /// and rotation are entirely the host's to own, so a deployment that only accepts Bearer
    /// identities registers no store and every scoped-token request then fails closed.
    ///
    /// <para>Contract the store MUST honor (.claude/rules/protocol-adapter-security.md):</para>
    /// <list type="bullet">
    /// <item>It OWNS token lookup and any secret comparison. That comparison MUST be constant-time /
    /// anti-enumeration — an unknown token must be indistinguishable by timing from a known one — so
    /// the authenticator never sees, compares, or holds raw secret material (invariant 2).</item>
    /// <item>An unknown, revoked-at-source, or otherwise unresolvable token resolves to <c>null</c>
    /// (auth fails closed). It NEVER returns an ambient/anonymous identity to stand in for a failed
    /// lookup.</item>
    /// <item>The <paramref name="token"/> value must never be logged, echoed, or used as a cache key
    /// by an implementation (token values never leave this boundary).</item>
    /// </list>
    /// </summary>
    public interface IFeedCredentialStore
    {
        /// <summary>
        /// Resolves the credential for <paramref name="token"/>, or <c>null</c> when no credential
        /// exists for it. Never returns a fallback identity. Enabled/expiry state may be carried on
        /// the returned <see cref="FeedCredential"/> (the authenticator fails closed on either), or a
        /// revoked token may simply resolve to <c>null</c> here — both are indistinguishable on the
        /// wire.
        /// </summary>
        Task<FeedCredential?> ResolveAsync(string token, CancellationToken cancellationToken);
    }
}
