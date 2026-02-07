using BifrostQL.Core.Model;
using BifrostQL.Model;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// Validates metadata configuration rules against the live database schema.
/// </summary>
public sealed class ConfigValidateCommand : ICommand
{
    public string Name => "config-validate";
    public string Description => "Validate config rules against the database schema";

    public async Task<int> ExecuteAsync(ToolConfig config, OutputFormatter output)
    {
        var connectionString = config.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (output.IsJsonMode)
            {
                output.WriteJson(new { success = false, error = "Connection string is required. Use --connection-string." });
                return 1;
            }
            output.WriteError("Connection string is required. Use --connection-string.");
            return 1;
        }

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

        try
        {
            var configJson = await File.ReadAllTextAsync(configPath);
            var rules = ConfigFileReader.ReadMetadataRules(configJson);

            var metadataLoader = new MetadataLoader(Array.Empty<string>());
            var loader = new DbModelLoader(connectionString, metadataLoader);
            var model = await loader.LoadAsync();

            var validator = new ConfigValidator();
            var issues = validator.Validate(rules, model);

            var errors = issues.Where(i => i.Severity == ConfigIssueSeverity.Error).ToList();
            var warnings = issues.Where(i => i.Severity == ConfigIssueSeverity.Warning).ToList();

            if (output.IsJsonMode)
            {
                output.WriteJson(new
                {
                    success = errors.Count == 0,
                    ruleCount = rules.Count,
                    errorCount = errors.Count,
                    warningCount = warnings.Count,
                    issues = issues.Select(i => new { severity = i.Severity.ToString(), rule = i.Rule, message = i.Message }),
                });
                return errors.Count > 0 ? 1 : 0;
            }

            output.WriteHeader($"Validated {rules.Count} rules from {configPath}");

            if (errors.Count == 0 && warnings.Count == 0)
            {
                output.WriteSuccess("All rules valid.");
                return 0;
            }

            foreach (var error in errors)
            {
                output.WriteError($"  ERROR: {error.Message}");
                output.WriteInfo($"         Rule: {error.Rule}");
            }

            foreach (var warning in warnings)
            {
                output.WriteWarning($"  WARN:  {warning.Message}");
                output.WriteInfo($"         Rule: {warning.Rule}");
            }

            output.WriteInfo("");
            output.WriteInfo($"  {errors.Count} error(s), {warnings.Count} warning(s)");

            return errors.Count > 0 ? 1 : 0;
        }
        catch (SqlException ex)
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
}

/// <summary>
/// Reads metadata rules from a bifrostql.json config file.
/// </summary>
internal static class ConfigFileReader
{
    public static IReadOnlyList<string> ReadMetadataRules(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("bifrostQL", out var bifrostSection) &&
            !root.TryGetProperty("BifrostQL", out bifrostSection))
        {
            return Array.Empty<string>();
        }

        if (!bifrostSection.TryGetProperty("metadata", out var metadataArray) &&
            !bifrostSection.TryGetProperty("Metadata", out metadataArray))
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
}
