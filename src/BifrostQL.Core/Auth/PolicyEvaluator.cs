using BifrostQL.Core.Model;

namespace BifrostQL.Core.Auth;

/// <summary>
/// Stateless evaluator for the server-side authorization policy engine. Given a
/// <see cref="TablePolicy"/> and an <see cref="AppIdentity"/>, answers whether a
/// table action or column access is permitted.
///
/// No I/O, no mutable state — safe to share as a singleton.
///
/// Decision rules (in order):
///   1. Admin bypass — an identity holding the configured admin role is allowed
///      everything. The bypass is explicit and role-name-driven.
///   2. Absent policy — <see cref="TablePolicy.None"/> (a table with no policy
///      metadata) imposes no restriction. This opt-in default mirrors the
///      tenant-filter and soft-delete modules: no metadata means no gating.
///   3. Otherwise the table's explicit allow-list / deny-lists apply.
///
/// Deny results carry only the generic <see cref="PolicyDecision.Deny"/> message,
/// which never names the table, column, or action — error output cannot be used
/// to probe the schema.
/// </summary>
public sealed class PolicyEvaluator
{
    private readonly string _adminRole;

    /// <summary>
    /// Creates an evaluator. <paramref name="adminRole"/> defaults to
    /// <see cref="MetadataKeys.Policy.DefaultAdminRole"/>; an identity holding
    /// this role bypasses all policy checks.
    /// </summary>
    public PolicyEvaluator(string? adminRole = null)
    {
        _adminRole = string.IsNullOrWhiteSpace(adminRole)
            ? MetadataKeys.Policy.DefaultAdminRole
            : adminRole.Trim();
    }

    /// <summary>
    /// Answers whether <paramref name="identity"/> may perform
    /// <paramref name="action"/> on a table governed by <paramref name="policy"/>.
    /// </summary>
    public PolicyDecision CanAct(TablePolicy policy, PolicyAction action, AppIdentity identity)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));
        if (identity is null) throw new ArgumentNullException(nameof(identity));

        if (IsAdmin(identity))
            return PolicyDecision.Allow;

        // Opt-in default: a table with no policy metadata is unrestricted.
        if (!policy.HasPolicy)
            return PolicyDecision.Allow;

        return policy.AllowedActions.Contains(action)
            ? PolicyDecision.Allow
            : PolicyDecision.Deny;
    }

    /// <summary>
    /// Answers whether <paramref name="identity"/> may access
    /// <paramref name="column"/> in the given <paramref name="direction"/> on a
    /// table governed by <paramref name="policy"/>.
    /// </summary>
    public PolicyDecision IsColumnAllowed(
        TablePolicy policy, string column, PolicyDirection direction, AppIdentity identity)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));
        if (identity is null) throw new ArgumentNullException(nameof(identity));
        if (string.IsNullOrWhiteSpace(column))
            throw new ArgumentException("Column name is required.", nameof(column));

        if (IsAdmin(identity))
            return PolicyDecision.Allow;

        // Opt-in default: a table with no policy metadata is unrestricted.
        if (!policy.HasPolicy)
            return PolicyDecision.Allow;

        var denyList = direction == PolicyDirection.Read
            ? policy.ReadDenyColumns
            : policy.WriteDenyColumns;

        if (!denyList.Contains(column))
            return PolicyDecision.Allow;

        // Role-qualified read deny: when the policy names the roles its
        // read-deny columns apply to, a caller holding none of them may still
        // read the column (e.g. finance_manager reads a finance field that is
        // hidden from officer/member). An unqualified deny — or any write
        // deny — blocks every non-admin caller.
        if (direction == PolicyDirection.Read && policy.ReadDenyRoles.Count > 0)
        {
            return identity.Roles.Any(policy.ReadDenyRoles.Contains)
                ? PolicyDecision.Deny
                : PolicyDecision.Allow;
        }

        return PolicyDecision.Deny;
    }

    private bool IsAdmin(AppIdentity identity) =>
        identity.Roles.Any(r => string.Equals(r, _adminRole, StringComparison.OrdinalIgnoreCase));
}
