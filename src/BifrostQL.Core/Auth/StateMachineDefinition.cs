namespace BifrostQL.Core.Auth;

/// <summary>
/// Pure-data state-machine definition parsed from table metadata.
/// </summary>
public sealed record StateMachineDefinition
{
    public string StateColumn { get; }
    public string InitialState { get; }
    public IReadOnlySet<string> States { get; }
    public IReadOnlySet<StateMachineTransition> Transitions { get; }

    public StateMachineDefinition(
        string stateColumn,
        string initialState,
        IEnumerable<string> states,
        IEnumerable<StateMachineTransition> transitions)
    {
        StateColumn = NormalizeRequired(stateColumn, nameof(stateColumn));
        InitialState = NormalizeRequired(initialState, nameof(initialState));
        States = new HashSet<string>(
            states.Select(s => NormalizeRequired(s, nameof(states))),
            StringComparer.OrdinalIgnoreCase);
        Transitions = new HashSet<StateMachineTransition>(transitions);

        if (States.Count == 0 || !States.Contains(InitialState) || Transitions.Count == 0)
            throw new ArgumentException("Invalid state-machine metadata.");
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Invalid state-machine metadata.", parameterName);

        return value.Trim();
    }
}

/// <summary>
/// Allowed transition between two states. Required roles and event name are
/// optional; enforcement and publication happen in later pipeline tasks.
/// </summary>
public sealed record StateMachineTransition
{
    public string From { get; }
    public string To { get; }
    public IReadOnlySet<string> RequiredRoles { get; }
    public string? OnEvent { get; }

    public StateMachineTransition(
        string from,
        string to,
        IEnumerable<string>? requiredRoles = null,
        string? onEvent = null)
    {
        From = NormalizeRequired(from, nameof(from));
        To = NormalizeRequired(to, nameof(to));
        RequiredRoles = new HashSet<string>(
            (requiredRoles ?? Enumerable.Empty<string>())
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Select(role => role.Trim()),
            StringComparer.OrdinalIgnoreCase);
        OnEvent = string.IsNullOrWhiteSpace(onEvent) ? null : onEvent.Trim();
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Invalid state-machine metadata.", parameterName);

        return value.Trim();
    }
}
