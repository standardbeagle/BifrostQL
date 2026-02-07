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

        var port = config.Port;
        var url = $"http://0.0.0.0:{port}";

        if (!output.IsJsonMode)
        {
            output.WriteHeader("Starting BifrostQL server...");
            output.WriteInfo($"  Endpoint:   {url}/graphql");
            output.WriteInfo($"  Playground: {url}/graphiql");
            output.WriteInfo("");
            output.WriteInfo("Press Ctrl+C to stop.");
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);

        builder.Services.AddSingleton<BasicAuditModule>();
        builder.Services.AddBifrostQL(options =>
        {
            options
                .BindConnectionString(connectionString)
                .BindConfiguration(builder.Configuration.GetSection("BifrostQL"))
                .AddModules(sp => new[] { sp.GetRequiredService<BasicAuditModule>() });
        });
        builder.Services.AddCors();

        var app = builder.Build();
        app.UseCors(x => x.AllowAnyMethod().AllowAnyHeader().AllowAnyOrigin());
        app.UseBifrostQL();

        await app.RunAsync();
        return 0;
    }
}
