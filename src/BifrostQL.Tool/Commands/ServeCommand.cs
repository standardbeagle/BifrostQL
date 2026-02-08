using BifrostQL.Core.Modules;
using BifrostQL.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// Starts a standalone Kestrel server with BifrostQL middleware.
/// </summary>
public sealed class ServeCommand : ICommand
{
    public string Name => "serve";
    public string Description => "Start a standalone BifrostQL GraphQL server";

    public async Task<int> ExecuteAsync(ToolConfig config, OutputFormatter output)
    {
        var resolved = ResolveConnectionString(config, output);
        if (resolved == null)
            return 1;

        var connectionString = resolved.Value.ConnectionString;
        var configSection = resolved.Value.ConfigSection;

        var port = config.Port;
        var url = $"http://0.0.0.0:{port}";
        var endpointPath = configSection?.Path ?? "/graphql";
        var playgroundPath = configSection?.Playground ?? "/graphiql";

        if (!output.IsJsonMode)
        {
            output.WriteHeader("Starting BifrostQL server...");
            output.WriteInfo($"  Endpoint:   {url}{endpointPath}");
            output.WriteInfo($"  Playground: {url}{playgroundPath}");
            output.WriteInfo("");
            output.WriteInfo("Press Ctrl+C to stop.");
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);

        if (configSection?.ConfigFilePath != null)
        {
            builder.Configuration.AddJsonFile(configSection.ConfigFilePath, optional: true);
        }

        var metadata = configSection?.Metadata ?? Array.Empty<string>();

        builder.Services.AddSingleton<BasicAuditModule>();
        builder.Services.AddBifrostQL(options =>
        {
            options
                .BindConnectionString(connectionString)
                .BindConfiguration(builder.Configuration.GetSection("BifrostQL"));

            options.AddModules(sp => new[] { sp.GetRequiredService<BasicAuditModule>() });
        });
        builder.Services.AddCors();

        var app = builder.Build();
        app.UseCors(x => x.AllowAnyMethod().AllowAnyHeader().AllowAnyOrigin());
        app.UseBifrostQL();

        await app.RunAsync();
        return 0;
    }

    private static ResolvedConnection? ResolveConnectionString(ToolConfig config, OutputFormatter output)
    {
        // Priority 1: Explicit --connection-string flag
        if (!string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            return new ResolvedConnection(config.ConnectionString, LoadConfigFile(config.ConfigPath));
        }

        // Priority 2: Positional args (server + database)
        if (config.CommandArgs.Length == 2)
        {
            var server = config.CommandArgs[0];
            var database = config.CommandArgs[1];

            if (!string.IsNullOrWhiteSpace(config.User))
            {
                var password = ReadPasswordSecurely($"Password for {config.User}@{server}: ");
                var connStr = $"Server={server};Database={database};User Id={config.User};Password={password};TrustServerCertificate=True";
                return new ResolvedConnection(connStr, LoadConfigFile(config.ConfigPath));
            }

            var trustedConnStr = $"Server={server};Database={database};Trusted_Connection=True;TrustServerCertificate=True";
            return new ResolvedConnection(trustedConnStr, LoadConfigFile(config.ConfigPath));
        }

        // Priority 2b: Single positional arg — disambiguate file vs error
        if (config.CommandArgs.Length == 1)
        {
            var arg = config.CommandArgs[0];
            if (File.Exists(arg))
            {
                return LoadFromConfigFile(arg, output);
            }

            WriteConnectionError(output, $"'{arg}' is not a recognized file.");
            return null;
        }

        // Priority 3: Config file (explicit --config or auto-discovered bifrostql.json)
        var configPath = config.ConfigPath ?? FindLocalConfigFile();
        if (configPath != null)
        {
            return LoadFromConfigFile(configPath, output);
        }

        WriteConnectionError(output);
        return null;
    }

    private static ResolvedConnection? LoadFromConfigFile(string path, OutputFormatter output)
    {
        if (!File.Exists(path))
        {
            if (output.IsJsonMode)
            {
                output.WriteJson(new { success = false, error = $"Config file not found: {path}" });
                return null;
            }
            output.WriteError($"Config file not found: {path}");
            return null;
        }

        var json = File.ReadAllText(path);
        var connStr = ConfigFileReader.ReadConnectionString(json);
        if (string.IsNullOrWhiteSpace(connStr))
        {
            if (output.IsJsonMode)
            {
                output.WriteJson(new { success = false, error = $"No connectionStrings.bifrost found in {path}" });
                return null;
            }
            output.WriteError($"No connectionStrings.bifrost found in {path}");
            return null;
        }

        var section = ConfigFileReader.ReadBifrostSection(json);
        section.ConfigFilePath = Path.GetFullPath(path);
        return new ResolvedConnection(connStr, section);
    }

    private static BifrostConfigSection? LoadConfigFile(string? configPath)
    {
        var path = configPath ?? FindLocalConfigFile();
        if (path == null || !File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        var section = ConfigFileReader.ReadBifrostSection(json);
        section.ConfigFilePath = Path.GetFullPath(path);
        return section;
    }

    private static string? FindLocalConfigFile()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), "bifrostql.json");
        return File.Exists(path) ? path : null;
    }

    private static void WriteConnectionError(OutputFormatter output, string? detail = null)
    {
        if (output.IsJsonMode)
        {
            output.WriteJson(new { success = false, error = "No connection configured." });
            return;
        }

        if (detail != null)
            output.WriteError(detail);

        output.WriteError("No connection configured. Use one of:");
        output.WriteInfo("  bifrost <server> <database>            Integrated auth");
        output.WriteInfo("  bifrost <server> <database> --user sa  SQL auth (prompts for password)");
        output.WriteInfo("  bifrost <config-file>                  Load from config file");
        output.WriteInfo("  bifrost serve --connection-string ...  Explicit connection string");
        output.WriteInfo("");
        output.WriteInfo("  Or create a bifrostql.json with 'bifrost init'");
    }

    private static string ReadPasswordSecurely(string prompt)
    {
        Console.Write(prompt);
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password.Length--;
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write('*');
            }
        }
        return password.ToString();
    }

    private readonly record struct ResolvedConnection(string ConnectionString, BifrostConfigSection? ConfigSection);
}
