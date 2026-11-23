using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Model;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// Detects configuration patterns from the database schema and generates
/// metadata rules using ConfigPatternDetector and ConfigGenerator.
/// </summary>
public sealed class ConfigGenerateCommand : ICommand
{
    public string Name => "config-generate";
    public string Description => "Generate config rules from database schema patterns";

    public async Task<int> ExecuteAsync(ToolConfig config, OutputFormatter output)
    {
        // --connection-string wins; otherwise the config file supplies it. A config file that
        // exists is refused here when serve would refuse it.
        var resolved = await CommandConnection.ResolveAsync(config, output);
        if (resolved is null)
            return 1;

        try
        {
            // The database alone: the file's rules may name tables that do not exist yet, and a
            // table they hide is still a pattern to detect.
            var model = await ServeModel.BuildWithoutConfigRulesAsync(resolved.Value);

            var detector = new ConfigPatternDetector();
            var results = detector.Detect(model);

            var generator = new ConfigGenerator();
            var rules = generator.Generate(results);

            if (output.IsJsonMode)
            {
                output.WriteJson(new
                {
                    success = true,
                    tableCount = model.Tables.Count,
                    patternsDetected = results.Count,
                    rules,
                });
                return 0;
            }

            output.WriteSuccess($"Detected patterns in {results.Count} of {model.Tables.Count} tables");
            output.WriteInfo("");

            if (rules.Count == 0)
            {
                output.WriteInfo("  No patterns detected. Your database may use non-standard naming conventions.");
                output.WriteInfo("  See the BifrostQL documentation for manual configuration.");
                return 0;
            }

            output.WriteHeader("Generated rules:");
            foreach (var rule in rules)
            {
                output.WriteInfo($"  {rule}");
            }

            output.WriteInfo("");
            output.WriteInfo("Add these rules to the \"Metadata\" array in your bifrostql.json config.");
            if (rules.Contains(ConfigGenerator.UserAuditKeyRule))
                output.WriteWarning(
                    $"  Confirm '{ConfigGenerator.UserAuditKeyRule}': it names the user-context claim the " +
                    "created-by/updated-by/deleted-by columns are stamped from. 'id' is the caller id BifrostQL " +
                    "maps by default; change it if your host maps the writer's identity under another claim.");
            return 0;
        }
        catch (DbException ex)
        {
            if (output.IsJsonMode)
            {
                output.WriteJson(new { success = false, error = ex.Message });
                return 1;
            }
            output.WriteError($"Config generation failed: {ex.Message}");
            return 1;
        }
    }
}
