using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.ComputedColumns;

public static class ComputedColumnConfigCollector
{
    public static IReadOnlyList<ComputedColumnDefinition> FromTable(IDbTable table)
    {
        var result = new List<ComputedColumnDefinition>();
        result.AddRange(ParseSql(table.GetMetadataValue(MetadataKeys.Computed.Sql)));
        result.AddRange(ParseProvider(table.GetMetadataValue(MetadataKeys.Computed.Provider)));
        return result;
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
