using BifrostQL.Core.Auth;
using BifrostQL.Core.Modules.ComputedColumns;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Per-row computed-column provider backing the read-only
/// <c>_availableTransitions</c> field emitted on state-machine tables. For each
/// row it reads the current state-column value, asks the table's
/// <see cref="StateMachineDefinition"/> for outgoing transitions, filters them
/// to the set the caller's roles permit (reusing <see cref="StateMachineRoleGate"/>,
/// the same rule the mutation enforcement path uses), and returns the distinct
/// list of reachable target state names.
/// </summary>
public sealed class StateMachineTransitionsProvider : IComputedColumnProvider
{
    /// <summary>Provider name referenced by the synthesized computed column.</summary>
    public const string ProviderName = "state-machine-transitions";

    /// <summary>GraphQL field name emitted on state-machine tables.</summary>
    public const string FieldName = "_availableTransitions";

    /// <summary>GraphQL type of the emitted field: a list of reachable state names.</summary>
    public const string FieldType = "[String!]";

    private readonly PolicyEvaluator _evaluator;

    public StateMachineTransitionsProvider(string? adminRole = null)
    {
        _evaluator = new PolicyEvaluator(adminRole);
    }

    public string Name => ProviderName;

    public ValueTask<object?> ComputeAsync(ComputedColumnContext context, CancellationToken cancellationToken = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        var definition = StateMachineConfigCollector.FromTable(context.Table);
        if (definition is null)
            return new ValueTask<object?>((object?)null);

        var currentState = ReadCurrentState(context, definition.StateColumn);
        if (string.IsNullOrWhiteSpace(currentState))
            // Null (not an empty list) lets clients distinguish "row has no state" from
            // "no transitions permitted from the current state" (which is an empty list).
            return new ValueTask<object?>((object?)null);

        var identity = StateMachineRoleGate.BuildIdentity(context.UserContext);

        var targets = definition.Transitions
            .Where(t => string.Equals(t.From, currentState.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(t => StateMachineRoleGate.CanUseTransition(t, identity, _evaluator))
            .Select(t => t.To)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ValueTask<object?>(targets);
    }

    private static string? ReadCurrentState(ComputedColumnContext context, string stateColumn)
    {
        // The synthesized definition declares the state column (DB name) as its
        // only dependency, so the projected row is keyed by that name. Fall back
        // to the GraphQL name lookup defensively.
        if (context.Row.TryGetValue(stateColumn, out var value) && value is not null)
            return value.ToString();

        if (context.Table.ColumnLookup.TryGetValue(stateColumn, out var byDb)
            && context.Row.TryGetValue(byDb.GraphQlName, out var graphQlValue)
            && graphQlValue is not null)
            return graphQlValue.ToString();

        return null;
    }
}
