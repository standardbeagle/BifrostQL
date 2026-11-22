namespace BifrostQL.Tool.Commands;

/// <summary>
/// The connection string a command runs against, and the config file it read. Every command
/// that takes <c>--connection-string</c> and <c>--config</c> resolves through here:
/// <c>--connection-string</c> wins, and the config file (<c>--config</c>, else
/// <c>bifrostql.json</c> in the working directory) supplies the connection string otherwise.
/// A file that exists passes <see cref="ConfigFileReader.ReadServeConfig"/> either way, so every
/// command refuses the files serve refuses. An explicit <c>--config</c> naming no file is refused.
/// </summary>
internal static class CommandConnection
{
    public const string NoConnectionConfigured =
        "Connection string is required. Use --connection-string, or --config with a file that sets connectionStrings.bifrost.";

    /// <summary>
    /// Resolves from <c>--connection-string</c> and the config file. On a refusal the reason is
    /// written to <paramref name="output"/> and null is returned.
    /// </summary>
    public static Task<ResolvedConnection?> ResolveAsync(ToolConfig config, OutputFormatter output)
        => RefusingConfigFileFaultsAsync(output, async () =>
        {
            if (!string.IsNullOrWhiteSpace(config.ConnectionString))
                return await WithConnectionStringAsync(config.ConnectionString, config.ConfigPath, output);

            var path = config.ConfigPath ?? FindLocalConfigFile();
            if (path != null)
                return await FromConfigFileAsync(path, output);

            WriteRefusal(output, NoConnectionConfigured);
            return null;
        });

    /// <summary>
    /// Runs <paramref name="resolve"/>, reporting a malformed or wrong-shaped config file as a
    /// refusal. JsonException: malformed JSON. ConfigFileShapeException: well-formed JSON of the
    /// wrong shape. Neither message quotes the file's content.
    /// </summary>
    internal static async Task<ResolvedConnection?> RefusingConfigFileFaultsAsync(
        OutputFormatter output, Func<Task<ResolvedConnection?>> resolve)
    {
        try
        {
            return await resolve();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ConfigFileShapeException)
        {
            WriteRefusal(output, ex is ConfigFileShapeException ? ex.Message : $"Config file is not valid JSON: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The connection string comes from the caller (flag or serve's positional args), but the
    /// config file is still read, so it passes every check config-validate applies,
    /// connectionStrings too.
    /// </summary>
    internal static async Task<ResolvedConnection?> WithConnectionStringAsync(
        string connectionString, string? configPath, OutputFormatter output)
    {
        if (configPath != null && !File.Exists(configPath))
        {
            WriteRefusal(output, ConfigFileNotFound(configPath));
            return null;
        }

        var path = configPath ?? FindLocalConfigFile();
        if (path == null)
            return new ResolvedConnection(connectionString, null);

        var section = ConfigFileReader.ReadServeConfig(await File.ReadAllTextAsync(path)).Section;
        section.ConfigFilePath = Path.GetFullPath(path);
        return new ResolvedConnection(connectionString, section);
    }

    /// <summary>The connection string and settings from the config file at <paramref name="path"/>.</summary>
    internal static async Task<ResolvedConnection?> FromConfigFileAsync(string path, OutputFormatter output)
    {
        if (!File.Exists(path))
        {
            WriteRefusal(output, ConfigFileNotFound(path));
            return null;
        }

        var (connectionString, section) = ConfigFileReader.ReadServeConfig(await File.ReadAllTextAsync(path));
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            WriteRefusal(output, CliErrorText.NoConnectionStringInConfigFile(path));
            return null;
        }

        section.ConfigFilePath = Path.GetFullPath(path);
        return new ResolvedConnection(connectionString, section);
    }

    internal static string? FindLocalConfigFile()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), "bifrostql.json");
        return File.Exists(path) ? path : null;
    }

    private static string ConfigFileNotFound(string path) => $"Config file not found: {path}";

    private static void WriteRefusal(OutputFormatter output, string error)
    {
        if (output.IsJsonMode)
            output.WriteJson(new { success = false, error });
        else
            output.WriteError(error);
    }
}

internal readonly record struct ResolvedConnection(string ConnectionString, BifrostConfigSection? ConfigSection);
