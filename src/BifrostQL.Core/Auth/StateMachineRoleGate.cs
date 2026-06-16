using System.Collections;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Auth;

/// <summary>
/// Shared role-gating logic for state-machine transitions. Both the mutation
/// enforcement path (<c>StateMachineMutationTransformer</c>) and the read-only
/// <c>_availableTransitions</c> discovery field reuse this single rule so the
/// "can this caller use this transition" decision is derived in exactly one
/// place.
/// </summary>
public static class StateMachineRoleGate
{
    private const string UserIdContextKey = MetadataKeys.Auth.DefaultUserIdContextKey;
    private const string RolesContextKey = MetadataKeys.Auth.DefaultRolesContextKey;

    /// <summary>
    /// Builds the caller identity (user id + roles) from a request user context.
    /// </summary>
    public static AppIdentity BuildIdentity(IDictionary<string, object?> userContext)
    {
        if (userContext is null) throw new ArgumentNullException(nameof(userContext));

        var userId = userContext.TryGetValue(UserIdContextKey, out var idValue) && idValue is not null
            ? idValue.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(userId))
            userId = "anonymous";

        return new AppIdentity(userId, "state-machine-context", roles: ExtractRoles(userContext));
    }

    /// <summary>
    /// Answers whether <paramref name="identity"/> is permitted to use
    /// <paramref name="transition"/>. Transitions with no required roles are open
    /// to every caller; otherwise the caller must hold one of the required roles
    /// (or the configured admin role bypass via <paramref name="evaluator"/>).
    /// </summary>
    public static bool CanUseTransition(
        StateMachineTransition transition,
        AppIdentity identity,
        PolicyEvaluator evaluator)
    {
        if (transition is null) throw new ArgumentNullException(nameof(transition));
        if (identity is null) throw new ArgumentNullException(nameof(identity));
        if (evaluator is null) throw new ArgumentNullException(nameof(evaluator));

        if (transition.RequiredRoles.Count == 0)
            return true;

        var transitionPolicy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Update },
            rowScopeExpression: "state-machine-role-gate",
            rowScopeRoles: transition.RequiredRoles);

        return evaluator.CanAct(transitionPolicy, PolicyAction.Update, identity).Allowed &&
               (identity.Roles.Any(transition.RequiredRoles.Contains) || IsAdmin(identity, evaluator));
    }

    private static bool IsAdmin(AppIdentity identity, PolicyEvaluator evaluator)
    {
        var denyAllPolicy = new TablePolicy(rowScopeExpression: "admin-probe");
        return evaluator.CanAct(denyAllPolicy, PolicyAction.Update, identity).Allowed;
    }

    private static IReadOnlyList<string> ExtractRoles(IDictionary<string, object?> userContext)
    {
        if (!userContext.TryGetValue(RolesContextKey, out var rolesValue) || rolesValue is null)
            return Array.Empty<string>();

        if (rolesValue is string singleRole)
            return new[] { singleRole };

        if (rolesValue is IEnumerable<string> typedRoles)
            return typedRoles.ToArray();

        if (rolesValue is IEnumerable sequence)
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
