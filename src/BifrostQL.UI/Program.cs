using System.CommandLine;
using BifrostQL.Aws;
using BifrostQL.Core.Model;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using BifrostQL.UI;
using BifrostQL.UI.Vault;
using BifrostQL.UI.Web;

// Register all dialect factories so DbConnFactoryResolver can route by provider
DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.PostgreSql, cs => new PostgresDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.MySql, cs => new MySqlDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));

// Register the AWS S3 storage provider so "s3" file-storage columns resolve.
AwsStorageRegistration.Register();

var connectionStringArg = new Argument<string?>("connection")
{
    Description = "Database connection string. Optional - can be set via UI.",
    Arity = ArgumentArity.ZeroOrOne
};

var portOption = new Option<int>("--port", "-p")
{
    Description = "Port to run the server on",
    DefaultValueFactory = _ => 5000
};

var headlessOption = new Option<bool>("--headless", "-H")
{
    Description = "Run in headless mode (server only, no UI window)"
};

var vaultPathOption = new Option<string?>("--vault", "-V")
{
    Description = "Path to encrypted vault file (default: ~/.config/bifrost/vault.json.enc)"
};

var exposeOption = new Option<bool>("--expose")
{
    Description = "Bind 0.0.0.0 to expose the server to the LAN. Off by default: " +
                  "the server binds 127.0.0.1 only. Authentication is disabled, so " +
                  "only enable this on a trusted network."
};

var rootCommand = new RootCommand("BifrostQL UI - Desktop database explorer")
{
    connectionStringArg,
    portOption,
    headlessOption,
    vaultPathOption,
    exposeOption
};

// Vault CLI subcommands (vault add/list/remove/export)
rootCommand.Add(VaultCommands.CreateVaultCommand(vaultPathOption));

// Shared connection state — the web endpoints and native bridge handlers capture
// this single instance by reference. Endpoints mutate it when a connection is
// activated; bridge handlers read it to run in-process SQL / schema queries.
var state = new ConnectionState();
var sshTunnel = new SshTunnelManager();

rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var connectionString = parseResult.GetValue(connectionStringArg);
    var port = parseResult.GetValue(portOption);
    var headless = parseResult.GetValue(headlessOption);
    var expose = parseResult.GetValue(exposeOption);
    state.VaultPath = parseResult.GetValue(vaultPathOption);

    state.ConnectionString = connectionString;
    if (connectionString != null)
        state.Provider = DbConnFactoryResolver.DetectProvider(connectionString);

    var localUrl = $"http://localhost:{port}";
    var serverUrl = expose ? $"http://0.0.0.0:{port}" : localUrl;

    var app = BifrostUiWebHost.Build(connectionString, port, state, sshTunnel, expose);

    // Start the server in the background
    var serverTask = app.RunAsync(cancellationToken);

    Console.WriteLine($"BifrostQL server started at {serverUrl}");
    if (expose)
        Console.WriteLine("WARNING: --expose binds 0.0.0.0 with authentication disabled. " +
                          "The GraphQL, connection, SSH, and vault APIs are reachable by any host on the LAN.");
    if (!string.IsNullOrEmpty(connectionString))
    {
        Console.WriteLine($"GraphQL endpoint: {localUrl}/graphql");
    }
    else
    {
        Console.WriteLine("No connection string provided - use the UI to connect to a database");
    }

    if (headless)
    {
        Console.WriteLine("Running in headless mode. Press Ctrl+C to stop.");
        await serverTask;
        await sshTunnel.DisposeAsync();
    }
    else
    {
        await DesktopShell.RunAsync(app, localUrl, state, sshTunnel);
    }

    return 0;
});

return await rootCommand.Parse(args).InvokeAsync();
