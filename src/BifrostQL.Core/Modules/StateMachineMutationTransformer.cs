using System.Collections;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Enforces table-level state-machine metadata on insert and update mutations.
/// </summary>
public sealed class StateMachineMutationTransformer : IMutationTransformer, IModuleNamed
{
    private const string UserIdContextKey = MetadataKeys.Auth.DefaultUserIdContextKey;
    private const string RolesContextKey = MetadataKeys.Auth.DefaultRolesContextKey;
    private const string InvalidTransitionMessage = "State transition is not permitted.";

    private readonly PolicyEvaluator _evaluator;

    public StateMachineMutationTransformer(string? adminRole = null)
    {
        _evaluator = new PolicyEvaluator(adminRole);
    }

    public string ModuleName => "state-machine";

    // Security range, after policy action/column checks.
    public int Priority => 2;

    public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        return mutationType is MutationType.Insert or MutationType.Update
            && StateMachineConfigCollector.FromTable(table) is not null;
    }

    public ValueTask<MutationTransformResult> TransformAsync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
        => new(TransformSync(table, mutationType, data, context));

    private MutationTransformResult TransformSync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (context is null) throw new ArgumentNullException(nameof(context));

        var definition = StateMachineConfigCollector.FromTable(table);
        if (definition is null || !TryGetValue(data, definition.StateColumn, out var newStateValue))
            return Allowed(mutationType, data);

        var newState = newStateValue?.ToString();
        if (string.IsNullOrWhiteSpace(newState) || !definition.States.Contains(newState))
            return Denied(mutationType, data);

        if (mutationType == MutationType.Insert)
        {
            return string.Equals(newState.Trim(), definition.InitialState, StringComparison.OrdinalIgnoreCase)
                ? Allowed(mutationType, data)
                : Denied(mutationType, data);
        }

        if (!TryGetValue(context.CurrentRow, definition.StateColumn, out var currentStateValue))
            return Denied(mutationType, data);

        var currentState = currentStateValue?.ToString();
        if (string.IsNullOrWhiteSpace(currentState))
            return Denied(mutationType, data);

        if (string.Equals(currentState.Trim(), newState.Trim(), StringComparison.OrdinalIgnoreCase))
            return Allowed(mutationType, data);

        var transition = definition.Transitions.FirstOrDefault(t =>
            string.Equals(t.From, currentState.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.To, newState.Trim(), StringComparison.OrdinalIgnoreCase));

        if (transition is null)
            return Denied(mutationType, data);

        var identity = BuildIdentity(context);
        if (!CanUseTransition(transition, identity))
            return Denied(mutationType, data);

        return new MutationTransformResult
        {
            MutationType = mutationType,
            Data = data,
            StateTransition = new StateTransitionInfo(
                table.DbName,
                ResolveEntityId(table, data),
                currentState.Trim(),
                newState.Trim(),
                identity.Id,
                transition.OnEvent ?? "StateTransitioned"),
        };
    }

    private bool CanUseTransition(StateMachineTransition transition, AppIdentity identity)
    {
        if (transition.RequiredRoles.Count == 0)
            return true;

        var transitionPolicy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Update },
            rowScopeExpression: "state-machine-role-gate",
            rowScopeRoles: transition.RequiredRoles);

        if (_evaluator.CanAct(transitionPolicy, PolicyAction.Update, identity).Allowed &&
            (identity.Roles.Any(transition.RequiredRoles.Contains) || IsAdmin(identity)))
        {
            return true;
        }

        return false;
    }

    private bool IsAdmin(AppIdentity identity)
    {
        var denyAllPolicy = new TablePolicy(rowScopeExpression: "admin-probe");
        return _evaluator.CanAct(denyAllPolicy, PolicyAction.Update, identity).Allowed;
    }

    private static AppIdentity BuildIdentity(MutationTransformContext context)
    {
        var userContext = context.UserContext;
        var userId = userContext.TryGetValue(UserIdContextKey, out var idValue) && idValue is not null
            ? idValue.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(userId))
            userId = "anonymous";

        return new AppIdentity(userId, "mutation-context", roles: ExtractRoles(userContext));
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

    private static bool TryGetValue(
        IReadOnlyDictionary<string, object?>? data,
        string key,
        out object? value)
    {
        value = null;
        return data is not null && data.TryGetValue(key, out value);
    }

    private static MutationTransformResult Allowed(MutationType mutationType, Dictionary<string, object?> data) =>
        new()
        {
            MutationType = mutationType,
            Data = data,
        };

    private static MutationTransformResult Denied(MutationType mutationType, Dictionary<string, object?> data) =>
        new()
        {
            MutationType = mutationType,
            Data = data,
            Errors = new[] { InvalidTransitionMessage },
        };

    private static object? ResolveEntityId(IDbTable table, Dictionary<string, object?> data)
    {
        var key = table.KeyColumns?.FirstOrDefault();
        if (key is null)
            return null;

        if (data.TryGetValue(key.ColumnName, out var dbValue))
            return dbValue;

        return data.TryGetValue(key.GraphQlName, out var graphQlValue) ? graphQlValue : null;
    }
}
