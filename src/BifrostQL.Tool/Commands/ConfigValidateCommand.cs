using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Model;
using Microsoft.Extensions.Configuration;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// Validates a config file with serve's own reader and the runtime's own parser and validator.
/// The file goes through every shape and kind check serve applies. Every rule is parsed by <see cref="MetadataLoader"/>, exactly as the server parses it at
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
            // Serve's own reader, so every shape and kind check serve applies to this file
            // (connection string, Path, Playground, DisableAuth, Metadata) refuses it here too.
            rules = ConfigFileReader.ReadServeConfig(configJson).Section.Metadata;
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
            // Only the validator's refusal is an error in the config file.
            catch (ModelConfigValidationException ex)
            {
                issues.Add(new ConfigIssue(null, ex.Message));
            }
            catch (DbException ex)
            {
                return WriteValidationFailure(output, ex.Message);
            }
            // Any other failed load (a provider with no registered factory, a pool timeout) is
            // the environment, not the file, so it is not counted against the file's rules. An
            // ArgumentException message is withheld: it can quote connection-string text (the
            // provider's own parse error is already dropped by OpenConnFactoryAsync).
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return WriteValidationFailure(output,
                    CliErrorText.MayQuoteConnectionString(ex) ? CliErrorText.UnhandledFailure : ex.Message);
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

    // The model could not be loaded for a reason outside the config file: one error, exit 1.
    private static int WriteValidationFailure(OutputFormatter output, string error)
    {
        if (output.IsJsonMode)
            output.WriteJson(new { success = false, error });
        else
            output.WriteError($"Validation failed: {error}");
        return 1;
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

    /// <summary>
    /// Everything <c>bifrost serve</c> reads from a config file, through every shape and kind
    /// check it applies. This is the reader's only entry: serve, config-validate and doctor all
    /// read through here, so each refuses exactly the files serve refuses. A missing connection
    /// string is not a shape fault: it comes back null for the caller to judge.
    /// </summary>
    public static (string? ConnectionString, BifrostConfigSection Section) ReadServeConfig(string json)
    {
        var file = ReadConfigFile(json);
        return (ReadConnectionString(file), ReadBifrostSection(file));
    }

    private static System.Text.Json.JsonDocument ParseJson(string json)
        => System.Text.Json.JsonDocument.Parse(json, ConfigurationJsonOptions);

    /// <summary>
    /// The connection string the server's <c>GetConnectionString(key)</c> reads from this file.
    /// Keys match case-insensitively, as on the server.
    /// </summary>
    private static string? ReadConnectionString(ConfigFile file, string key = "bifrost")
    {
        foreach (var kind in file.KindsAt("ConnectionStrings"))
        {
            if (kind is not (System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Null))
                throw new ConfigFileShapeException($"Config file connectionStrings section must be a JSON object, found {kind}.");
        }
        foreach (var kind in file.KindsAt("ConnectionStrings", key))
        {
            if (kind is not (System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Null))
                throw new ConfigFileShapeException($"Config file connectionStrings.{key} must be a string, found {kind}.");
        }
        return file.Configuration.GetConnectionString(key);
    }

    // The BifrostQL section's values, with the kind checks the configuration binder skips
    // (it reads a number or bool as its text). The metadata rules come from the runtime reader
    // MetadataLoader.ReadMetadataRules.
    private static BifrostConfigSection ReadBifrostSection(ConfigFile file)
    {
        var section = BifrostSection(file);
        if (section is null)
            return new BifrostConfigSection();

        // Configuration reads a number or bool as its text, so "Path": 5 would serve at "5".
        foreach (var name in new[] { "Path", "Playground" })
        {
            foreach (var kind in file.KindsAt("BifrostQL", name))
            {
                if (kind is not (System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Null))
                    throw new ConfigFileShapeException($"Config file BifrostQL:{name} must be a string, found {kind}.");
            }
        }

        bool disableAuth;
        try
        {
            disableAuth = section.GetValue("DisableAuth", true);
        }
        // The binder's message can quote the value; this one names the path only.
        catch (InvalidOperationException)
        {
            throw new ConfigFileShapeException("Config file BifrostQL:DisableAuth must be true or false.");
        }

        return new BifrostConfigSection
        {
            Path = section["Path"],
            Playground = section["Playground"],
            DisableAuth = disableAuth,
            Metadata = ReadMetadataRules(section),
        };
    }

    // The bound configuration every value is read from, and the JSON kind of every element by its
    // full configuration path, kept only to name the kind of a mis-shaped element (configuration
    // reads a number as its text).
    private sealed record ConfigFile(
        IConfigurationRoot Configuration, ILookup<string, System.Text.Json.JsonValueKind> Kinds)
    {
        /// <summary>
        /// The JSON kinds of every element whose configuration path is <paramref name="path"/>,
        /// compared case-insensitively. <c>"A:B": 1</c> and <c>"a": { "b": 1 }</c> are the same
        /// configuration key, so both are returned.
        /// </summary>
        public IEnumerable<System.Text.Json.JsonValueKind> KindsAt(params string[] path)
            => Kinds[ConfigurationPath.Combine(path)];
    }

    /// <summary>
    /// Binds the file the way the server's <c>AddJsonFile</c> does: keys compare
    /// case-insensitively, and keys that differ only in case are refused. Malformed JSON throws
    /// <see cref="System.Text.Json.JsonException"/>; a non-object root or a refused binding throws
    /// <see cref="ConfigFileShapeException"/>.
    /// </summary>
    private static ConfigFile ReadConfigFile(string json)
    {
        System.Text.Json.JsonElement root;
        using (var doc = ParseJson(json))
            root = doc.RootElement.Clone();
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new ConfigFileShapeException($"Config file root must be a JSON object, found {root.ValueKind}.");

        try
        {
            return new ConfigFile(
                new ConfigurationBuilder()
                    .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                    .Build(),
                FlattenKinds(root, null).ToLookup(e => e.Path, e => e.Kind, StringComparer.OrdinalIgnoreCase));
        }
        // The JSON parsed above, so this is a binding refusal such as a duplicate key. Its
        // message names the key, never a value.
        catch (FormatException ex)
        {
            throw new ConfigFileShapeException($"Config file cannot be bound as configuration: {ex.Message}");
        }
    }

    /// <summary>
    /// Every element below <paramref name="element"/> with its configuration path, built as
    /// <c>JsonConfigurationFileParser</c> builds keys: property names joined with ':' as written
    /// (a name containing ':' is several segments), array items by index.
    /// </summary>
    private static IEnumerable<(string Path, System.Text.Json.JsonValueKind Kind)> FlattenKinds(
        System.Text.Json.JsonElement element, string? path)
    {
        if (path is not null)
            yield return (path, element.ValueKind);

        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                foreach (var child in FlattenKinds(property.Value, path is null ? property.Name : ConfigurationPath.Combine(path, property.Name)))
                    yield return child;
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var name = (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                foreach (var child in FlattenKinds(item, path is null ? name : ConfigurationPath.Combine(path, name)))
                    yield return child;
            }
        }
    }

    // The BifrostQL section, or null when the file has none.
    private static IConfigurationSection? BifrostSection(ConfigFile file)
    {
        foreach (var kind in file.KindsAt("BifrostQL"))
        {
            if (kind is not (System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Null))
                throw new ConfigFileShapeException($"Config file BifrostQL section must be a JSON object, found {kind}.");
        }
        var section = file.Configuration.GetSection("BifrostQL");
        return section.Exists() ? section : null;
    }

    private static IReadOnlyList<string> ReadMetadataRules(IConfigurationSection? bifrostSection)
    {
        if (bifrostSection is null)
            return Array.Empty<string>();
        try
        {
            return MetadataLoader.ReadMetadataRules(bifrostSection.GetSection("Metadata"));
        }
        // The runtime reader's shape refusal; its message names the configuration path only.
        catch (InvalidOperationException ex)
        {
            throw new ConfigFileShapeException(ex.Message);
        }
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
