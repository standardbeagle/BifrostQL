namespace BifrostQL.Core.Auth;

/// <summary>
/// Provider-agnostic internal identity contract produced by every authentication
/// path (local auth, OIDC, and any future provider). It is a pure data type with
/// no Server or ASP.NET dependency: the authentication layer is responsible for
/// constructing it, and <see cref="IdentityContextMapper"/> projects it into the
/// <c>UserContext</c> dictionary consumed by the security modules.
///
/// The record is immutable. <see cref="OrgIds"/>, <see cref="Roles"/>,
/// <see cref="Permissions"/>, and <see cref="Claims"/> are normalized to
/// non-null empty collections by the constructor so consumers never need to
/// null-check them.
/// </summary>
public sealed record AppIdentity
{
    /// <summary>Stable, provider-neutral identifier for the authenticated user.</summary>
    public string Id { get; }

    /// <summary>The user's email address, if known.</summary>
    public string? Email { get; }

    /// <summary>Human-readable display name, if known.</summary>
    public string? DisplayName { get; }

    /// <summary>
    /// Name of the authentication provider that produced this identity
    /// (e.g. "local", "oidc:google"). Used for diagnostics and provider-specific
    /// behavior; never consumed by the security filters.
    /// </summary>
    public string Provider { get; }

    /// <summary>
    /// Primary tenant identifier for tenant isolation, if the user belongs to a
    /// single tenant. Mapped to the tenant context key by
    /// <see cref="IdentityContextMapper"/>.
    /// </summary>
    public string? TenantId { get; }

    /// <summary>
    /// All organization identifiers the user belongs to. Never null; empty when
    /// the user has no organization memberships.
    /// </summary>
    public IReadOnlyList<string> OrgIds { get; }

    /// <summary>
    /// Roles granted to the user. Never null; empty when the user has no roles.
    /// </summary>
    public IReadOnlyList<string> Roles { get; }

    /// <summary>
    /// Fine-grained permissions granted to the user. Never null; empty when the
    /// user has no explicit permissions. Projected to the canonical
    /// <c>permissions</c> user-context claim by <see cref="IdentityContextMapper"/>.
    /// </summary>
    public IReadOnlyList<string> Permissions { get; }

    /// <summary>
    /// Additional provider claims keyed by claim name. Never null; empty when the
    /// provider supplied no extra claims. Copied verbatim into the user context.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Claims { get; }

    public AppIdentity(
        string id,
        string provider,
        string? email = null,
        string? displayName = null,
        string? tenantId = null,
        IReadOnlyList<string>? orgIds = null,
        IReadOnlyList<string>? roles = null,
        IReadOnlyDictionary<string, object?>? claims = null,
        IReadOnlyList<string>? permissions = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Identity id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Identity provider is required.", nameof(provider));

        Id = id;
        Provider = provider;
        Email = email;
        DisplayName = displayName;
        TenantId = tenantId;
        OrgIds = orgIds ?? Array.Empty<string>();
        Roles = roles ?? Array.Empty<string>();
        Claims = claims ?? new Dictionary<string, object?>();
        Permissions = permissions ?? Array.Empty<string>();
    }
}
