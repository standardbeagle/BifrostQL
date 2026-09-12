using BifrostQL.Core.Model;

namespace BifrostQL.Core.Auth;

/// <summary>
/// Parses table-local state-machine metadata into a pure data definition.
/// Transition format: <c>from-&gt;to[role1,role2]@event</c>, separated by
/// semicolons or pipes. Roles and event are optional.
/// </summary>
public static class StateMachineConfigCollector
{
    private const string InvalidMetadataMessage = "Invalid state-machine metadata.";

    public static StateMachineDefinition? FromTable(IDbTable table)
    {
        if (table is null)
            throw new ArgumentNullException(nameof(table));

        var stateColumnRaw = table.GetMetadataValue(MetadataKeys.StateMachine.StateColumn);
        var initialStateRaw = table.GetMetadataValue(MetadataKeys.StateMachine.InitialState);
        var statesRaw = table.GetMetadataValue(MetadataKeys.StateMachine.States);
        var transitionsRaw = table.GetMetadataValue(MetadataKeys.StateMachine.Transitions);

        var hasAny =
            !string.IsNullOrWhiteSpace(stateColumnRaw) ||
            !string.IsNullOrWhiteSpace(initialStateRaw) ||
            !string.IsNullOrWhiteSpace(statesRaw) ||
            !string.IsNullOrWhiteSpace(transitionsRaw);

        if (!hasAny)
            return null;

        if (string.IsNullOrWhiteSpace(stateColumnRaw) ||
            string.IsNullOrWhiteSpace(initialStateRaw) ||
            string.IsNullOrWhiteSpace(statesRaw) ||
            string.IsNullOrWhiteSpace(transitionsRaw))
        {
            throw InvalidMetadata();
        }

        try
        {
            var states = SplitList(statesRaw).ToArray();
            var transitions = ParseTransitions(transitionsRaw, states).ToArray();
            return new StateMachineDefinition(
                stateColumnRaw!,
                initialStateRaw!,
                states,
                transitions);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw InvalidMetadata();
        }
    }

    private static IEnumerable<StateMachineTransition> ParseTransitions(string raw, IReadOnlyCollection<string> states)
    {
        var stateSet = new HashSet<string>(states, StringComparer.OrdinalIgnoreCase);

        foreach (var token in raw.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (transitionPart, onEvent) = SplitOptionalSuffix(token, '@');
            var (routePart, rolesPart) = BracketGrammar.SplitOptionalBracket(transitionPart, InvalidMetadata);
            var route = routePart.Split("->", StringSplitOptions.TrimEntries);

            if (route.Length != 2 ||
                string.IsNullOrWhiteSpace(route[0]) ||
                string.IsNullOrWhiteSpace(route[1]) ||
                !stateSet.Contains(route[0]) ||
                !stateSet.Contains(route[1]))
            {
                throw InvalidMetadata();
            }

            yield return new StateMachineTransition(
                route[0],
                route[1],
                SplitList(rolesPart),
                onEvent);
        }
    }

    private static (string Value, string? Suffix) SplitOptionalSuffix(string value, char marker)
    {
        var index = value.IndexOf(marker);
        if (index < 0)
            return (value, null);

        var suffix = value[(index + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(suffix) || value.IndexOf(marker, index + 1) >= 0)
            throw InvalidMetadata();

        return (value[..index].Trim(), suffix);
    }

    private static IEnumerable<string> SplitList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static InvalidOperationException InvalidMetadata() => new(InvalidMetadataMessage);
}
