using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Storage;

namespace BifrostQL.Core.Modules.ComputedColumns;

public static class ComputedColumnConfigCollector
{
    public static IReadOnlyList<ComputedColumnDefinition> FromTable(IDbTable table)
    {
        var result = new List<ComputedColumnDefinition>();
        result.AddRange(ParseSql(table.GetMetadataValue(MetadataKeys.Computed.Sql)));
        result.AddRange(ParseProvider(table.GetMetadataValue(MetadataKeys.Computed.Provider)));
        result.AddRange(FileFolderComputedColumnCollector.FromTable(table));
        AddStateMachineTransitions(table, result);
        return result;
    }

    /// <summary>
    /// Emits the read-only <c>_availableTransitions</c> provider column on tables
    /// that carry state-machine metadata. Tables without it are unaffected.
    /// </summary>
    private static void AddStateMachineTransitions(IDbTable table, List<ComputedColumnDefinition> result)
    {
        var definition = StateMachineConfigCollector.FromTable(table);
        if (definition is null)
            return;

        result.Add(new ComputedColumnDefinition(
            StateMachineTransitionsProvider.FieldName,
            StateMachineTransitionsProvider.FieldType,
            ComputedColumnKind.Provider,
            StateMachineTransitionsProvider.ProviderName,
            new[] { definition.StateColumn }));
    }

    public static ComputedColumnDefinition? Find(IDbTable table, string graphQlName)
        => FromTable(table).FirstOrDefault(c => string.Equals(c.Name, graphQlName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<ComputedColumnDefinition> ParseSql(string? raw)
    {
        foreach (var entry in SplitEntries(raw))
        {
            var parts = entry.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length != 3)
                continue;

            var name = parts[0];
            var type = parts[1];
            var expression = parts[2];
            if (!ComputedColumnDefinition.IsValidGraphQlName(name) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(expression))
                continue;

            yield return new ComputedColumnDefinition(
                name,
                type,
                ComputedColumnKind.Sql,
                expression,
                ExtractPlaceholders(expression));
        }
    }

    private static IEnumerable<ComputedColumnDefinition> ParseProvider(string? raw)
    {
        foreach (var entry in SplitEntries(raw))
        {
            var parts = entry.Split(':', 4, StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
                continue;

            var name = parts[0];
            var type = parts[1];
            var provider = parts[2];
            if (!ComputedColumnDefinition.IsValidGraphQlName(name)
                || string.IsNullOrWhiteSpace(type)
                || string.IsNullOrWhiteSpace(provider))
                continue;

            var dependencies = parts.Length == 4 && parts[3].StartsWith("depends=", StringComparison.OrdinalIgnoreCase)
                ? SplitList(parts[3]["depends=".Length..])
                : Array.Empty<string>();

            yield return new ComputedColumnDefinition(
                name,
                type,
                ComputedColumnKind.Provider,
                provider,
                dependencies);
        }
    }

    private static IReadOnlyList<string> ExtractPlaceholders(string expression)
        => System.Text.RegularExpressions.Regex.Matches(expression, @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> SplitList(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> SplitEntries(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
