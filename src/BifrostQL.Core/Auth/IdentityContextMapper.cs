using BifrostQL.Core.Model;

namespace BifrostQL.Core.Auth;

/// <summary>
/// Projects an <see cref="AppIdentity"/> into the <c>UserContext</c> dictionary
/// (<c>IDictionary&lt;string, object?&gt;</c>) consumed by the security modules.
///
/// The produced dictionary carries the canonical claim set:
///   <list type="bullet">
///     <item><description>
///       Tenant key (default <c>tenant_id</c>) — read by <c>TenantFilterTransformer</c>,
///       configurable via the <c>tenant-context-key</c> model metadata. Omitted
///       when <see cref="AppIdentity.TenantId"/> is null.
///     </description></item>
///     <item><description>
///       Tenant-ids key (fixed <c>tenant_ids</c>) — the plural projection of
///       <see cref="AppIdentity.OrgIds"/>. Always written as
///       <c>IReadOnlyList&lt;string&gt;</c> (empty when the user has no orgs).
///     </description></item>
///     <item><description>
///       User-id key (fixed <c>user_id</c>) — the canonical explicit user
///       identifier, projected from <see cref="AppIdentity.Id"/>.
///     </description></item>
///     <item><description>
///       Roles key (default <c>roles</c>) — read by <c>AutoFilterTransformer</c> for
///       bypass-role checks. Written as <c>IReadOnlyList&lt;string&gt;</c>.
///     </description></item>
///     <item><description>
///       Permissions key (fixed <c>permissions</c>) — the projection of
///       <see cref="AppIdentity.Permissions"/>. Always written as
///       <c>IReadOnlyList&lt;string&gt;</c> (empty when the user has none).
///     </description></item>
///     <item><description>
///       Audit user key (default <c>id</c>) — read by <c>BasicAuditModule</c>,
///       configurable via the <c>user-audit-key</c> model metadata.
///     </description></item>
///   </list>
///
/// Precedence: provider <see cref="AppIdentity.Claims"/> are copied first, so
/// every mapped identity key above always takes precedence over a same-named
/// provider claim (provider-claims-first, mapped-keys-win).
/// </summary>
public sealed class IdentityContextMapper
{
    private readonly string _tenantContextKey;
    private readonly string _rolesContextKey;
    private readonly string _userAuditKey;

    /// <summary>
    /// Creates a mapper with optional overrides for the user-context key names.
    /// Each null argument falls back to the corresponding default in
    /// <see cref="MetadataKeys.Auth"/>.
    /// </summary>
    /// <param name="tenantContextKey">
    /// Key under which <see cref="AppIdentity.TenantId"/> is written. Defaults to
    /// <see cref="MetadataKeys.Auth.DefaultTenantContextKey"/>.
    /// </param>
    /// <param name="rolesContextKey">
    /// Key under which <see cref="AppIdentity.Roles"/> is written. Defaults to
    /// <see cref="MetadataKeys.Auth.DefaultRolesContextKey"/>.
    /// </param>
    /// <param name="userAuditKey">
    /// Key under which <see cref="AppIdentity.Id"/> is written for audit
    /// population. Defaults to <see cref="MetadataKeys.Auth.DefaultUserAuditKey"/>.
    /// </param>
    public IdentityContextMapper(
        string? tenantContextKey = null,
        string? rolesContextKey = null,
        string? userAuditKey = null)
    {
        _tenantContextKey = NormalizeKey(tenantContextKey, MetadataKeys.Auth.DefaultTenantContextKey, nameof(tenantContextKey));
        _rolesContextKey = NormalizeKey(rolesContextKey, MetadataKeys.Auth.DefaultRolesContextKey, nameof(rolesContextKey));
        _userAuditKey = NormalizeKey(userAuditKey, MetadataKeys.Auth.DefaultUserAuditKey, nameof(userAuditKey));
    }

    /// <summary>
    /// Maps <paramref name="identity"/> into a fresh user-context dictionary.
    /// </summary>
    public IDictionary<string, object?> ToUserContext(AppIdentity identity)
    {
        if (identity == null)
            throw new ArgumentNullException(nameof(identity));

        var context = new Dictionary<string, object?>();

        // Provider claims first so mapped identity keys below always win.
        foreach (var claim in identity.Claims)
            context[claim.Key] = claim.Value;

        context[_userAuditKey] = identity.Id;
        context[MetadataKeys.Auth.DefaultUserIdContextKey] = identity.Id;
        context[_rolesContextKey] = identity.Roles;
        context[MetadataKeys.Auth.DefaultPermissionsContextKey] = identity.Permissions;
        context[MetadataKeys.Auth.DefaultTenantIdsContextKey] = identity.OrgIds;

        if (identity.TenantId != null)
            context[_tenantContextKey] = identity.TenantId;

        return context;
    }

    private static string NormalizeKey(string? value, string fallback, string paramName)
    {
        if (value == null)
            return fallback;
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Context key must not be empty or whitespace.", paramName);
        return value;
    }
}
