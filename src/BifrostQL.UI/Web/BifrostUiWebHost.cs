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
        /// <param name="expose">
        /// When <c>false</c> (default) the host binds <c>127.0.0.1</c> only — the
        /// desktop window and local processes can reach it, but the LAN cannot.
        /// This tool ships DB credentials, an SSH config, and a secrets vault with
        /// authentication disabled, so external exposure must be an explicit opt-in.
        /// When <c>true</c> the host binds <c>0.0.0.0</c> and the developer
        /// exception page is suppressed. CORS is same-origin only on both binds.
        /// </param>
        public static WebApplication Build(string? connectionString, int port, ConnectionState state, SshTunnelManager sshTunnel, bool expose = false)
        {
            var bindHost = expose ? "0.0.0.0" : "127.0.0.1";
            var serverUrl = $"http://{bindHost}:{port}";

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

            // CORS is same-origin ONLY, on both loopback and exposed bindings. The
            // SPA is served from this very host, so it never needs cross-origin
            // reads. This host ships DB credentials, an SSH config, and a secrets
            // vault with authentication disabled; any-origin CORS would let any web
            // page the user visits `fetch('http://localhost:<port>/api/vault/...')`
            // cross-origin and read the response. SetIsOriginAllowed(_ => false)
            // means no cross-origin Origin is ever approved.
            //
            // The developer exception page leaks stack traces and config, so it is
            // only enabled when bound to loopback (never on the LAN-reachable bind).
            if (!expose)
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseCors(x => x
                .AllowAnyMethod()
                .AllowAnyHeader()
                .SetIsOriginAllowed(_ => false));

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
