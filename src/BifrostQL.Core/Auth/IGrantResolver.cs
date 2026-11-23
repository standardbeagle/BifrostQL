namespace BifrostQL.Core.Auth;

/// <summary>
/// Optional per-request grant loading hook. When an application registers an
/// implementation (<c>AddBifrostGrantResolver</c>), it runs every time the user
/// context is assembled (at least once per request; nothing memoizes it), and its result is UNIONED into the
/// <c>permissions</c> user-context key before any security module or transformer
/// sees the context.
///
/// Transport shape: HTTP mounts (GraphQL, binary WebSocket, MCP-HTTP) run it per
/// request; the connection-oriented adapters (pgwire, RESP, LDAP) project identity
/// once per CONNECTION at login, so there a permission change takes effect on the
/// client's next connection; the Prometheus scrape identity never runs it (no
/// request service provider exists for a scoped resolver).
///
/// Execution: every shipped gate awaits the resolver through
/// <c>IBifrostAuthContextFactory.CreateUserContextAsync</c>; no thread blocks on it. It runs
/// once per projection (an endpoint that projects the caller and also takes the policy gate
/// runs it twice), so keep the read bounded and cheap.
///
/// The hook exists for deployments whose capability sets live in the database and
/// must take effect immediately: a token (JWT, cookie) can live for weeks, but a
/// permission change applies on the very next request because the grants are read
/// per request rather than baked into the token. The resolver is therefore
/// expected to perform a database read; caching (and its revocation policy) is
/// the application's business, not BifrostQL's.
///
/// Contract:
/// <list type="bullet">
///   <item><description>
///     Return the grant names this identity holds IN ADDITION to whatever the
///     token already carries — the result is unioned with the identity's mapped
///     permissions, never a replacement.
///   </description></item>
///   <item><description>
///     Returning <c>null</c> is treated as an empty grant set: the identity's own
///     permissions remain, nothing is added.
///   </description></item>
///   <item><description>
    ///     Throwing fails CLOSED (E9): the request runs with an EMPTY permission set
    ///     — the identity's pre-resolver permissions are wiped — and a Warning naming
    ///     the identity id is logged. The exception never reaches the wire and never
    ///     leaves the caller with broader access than a grant-less caller. Only the
    ///     <c>permissions</c> key is wiped: token <c>roles</c> are claims of the token
    ///     itself, not resolver-sourced grants, and remain.
///   </description></item>
/// </list>
///
/// The identity's <c>TenantId</c> is the identity mapper's tenant (<c>tenant_id</c>), not the
/// value under a model's <c>tenant-context-key</c>: the resolver runs before any model is
/// resolved. With <c>tenant-context-key: org_id</c> fed by a raw <c>org_id</c> claim, the
/// resolver gets <c>TenantId = null</c>. If grants are per tenant, read the claim from
/// <see cref="AppIdentity.Claims"/>: it holds the principal's claims, one <c>string[]</c> per
/// claim type, keyed case-insensitively (<c>identity.Claims["org_id"]</c>). The resolver is
/// handed no principal, and pgwire, RESP and LDAP have no <c>IHttpContextAccessor</c>.
///
/// <see cref="AppIdentity.Claims"/> is the raw principal, not the scoped user context. It
/// includes claim types the user context deliberately drops: a raw claim named after a key the
/// identity mapper owns (<c>tenant_id</c>, <c>tenant_ids</c>, <c>roles</c>, <c>user_id</c>,
/// <c>id</c>, <c>permissions</c>) never reaches the context. The tenant filter reads the user
/// context under <c>tenant-context-key</c>. For the default <c>tenant_id</c> that is the identity
/// mapper's value, which <c>TenantId</c> already carries. For an OIDC principal it is what the
/// issuer's claim mapper produced, so <c>Claims["tenant_id"]</c> may be absent or differ from
/// it. Only for a key naming a raw claim the mapper does not own (<c>org_id</c>) does the filter
/// read that claim, and only as a single value: a multi-valued claim is refused. Key grants on
/// <c>TenantId</c> under the default key, and on a single-valued
/// <c>Claims[tenant-context-key]</c> only when that key names such a raw claim.
///
/// <c>permissions</c> is an owned key (<see cref="IdentityContextMapper.OwnedKeyNames"/>),
/// so a wire-supplied value can never add to it; the resolver is the only way grants
/// enter beyond the authenticated identity itself.
/// </summary>
public interface IGrantResolver
{
    /// <summary>
    /// Resolves the additional grant names for <paramref name="identity"/>. Called
    /// once per user-context assembly, for authenticated principals only — per
    /// request on HTTP mounts, per connection login on pgwire/RESP/LDAP.
    /// </summary>
    ValueTask<IReadOnlyCollection<string>> ResolveAsync(AppIdentity identity, CancellationToken ct);
}
