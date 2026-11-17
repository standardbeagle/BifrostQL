using BifrostQL.Core.Modules;
using BifrostQL.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Configuration;
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
        var resolved = await ResolveConnectionString(config, output);
        if (resolved == null)
            return 1;

        var connectionString = resolved.Value.ConnectionString;
        var configSection = resolved.Value.ConfigSection;

        var port = config.Port;
        // Bind loopback by default. The serve command ships no authentication, so
        // exposing it beyond localhost is an explicit operator decision (--host),
        // announced with a warning — never an ambient 0.0.0.0 default.
        var host = config.Host;
        var url = $"http://{host}:{port}";
        var endpointPath = configSection?.Path ?? "/graphql";
        var playgroundPath = configSection?.Playground ?? "/graphiql";

        if (!IsLoopback(host))
            output.WriteWarning(
                $"bifrost serve is bound to '{host}', reachable beyond localhost. This server has no " +
                "authentication; anyone who can reach this interface can read and write the whole database. " +
                "Bind 127.0.0.1 (the default) unless this exposure is intended.");

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

        var app = BuildApp(builder, host, connectionString);
        await app.RunAsync();
        return 0;
    }

    /// <summary>
    /// Wires the serve pipeline onto <paramref name="builder"/>. The server has no
    /// authentication, so the browser is the attacker to keep out:
    /// <list type="bullet">
    /// <item>No CORS policy. The playground is served from this same origin, so a page on
    /// any other origin gets no <c>Access-Control-Allow-Origin</c> and cannot read a
    /// response or send a preflighted (JSON) request.</item>
    /// <item>On a loopback bind, the Host header must name a loopback host. That stops a DNS
    /// rebinding page (its own name resolved to 127.0.0.1) from reaching the API
    /// as same-origin.</item>
    /// </list>
    /// A widened <c>--host</c> bind has already warned the operator, and its reachable
    /// names are unknown, so host filtering is left to configuration there.
    /// </summary>
    internal static WebApplication BuildApp(WebApplicationBuilder builder, string host, string connectionString)
    {
        // serve wires no authentication scheme (no JwtSettings binding), so it states that
        // instead of inheriting the fail-secure "auth on" default: with auth on and no scheme
        // registered, UseAuthentication fails the pipeline build and the server never starts
        // unless the config file happened to carry DisableAuth: true.
        builder.Configuration.AddInMemoryCollection(
            [new KeyValuePair<string, string?>("BifrostQL:DisableAuth", "true")]);

        if (IsLoopback(host))
            builder.Services.Configure<HostFilteringOptions>(o => o.AllowedHosts = LoopbackHostNames.ToList());

        builder.Services.AddBifrostQL(options =>
        {
            options
                .BindConnectionString(connectionString)
                .BindConfiguration(builder.Configuration.GetSection("BifrostQL"));
        });

        var app = builder.Build();
        app.UseBifrostQL();
        return app;
    }

    /// <summary>Host header values a loopback bind answers to (ports are ignored by host filtering).</summary>
    internal static readonly string[] LoopbackHostNames = ["localhost", "127.0.0.1", "[::1]"];

    /// <summary>
    /// True when the bind host is a loopback address (localhost / 127.0.0.0-8 / ::1),
    /// so a non-loopback bind (LAN/public exposure) can be warned about.
    /// </summary>
    internal static bool IsLoopback(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return System.Net.IPAddress.TryParse(host, out var ip)
            && System.Net.IPAddress.IsLoopback(ip);
    }

    private static async Task<ResolvedConnection?> ResolveConnectionString(ToolConfig config, OutputFormatter output)
    {
        // Priority 1: Explicit --connection-string flag
        if (!string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            return new ResolvedConnection(config.ConnectionString, await LoadConfigFile(config.ConfigPath));
        }

        // Priority 2: Positional args (server + database)
        if (config.CommandArgs.Length == 2)
        {
            var server = config.CommandArgs[0];
            var database = config.CommandArgs[1];

            // A weakened TLS posture is stated at the moment it takes effect, rather than
            // being discoverable only by reading the connection string back.
            if (config.TrustServerCertificate)
                output.WriteWarning(SqlServerCertificateTrust.WaiverWarning(server));

            var password = string.IsNullOrWhiteSpace(config.User)
                ? null
                : ConsolePrompt.ReadPassword($"Password for {config.User}@{server}: ");

            var connStr = SqlServerCertificateTrust.Build(
                server, database, config.User, password, config.TrustServerCertificate);
            return new ResolvedConnection(connStr, await LoadConfigFile(config.ConfigPath));
        }

        // Priority 2b: Single positional arg — disambiguate file vs error
        if (config.CommandArgs.Length == 1)
        {
            var arg = config.CommandArgs[0];
            if (File.Exists(arg))
            {
                return await LoadFromConfigFile(arg, output);
            }

            WriteConnectionError(output, $"'{arg}' is not a recognized file.");
            return null;
        }

        // Priority 3: Config file (explicit --config or auto-discovered bifrostql.json)
        var configPath = config.ConfigPath ?? FindLocalConfigFile();
        if (configPath != null)
        {
            return await LoadFromConfigFile(configPath, output);
        }

        WriteConnectionError(output);
        return null;
    }

    private static async Task<ResolvedConnection?> LoadFromConfigFile(string path, OutputFormatter output)
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

        var json = await File.ReadAllTextAsync(path);
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

    private static async Task<BifrostConfigSection?> LoadConfigFile(string? configPath)
    {
        var path = configPath ?? FindLocalConfigFile();
        if (path == null || !File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path);
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
        output.WriteInfo($"  Add {SqlServerCertificateTrust.Flag} only for a server whose self-signed");
        output.WriteInfo("  certificate you already trust; it disables certificate validation.");
        output.WriteInfo("");
        output.WriteInfo("  Or create a bifrostql.json with 'bifrost init'");
    }

    private readonly record struct ResolvedConnection(string ConnectionString, BifrostConfigSection? ConfigSection);
}
