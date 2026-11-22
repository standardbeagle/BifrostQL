using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Model;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// Validates metadata configuration rules with the runtime's own parser and validator.
/// Every rule is parsed by <see cref="MetadataLoader"/>, exactly as the server parses it at
/// startup, and each rule that would fail startup is reported. Given a connection string, the
/// model is then loaded with those rules, which runs <c>ModelConfigValidator</c> against the live
/// schema. Without one, only the parse is checked.
/// </summary>
public sealed class ConfigValidateCommand : ICommand
{
    public string Name => "config-validate";
    public string Description => "Validate config rules (parse; against the database schema with --connection-string)";

    public async Task<int> ExecuteAsync(ToolConfig config, OutputFormatter output)
    {
        var configPath = config.ConfigPath ?? Path.Combine(Directory.GetCurrentDirectory(), "bifrostql.json");
        if (!File.Exists(configPath))
        {
            if (output.IsJsonMode)
            {
                output.WriteJson(new { success = false, error = $"Config file not found: {configPath}" });
                return 1;
            }
            output.WriteError($"Config file not found: {configPath}");
            output.WriteInfo("  Run 'bifrost init' to create a default config, or use --config <path>.");
            return 1;
        }

        var configJson = await File.ReadAllTextAsync(configPath);
        var rules = ConfigFileReader.ReadMetadataRules(configJson);
        var issues = ParseRules(rules);

        var modelValidated = false;
        if (issues.Count == 0 && !string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            try
            {
                await new DbModelLoader(config.ConnectionString, new MetadataLoader(rules)).LoadAsync();
                modelValidated = true;
            }
            catch (InvalidOperationException ex)
            {
                issues.Add(new ConfigIssue(null, ex.Message));
            }
            catch (DbException ex)
            {
                if (output.IsJsonMode)
                {
                    output.WriteJson(new { success = false, error = ex.Message });
                    return 1;
                }
                output.WriteError($"Validation failed: {ex.Message}");
                return 1;
            }
        }

        if (output.IsJsonMode)
        {
            output.WriteJson(new
            {
                success = issues.Count == 0,
                ruleCount = rules.Count,
                errorCount = issues.Count,
                modelValidated,
                issues = issues.Select(i => new { rule = i.Rule, message = i.Message }),
            });
            return issues.Count > 0 ? 1 : 0;
        }

        output.WriteHeader($"Validated {rules.Count} rules from {configPath}");

        foreach (var issue in issues)
        {
            output.WriteError($"  ERROR: {issue.Message}");
            if (issue.Rule != null)
                output.WriteInfo($"         Rule: {issue.Rule}");
        }

        if (issues.Count > 0)
        {
            output.WriteInfo("");
            output.WriteInfo($"  {issues.Count} error(s)");
            return 1;
        }

        output.WriteSuccess(modelValidated
            ? "All rules valid against the database schema."
            : "All rules parse. Pass --connection-string to validate them against the database schema.");
        return 0;
    }

    // Parses each rule on its own through the runtime parser, so every malformed rule is
    // reported, not only the first one the server would stop at.
    private static List<ConfigIssue> ParseRules(IReadOnlyList<string> rules)
    {
        var issues = new List<ConfigIssue>();
        foreach (var rule in rules)
        {
            try
            {
                _ = new MetadataLoader(new[] { rule });
            }
            catch (ArgumentException ex)
            {
                issues.Add(new ConfigIssue(rule, ex.Message));
            }
        }
        return issues;
    }

    private sealed record ConfigIssue(string? Rule, string Message);
}

/// <summary>
/// Reads metadata rules and configuration from a bifrostql.json config file.
/// </summary>
internal static class ConfigFileReader
{
    public static IReadOnlyList<string> ReadMetadataRules(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        var bifrostSection = GetBifrostSection(root);
        if (bifrostSection == null)
            return Array.Empty<string>();

        if (!bifrostSection.Value.TryGetProperty("metadata", out var metadataArray) &&
            !bifrostSection.Value.TryGetProperty("Metadata", out metadataArray))
        {
            return Array.Empty<string>();
        }

        if (metadataArray.ValueKind != System.Text.Json.JsonValueKind.Array)
            return Array.Empty<string>();

        var rules = new List<string>();
        foreach (var element in metadataArray.EnumerateArray())
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                rules.Add(value);
        }
        return rules;
    }

    public static string? ReadConnectionString(string json, string key = "bifrost")
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("connectionStrings", out var connSection) &&
            !root.TryGetProperty("ConnectionStrings", out connSection))
        {
            return null;
        }

        if (connSection.TryGetProperty(key, out var value))
            return value.GetString();

        return null;
    }

    public static BifrostConfigSection ReadBifrostSection(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        var section = GetBifrostSection(root);
        if (section == null)
            return new BifrostConfigSection();

        var result = new BifrostConfigSection();

        if (section.Value.TryGetProperty("path", out var path) ||
            section.Value.TryGetProperty("Path", out path))
            result.Path = path.GetString();

        if (section.Value.TryGetProperty("playground", out var playground) ||
            section.Value.TryGetProperty("Playground", out playground))
            result.Playground = playground.GetString();

        if (section.Value.TryGetProperty("disableAuth", out var disableAuth) ||
            section.Value.TryGetProperty("DisableAuth", out disableAuth))
            result.DisableAuth = disableAuth.GetBoolean();

        result.Metadata = ReadMetadataRules(json);

        return result;
    }

    private static System.Text.Json.JsonElement? GetBifrostSection(System.Text.Json.JsonElement root)
    {
        if (root.TryGetProperty("bifrostQL", out var section) ||
            root.TryGetProperty("BifrostQL", out section))
        {
            return section;
        }
        return null;
    }
}

internal sealed class BifrostConfigSection
{
    public string? Path { get; set; }
    public string? Playground { get; set; }
    public bool DisableAuth { get; set; } = true;
    public IReadOnlyList<string> Metadata { get; set; } = Array.Empty<string>();
    public string? ConfigFilePath { get; set; }
}
