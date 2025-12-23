using System.CommandLine;
using BifrostQL.Server;
using Photino.NET;

var connectionStringArg = new Argument<string?>("connection")
{
    Description = "SQL Server connection string (e.g., 'Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True')"
};

var portOption = new Option<int>("--port", "-p")
{
    Description = "Port to run the server on",
    DefaultValueFactory = _ => 5000
};

var exposeOption = new Option<bool>("--expose", "-e")
{
    Description = "Expose the API to the network (binds to 0.0.0.0 instead of localhost)"
};

var headlessOption = new Option<bool>("--headless", "-H")
{
    Description = "Run in headless mode (server only, no UI window)"
};

var rootCommand = new RootCommand("BifrostQL UI - Desktop database explorer")
{
    connectionStringArg,
    portOption,
    exposeOption,
    headlessOption
};

rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var connectionString = parseResult.GetValue(connectionStringArg);
    var port = parseResult.GetValue(portOption);
    var expose = parseResult.GetValue(exposeOption);
    var headless = parseResult.GetValue(headlessOption);

    if (string.IsNullOrEmpty(connectionString))
    {
        Console.Error.WriteLine("Error: Connection string is required.");
        Console.Error.WriteLine("Usage: bifrostui <connection_string> [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Example: bifrostui 'Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True'");
        return 1;
    }

    var bindAddress = expose ? "0.0.0.0" : "localhost";
    var serverUrl = $"http://{bindAddress}:{port}";
    var localUrl = $"http://localhost:{port}";

    // Build and start the web server
    var builder = WebApplication.CreateBuilder();

    builder.WebHost.UseUrls(serverUrl);

    // Configure Kestrel for larger headers (needed for some auth tokens)
    builder.WebHost.ConfigureKestrel(serverOptions =>
    {
        serverOptions.Limits.MaxRequestHeadersTotalSize = 131072;
    });

    // Add in-memory configuration for BifrostQL
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["BifrostQL:DisableAuth"] = "true",
        ["BifrostQL:Path"] = "/graphql",
        ["BifrostQL:Playground"] = "/graphiql"
    });

    // Add BifrostQL services with the provided connection string
    builder.Services.AddBifrostQL(options =>
    {
        options.BindConnectionString(connectionString)
               .BindConfiguration(builder.Configuration.GetSection("BifrostQL"));
    });

    builder.Services.AddCors();

    var app = builder.Build();

    app.UseDeveloperExceptionPage();
    app.UseCors(x => x
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowAnyOrigin());

    // Serve static files from wwwroot
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // BifrostQL GraphQL endpoint
    app.UseBifrostQL();

    // Fallback to index.html for SPA routing
    app.MapFallbackToFile("index.html");

    // Start the server in the background
    var serverTask = app.RunAsync(cancellationToken);

    Console.WriteLine($"BifrostQL server started at {serverUrl}");
    Console.WriteLine($"GraphQL endpoint: {localUrl}/graphql");

    if (headless)
    {
        Console.WriteLine("Running in headless mode. Press Ctrl+C to stop.");
        await serverTask;
    }
    else
    {
        // Create the Photino window
        var window = new PhotinoWindow()
            .SetTitle("BifrostQL - Database Explorer")
            .SetSize(1400, 900)
            .Center()
            .SetDevToolsEnabled(true)
            .Load(localUrl);

        window.WaitForClose();

        // Shutdown the server when window closes
        await app.StopAsync();
    }

    return 0;
});

return await rootCommand.Parse(args).InvokeAsync();
