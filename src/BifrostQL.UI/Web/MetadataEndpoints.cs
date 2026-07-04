using System.Reflection;
using System.Runtime.InteropServices;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Server;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.UI.Web
{
    /// <summary>
    /// Read-only info endpoints: available providers, selectable API profiles,
    /// health, and diagnostics. None of these mutate <see cref="ConnectionState"/>.
    /// </summary>
    public static class MetadataEndpoints
    {
        public static void MapMetadataEndpoints(this WebApplication app, ConnectionState state)
        {
            // GET /api/providers - Returns available database providers
            app.MapGet("/api/providers", () =>
            {
                var registered = DbConnFactoryResolver.GetRegisteredProviders();
                // SQL Server is always available via the built-in fallback
                var providers = new HashSet<BifrostDbProvider>(registered) { BifrostDbProvider.SqlServer };
                var result = providers.OrderBy(p => p).Select(p => new
                {
                    id = p.ToString().ToLowerInvariant(),
                    name = p switch
                    {
                        BifrostDbProvider.SqlServer => "SQL Server",
                        BifrostDbProvider.PostgreSql => "PostgreSQL",
                        BifrostDbProvider.MySql => "MySQL",
                        BifrostDbProvider.Sqlite => "SQLite",
                        _ => p.ToString()
                    }
                });
                return Results.Ok(result);
            });

            // GET /api/profiles - Lists the selectable API shapes for the current
            // connection: a synthesized raw default first, then each registry profile.
            // Gated behind BifrostQL:ExposeProfiles so non-desktop hosts stay closed.
            app.MapGet("/api/profiles", (HttpContext http) =>
            {
                var config = http.RequestServices.GetRequiredService<IConfiguration>();
                if (!config.GetValue("BifrostQL:ExposeProfiles", false))
                    return Results.NotFound();

                var registry = http.RequestServices.GetService<BifrostProfileRegistry>();
                var result = new List<object>
                {
                    new { id = "default", label = "Database (raw)", serverProfile = (string?)null },
                };
                if (registry != null)
                {
                    foreach (var profile in registry.All)
                    {
                        result.Add(new
                        {
                            id = profile.Name,
                            label = profile.Label ?? profile.Name,
                            serverProfile = profile.Name,
                        });
                    }
                }
                return Results.Ok(result);
            });

            // Health check endpoint
            app.MapGet("/api/health", () => Results.Ok(new
            {
                status = "ok",
                connected = !string.IsNullOrEmpty(state.ConnectionString),
                provider = state.Provider?.ToString().ToLowerInvariant(),
                version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            }));

            // GET /api/diagnostics — versions + runtime info for the About page. The
            // host (this desktop shell) and server (BifrostQL GraphQL engine) are
            // versioned independently, so reporting both makes a drift obvious.
            app.MapGet("/api/diagnostics", () => Results.Ok(new
            {
                hostVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3),
                serverVersion = typeof(BifrostSetupOptions).Assembly.GetName().Version?.ToString(3),
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                connected = !string.IsNullOrEmpty(state.ConnectionString),
                provider = state.Provider?.ToString().ToLowerInvariant()
            }));
        }
    }
}
