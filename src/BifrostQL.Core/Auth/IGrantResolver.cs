namespace BifrostQL.Core.Auth;

/// <summary>
/// Optional per-request grant loading hook. When an application registers an
/// implementation (<c>AddBifrostGrantResolver</c>), it runs once per request at
/// the point the user context is assembled, and its result is UNIONED into the
/// <c>permissions</c> user-context key before any security module or transformer
/// sees the context.
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
///     leaves the caller with broader access than a grant-less caller.
///   </description></item>
/// </list>
///
/// <c>permissions</c> is an owned key (<see cref="IdentityContextMapper.OwnedKeyNames"/>),
/// so a wire-supplied value can never add to it; the resolver is the only way grants
/// enter beyond the authenticated identity itself.
/// </summary>
public interface IGrantResolver
{
    /// <summary>
    /// Resolves the additional grant names for <paramref name="identity"/>. Called
    /// once per request, for authenticated principals only.
    /// </summary>
    ValueTask<IReadOnlyCollection<string>> ResolveAsync(AppIdentity identity, CancellationToken ct);
}
