using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Model;
using Microsoft.Extensions.Configuration;

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
        IReadOnlyList<string> rules;
        try
        {
            rules = ConfigFileReader.ReadMetadataRules(configJson);
        }
        // JsonException: malformed JSON. ConfigFileShapeException: well-formed JSON of the
        // wrong shape. Neither message quotes the file's content.
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ConfigFileShapeException)
        {
            var error = ex is ConfigFileShapeException ? ex.Message : $"Config file is not valid JSON: {ex.Message}";
            if (output.IsJsonMode)
            {
                output.WriteJson(new { success = false, error });
                return 1;
            }
            output.WriteError(error);
            return 1;
        }
        var issues = ParseRules(rules);

        var modelValidated = false;
        string? modelNotValidatedReason = null;
        if (issues.Count > 0)
            modelNotValidatedReason = "model not validated: fix the rule parse errors first";
        else if (string.IsNullOrWhiteSpace(config.ConnectionString))
            modelNotValidatedReason = "model not validated: no --connection-string";
        else
        {
            try
            {
                var connFactory = await OpenConnFactoryAsync(config.ConnectionString);
                if (connFactory == null)
                    issues.Add(new ConfigIssue(null, CliErrorText.MalformedConnectionString));
                else
                {
                    await new DbModelLoader(connFactory, new MetadataLoader(rules)).LoadAsync();
                    modelValidated = true;
                }
            }
            // InvalidOperationException: ModelConfigValidator or an unregistered provider.
            // ArgumentException (and its ArgumentOutOfRangeException subtype): a failed model
            // lookup. The provider's connection-string parse error is not among them: its
            // message names the rejected keyword, which can be part of a password holding an
            // unquoted ';', so OpenConnFactoryAsync drops it before the model load starts.
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
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
                modelNotValidatedReason,
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
            if (modelNotValidatedReason != null)
                output.WriteInfo($"  {modelNotValidatedReason}");
            return 1;
        }

        output.WriteSuccess(modelValidated
            ? "All rules valid against the database schema."
            : "All rules parse. Pass --connection-string to validate them against the database schema.");
        return 0;
    }

    // Resolves the provider and opens one connection, so the whole connection string is parsed
    // (some factories parse it on construction, some providers only on open). Returns null when
    // that parse throws ArgumentException; its message is dropped because it can quote part of
    // a value (see CliErrorText).
    private static async Task<IDbConnFactory?> OpenConnFactoryAsync(string connectionString)
    {
        try
        {
            var connFactory = DbConnFactoryResolver.Create(connectionString);
            await using var connection = connFactory.GetConnection();
            await connection.OpenAsync();
            return connFactory;
        }
        catch (ArgumentException)
        {
            return null;
        }
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
    // The server reads this file through the JSON configuration provider (AddJsonFile), which
    // skips comments and allows trailing commas. The CLI parses it the same way, or a file the
    // server accepts is refused here as invalid JSON.
    private static readonly System.Text.Json.JsonDocumentOptions ConfigurationJsonOptions = new()
    {
        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parses a config file's text with the configuration provider's JSON grammar.</summary>
    public static System.Text.Json.JsonDocument ParseJson(string json)
        => System.Text.Json.JsonDocument.Parse(json, ConfigurationJsonOptions);

    /// <summary>
    /// Reads the metadata rules the server would read from this file: the JSON is bound into
    /// an <see cref="IConfiguration"/> the way the Host's <c>AddJsonFile</c> binds it, and the
    /// rules come from the runtime reader <see cref="MetadataLoader.ReadMetadataRules"/>.
    /// </summary>
    public static IReadOnlyList<string> ReadMetadataRules(string json)
    {
        using (var doc = ConfigFileReader.ParseJson(json))
            GetBifrostSection(doc.RootElement);

        IConfigurationRoot configuration;
        try
        {
            configuration = new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                .Build();
        }
        // The JSON parsed above, so this is a binding refusal such as a duplicate key. Its
        // message names the key, never a value.
        catch (FormatException ex)
        {
            throw new ConfigFileShapeException($"Config file cannot be bound as configuration: {ex.Message}");
        }

        try
        {
            return MetadataLoader.ReadMetadataRules(configuration.GetSection("BifrostQL:Metadata"));
        }
        // The runtime reader's shape refusal; its message names the configuration path only.
        catch (InvalidOperationException ex)
        {
            throw new ConfigFileShapeException(ex.Message);
        }
    }

    public static string? ReadConnectionString(string json, string key = "bifrost")
    {
        using var doc = ConfigFileReader.ParseJson(json);
        var root = doc.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new ConfigFileShapeException($"Config file root must be a JSON object, found {root.ValueKind}.");

        if (!root.TryGetProperty("connectionStrings", out var connSection) &&
            !root.TryGetProperty("ConnectionStrings", out connSection))
        {
            return null;
        }

        if (connSection.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new ConfigFileShapeException($"Config file connectionStrings section must be a JSON object, found {connSection.ValueKind}.");

        if (!connSection.TryGetProperty(key, out var value) || value.ValueKind == System.Text.Json.JsonValueKind.Null)
            return null;

        if (value.ValueKind != System.Text.Json.JsonValueKind.String)
            throw new ConfigFileShapeException($"Config file connectionStrings.{key} must be a string, found {value.ValueKind}.");

        return value.GetString();
    }

    public static BifrostConfigSection ReadBifrostSection(string json)
    {
        using var doc = ConfigFileReader.ParseJson(json);
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
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new ConfigFileShapeException($"Config file root must be a JSON object, found {root.ValueKind}.");
        if (root.TryGetProperty("bifrostQL", out var section) ||
            root.TryGetProperty("BifrostQL", out section))
        {
            if (section.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new ConfigFileShapeException($"Config file BifrostQL section must be a JSON object, found {section.ValueKind}.");
            return section;
        }
        return null;
    }
}

/// <summary>
/// A config file that is well-formed JSON but not the shape bifrostql.json requires. The
/// message names the offending element's position and JSON kind, never its content.
/// </summary>
internal sealed class ConfigFileShapeException(string message) : Exception(message);

internal sealed class BifrostConfigSection
{
    public string? Path { get; set; }
    public string? Playground { get; set; }
    public bool DisableAuth { get; set; } = true;
    public IReadOnlyList<string> Metadata { get; set; } = Array.Empty<string>();
    public string? ConfigFilePath { get; set; }
}
