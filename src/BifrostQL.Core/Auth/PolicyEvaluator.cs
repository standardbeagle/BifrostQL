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
///      everything. The bypass is explicit and grant-driven.
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

    public PolicyDecision CanAct(TablePolicy policy, PolicyAction action, AppIdentity identity, string requiredRole)
    {
        var policyDecision = CanAct(policy, action, identity);
        if (!policyDecision.Allowed || string.IsNullOrWhiteSpace(requiredRole)) return PolicyDecision.Deny;
        return identity.Grants.Contains(requiredRole)
            ? PolicyDecision.Allow : PolicyDecision.Deny;
    }

    /// <summary>
    /// Answers whether <paramref name="identity"/> may access
    /// <paramref name="column"/> in the given <paramref name="direction"/> on a
    /// table governed by <paramref name="policy"/>.
    ///
    /// Write direction, in order:
    ///   1. <see cref="TablePolicy.WriteRequires"/> — a column gated by
    ///      <c>write-requires</c> is allowed when the caller holds ANY listed
    ///      grant, denied otherwise (an empty grant list denies every non-admin
    ///      caller). The check is presence-keyed: sending the column at all —
    ///      even with the currently stored value — is a write (E4).
    ///   2. <see cref="TablePolicy.WriteDenyColumns"/> — an unconditional deny
    ///      blocks every non-admin caller; a deny qualified by
    ///      <see cref="TablePolicy.WriteDenyRoles"/> blocks only callers holding
    ///      one of those roles.
    /// <paramref name="forDelete"/> exempts the write-requires gate only (E5):
    /// a delete carries no writable columns; action brackets gate the delete
    /// itself. The deny list still applies, preserving the pre-existing
    /// delete-data semantics.
    /// </summary>
    public PolicyDecision IsColumnAllowed(
        TablePolicy policy, string column, PolicyDirection direction, AppIdentity identity,
        bool forDelete = false)
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

        if (direction == PolicyDirection.Write)
        {
            if (!forDelete && policy.WriteRequires.TryGetValue(column, out var requiredGrants)
                && !identity.Grants.Any(requiredGrants.Contains))
            {
                return PolicyDecision.Deny;
            }

            // A held write-requires grant does NOT override the deny list:
            // spec order is requires (allow if any held) THEN deny.

            if (!policy.WriteDenyColumns.Contains(column))
                return PolicyDecision.Allow;

            // Role-qualified write deny, mirroring the read side: when the
            // policy names the roles its write-deny columns apply to, a caller
            // holding none of them may still write the column.
            if (policy.WriteDenyRoles.Count > 0)
            {
                return identity.Grants.Any(policy.WriteDenyRoles.Contains)
                    ? PolicyDecision.Deny
                    : PolicyDecision.Allow;
            }

            return PolicyDecision.Deny;
        }

        if (!policy.ReadDenyColumns.Contains(column))
            return PolicyDecision.Allow;

        // Role-qualified read deny: when the policy names the roles its
        // read-deny columns apply to, a caller holding none of them may still
        // read the column (e.g. finance_manager reads a finance field that is
        // hidden from officer/member). An unqualified deny blocks every
        // non-admin caller.
        if (policy.ReadDenyRoles.Count > 0)
        {
            return identity.Grants.Any(policy.ReadDenyRoles.Contains)
                ? PolicyDecision.Deny
                : PolicyDecision.Allow;
        }

        return PolicyDecision.Deny;
    }

    /// <summary>
    /// Answers how a READ of <paramref name="column"/> resolves for
    /// <paramref name="identity"/>: <see cref="ReadColumnDisposition.Allow"/>,
    /// <see cref="ReadColumnDisposition.Mask"/> (the selection succeeds with the
    /// value nulled), or <see cref="ReadColumnDisposition.Refuse"/> (the query
    /// is rejected). Read denial has two sources:
    ///   1. <see cref="TablePolicy.ReadRequires"/> — a column gated by
    ///      <c>read-requires</c> is denied when the caller holds none of the
    ///      listed grants (an empty grant list denies every non-admin caller).
    ///   2. <see cref="TablePolicy.ReadDenyColumns"/> — an unconditional deny
    ///      blocks every non-admin caller; a deny qualified by
    ///      <see cref="TablePolicy.ReadDenyRoles"/> blocks only callers holding
    ///      one of those roles.
    /// The deny MODE resolves column <c>deny-mode</c> first, then the table
    /// <c>deny-mode</c>, then the source default: <c>refuse</c> for the deny
    /// list (shipped behaviour), <c>null</c> (mask) for <c>read-requires</c>.
    /// </summary>
    public ReadColumnDisposition GetReadDisposition(
        TablePolicy policy, string column, AppIdentity identity)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));
        if (identity is null) throw new ArgumentNullException(nameof(identity));
        if (string.IsNullOrWhiteSpace(column))
            throw new ArgumentException("Column name is required.", nameof(column));

        if (IsAdmin(identity))
            return ReadColumnDisposition.Allow;

        // Opt-in default: a table with no policy metadata is unrestricted.
        if (!policy.HasPolicy)
            return ReadColumnDisposition.Allow;

        bool denied;
        var defaultRefuse = false;
        if (policy.ReadRequires.ContainsKey(column))
        {
            var requiredGrants = policy.ReadRequires[column];
            denied = !identity.Grants.Any(requiredGrants.Contains);
        }
        else if (policy.ReadDenyColumns.Contains(column))
        {
            defaultRefuse = true;
            // Role-qualified read deny: when the policy names the roles its
            // read-deny columns apply to, a caller holding none of them may
            // still read the column.
            denied = policy.ReadDenyRoles.Count == 0
                || identity.Grants.Any(policy.ReadDenyRoles.Contains);
        }
        else
        {
            return ReadColumnDisposition.Allow;
        }

        if (!denied)
            return ReadColumnDisposition.Allow;

        var mode = policy.ColumnDenyModes.TryGetValue(column, out var columnMode)
            ? columnMode
            : policy.TableDenyMode;
        var refuse = mode is null
            ? defaultRefuse
            : string.Equals(mode, "refuse", StringComparison.OrdinalIgnoreCase);
        return refuse ? ReadColumnDisposition.Refuse : ReadColumnDisposition.Mask;
    }

    private bool IsAdmin(AppIdentity identity) =>
        identity.Grants.Contains(_adminRole);
}
