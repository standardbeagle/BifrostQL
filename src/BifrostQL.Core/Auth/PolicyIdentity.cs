using System.Collections;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Auth;

/// <summary>
/// Projects a per-request user context (the dictionary <c>IdentityContextMapper</c>
/// writes) into the <see cref="AppIdentity"/> the <see cref="PolicyEvaluator"/>
/// checks. Extracted from <c>PolicyFilterTransformer</c> so every policy-gated
/// surface reconstructs identity <b>identically</b>: the query-path filter
/// transformer AND any out-of-band read gate (e.g. the pgwire catalog visibility
/// filter) must resolve the same user id, roles, and permissions from the same context keys.
/// A second, drifting projection would be a weaker — potentially fail-open —
/// authorization check, so this is the single source of the projection.
/// </summary>
public static class PolicyIdentity
{
    private const string UserIdContextKey = MetadataKeys.Auth.DefaultUserIdContextKey;
    private const string RolesContextKey = MetadataKeys.Auth.DefaultRolesContextKey;
    private const string PermissionsContextKey = MetadataKeys.Auth.DefaultPermissionsContextKey;

    /// <summary>
    /// Builds the identity for a policy check from <paramref name="userContext"/>.
    /// A request with no resolved user still yields a stable <c>anonymous</c>
    /// identity so policy checks run normally (and deny by the table's rules)
    /// rather than being skipped.
    /// </summary>
    public static AppIdentity FromUserContext(IDictionary<string, object?> userContext)
        => FromUserContext(userContext, MetadataKeys.Auth.DefaultTenantContextKey);

    /// <summary>Builds the policy identity including the configured tenant claim.</summary>
    public static AppIdentity FromUserContext(IDictionary<string, object?> userContext, string? tenantContextKey)
    {
        if (userContext is null) throw new ArgumentNullException(nameof(userContext));

        var userId = userContext.TryGetValue(UserIdContextKey, out var idValue) && idValue is not null
            ? idValue.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(userId))
            userId = "anonymous";

        tenantContextKey = string.IsNullOrWhiteSpace(tenantContextKey)
            ? MetadataKeys.Auth.DefaultTenantContextKey
            : tenantContextKey;
        var tenantId = userContext.TryGetValue(tenantContextKey, out var tenantValue)
            ? tenantValue?.ToString()
            : null;

        return new AppIdentity(userId, "query-context", tenantId: tenantId,
            roles: ExtractRoles(userContext), permissions: ExtractPermissions(userContext));
    }

    /// <summary>
    /// Reads the caller's roles from the canonical <c>roles</c> context key,
    /// tolerating the shapes it can arrive in: a single string, a typed string
    /// sequence, or an untyped claim array.
    /// </summary>
    public static IReadOnlyList<string> ExtractRoles(IDictionary<string, object?> userContext)
    {
        if (userContext is null) throw new ArgumentNullException(nameof(userContext));

        return ExtractStrings(userContext, RolesContextKey);
    }

    public static IReadOnlyList<string> ExtractPermissions(IDictionary<string, object?> userContext)
    {
        if (userContext is null) throw new ArgumentNullException(nameof(userContext));
        // Permissions are first-class grants, allowing policy checks without roles.
        return ExtractStrings(userContext, PermissionsContextKey);
    }

    private static IReadOnlyList<string> ExtractStrings(IDictionary<string, object?> userContext, string key)
    {
        if (!userContext.TryGetValue(key, out var value) || value is null)
            return Array.Empty<string>();

        if (value is string single)
            return new[] { single };

        if (value is IEnumerable<string> typedRoles)
            return typedRoles.ToArray();

        if (value is IEnumerable sequence)
        {
            var result = new List<string>();
            foreach (var item in sequence)
            {
                var role = item?.ToString();
                if (!string.IsNullOrWhiteSpace(role))
                    result.Add(role);
            }
            return result;
        }

        return Array.Empty<string>();
    }
}
