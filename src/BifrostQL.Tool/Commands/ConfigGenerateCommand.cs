using BifrostQL.Core.Model;
using BifrostQL.Model;
using Microsoft.Data.SqlClient;

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

        try
        {
            var metadataLoader = new MetadataLoader(Array.Empty<string>());
            var loader = new DbModelLoader(connectionString, metadataLoader);
            var model = await loader.LoadAsync();

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
            return 0;
        }
        catch (SqlException ex)
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
