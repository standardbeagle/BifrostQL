using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;

namespace BifrostQL.Core.Storage;

public static class FileFolderComputedColumnCollector
{
    public const string LocalProviderName = "file-folder-local";
    public const string S3ProviderName = "file-folder-s3";

    public static IReadOnlyList<ComputedColumnDefinition> FromTable(IDbTable table)
    {
        var raw = table.GetMetadataValue(MetadataKeys.FileStorage.Folder);
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<ComputedColumnDefinition>();

        var result = new List<ComputedColumnDefinition>();
        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':', 4, StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
                continue;

            var fieldName = parts[0];
            var graphQlType = parts.Length == 3 ? "JSON" : parts[1];
            var providerType = parts.Length == 3 ? parts[1] : parts[2];
            var optionsRaw = parts.Length == 3 ? parts[2] : parts[3];

            if (!ComputedColumnDefinition.IsValidGraphQlName(fieldName))
                continue;

            var options = ParseOptions(optionsRaw);
            var dependencies = options.TryGetValue("depends", out var depends)
                ? depends.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : Array.Empty<string>();

            result.Add(new ComputedColumnDefinition(
                fieldName,
                string.IsNullOrWhiteSpace(graphQlType) ? "JSON" : graphQlType,
                ComputedColumnKind.Provider,
                NormalizeProvider(providerType),
                dependencies,
                options));
        }

        return result;
    }

    private static string NormalizeProvider(string provider)
        => provider.Trim().ToLowerInvariant() switch
        {
            "local" => LocalProviderName,
            "s3" => S3ProviderName,
            var p => p,
        };

    private static IReadOnlyDictionary<string, string> ParseOptions(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && !string.IsNullOrWhiteSpace(kv[0]))
            {
                result[kv[0]] = kv[1];
                currentKey = kv[0];
                continue;
            }

            if (currentKey != null && result.TryGetValue(currentKey, out var existing))
                result[currentKey] = existing + "," + part;
        }

        return result;
    }
}
