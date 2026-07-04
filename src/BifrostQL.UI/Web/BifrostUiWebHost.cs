using System.Reflection;
using BifrostQL.Core.Modules;
using BifrostQL.Server;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.UI.Web
{
    /// <summary>
    /// Builds and configures the BifrostQL UI web host: Kestrel/logging/config,
    /// BifrostQL service registration, CORS, the full HTTP API surface, static
    /// files, the GraphQL endpoint, and the SPA fallback.
    ///
    /// The returned <see cref="WebApplication"/> is not started — the caller owns
    /// its lifetime (headless <c>RunAsync</c> vs. the desktop window shell).
    /// </summary>
    public static class BifrostUiWebHost
    {
        public static WebApplication Build(string? connectionString, int port, ConnectionState state, SshTunnelManager sshTunnel)
        {
            var serverUrl = $"http://0.0.0.0:{port}";

            // Set content root to the binary's directory so wwwroot is found
            // regardless of the current working directory.
            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = assemblyDir });

            builder.WebHost.UseUrls(serverUrl);

            // Configure Kestrel for larger headers (needed for some auth tokens)
            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                serverOptions.Limits.MaxRequestHeadersTotalSize = 131072;
            });

            // Configure logging to show detailed errors
            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Logging.AddConsole();
            builder.Logging.AddDebug();

            // Add in-memory configuration for BifrostQL
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BifrostQL:DisableAuth"] = "true",
                ["BifrostQL:Path"] = "/graphql",
                ["BifrostQL:Playground"] = "/graphiql",
                ["BifrostQL:ExposeProfiles"] = "true"
            });

            // Always register BifrostQL services — connection string may be set later via API
            builder.Services.AddBifrostQL(options =>
            {
                state.Options = options;
                options.BindConnectionString(connectionString)
                       .BindConfiguration(builder.Configuration.GetSection("BifrostQL"))
                       .AddFilterTransformers(new IFilterTransformer[]
                       {
                           new SoftDeleteFilterTransformer(),
                           new TenantFilterTransformer(),
                       });
            });

            builder.Services.AddCors();
            builder.Services.AddEndpointsApiExplorer();

            var app = builder.Build();

            app.UseDeveloperExceptionPage();
            app.UseCors(x => x
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowAnyOrigin());

            app.MapMetadataEndpoints(state);
            app.MapConnectionEndpoints(state);
            app.MapSshEndpoints(sshTunnel);
            app.MapVaultEndpoints(state, sshTunnel);

            // Serve static files from wwwroot
            app.UseDefaultFiles();
            app.UseStaticFiles();

            // BifrostQL GraphQL endpoint — always registered, connection set dynamically
            app.UseBifrostQL();

            // Fallback to index.html for SPA routing
            app.MapFallbackToFile("index.html");

            return app;
        }
    }
}
