using System.CommandLine;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Utils;
using BifrostQL.Server;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using BifrostQL.UI.Vault;
using Photino.NET;

// Register all dialect factories so DbConnFactoryResolver can route by provider
DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.PostgreSql, cs => new PostgresDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.MySql, cs => new MySqlDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));

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

var rootCommand = new RootCommand("BifrostQL UI - Desktop database explorer")
{
    connectionStringArg,
    portOption,
    headlessOption,
    vaultPathOption
};

// Vault CLI subcommands (vault add/list/remove/export)
rootCommand.Add(VaultCommands.CreateVaultCommand(vaultPathOption));

// Shared connection state — BifrostSetupOptions captures this by reference via the lambda closure
string? currentConnectionString = null;
BifrostDbProvider? currentProvider = null;
BifrostSetupOptions? bifrostOptions = null;
string? activeVaultPath = null;
var sshTunnel = new BifrostQL.UI.SshTunnelManager();

rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var connectionString = parseResult.GetValue(connectionStringArg);
    var port = parseResult.GetValue(portOption);
    var headless = parseResult.GetValue(headlessOption);
    activeVaultPath = parseResult.GetValue(vaultPathOption);

    currentConnectionString = connectionString;
    if (connectionString != null)
        currentProvider = DbConnFactoryResolver.DetectProvider(connectionString);

    var serverUrl = $"http://0.0.0.0:{port}";
    var localUrl = $"http://localhost:{port}";

    // Build and start the web server - set content root to the binary's directory
    // so wwwroot is found regardless of the current working directory
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
        ["BifrostQL:Playground"] = "/graphiql"
    });

    // Always register BifrostQL services — connection string may be set later via API
    builder.Services.AddBifrostQL(options =>
    {
        bifrostOptions = options;
        options.BindConnectionString(connectionString)
               .BindConfiguration(builder.Configuration.GetSection("BifrostQL"));
    });

    builder.Services.AddCors();
    builder.Services.AddEndpointsApiExplorer();

    var app = builder.Build();

    app.UseDeveloperExceptionPage();
    app.UseCors(x => x
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowAnyOrigin());

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

    // API endpoint to test a connection string
    app.MapPost("/api/connection/test", async (ConnectionTestRequest request, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return Results.BadRequest(new { error = "Connection string is required" });
        }

        try
        {
            var provider = request.Provider != null
                ? DbConnFactoryResolver.ParseProviderName(request.Provider)
                : DbConnFactoryResolver.DetectProvider(request.ConnectionString);
            var factory = DbConnFactoryResolver.Create(request.ConnectionString, provider);
            await using var conn = factory.GetConnection();
            await conn.OpenAsync(ct);

            return Results.Ok(new {
                success = true,
                message = $"Successfully connected to {conn.Database} via {provider}",
                database = conn.Database,
                provider = provider.ToString().ToLowerInvariant()
            });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new {
                success = false,
                error = ex.Message
            });
        }
    });

    // POST /api/databases - Lists available databases on the server
    app.MapPost("/api/databases", async (ListDatabasesRequest request, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
            return Results.BadRequest(new { error = "Connection string is required", databases = Array.Empty<string>() });

        try
        {
            var provider = request.Provider != null
                ? DbConnFactoryResolver.ParseProviderName(request.Provider)
                : DbConnFactoryResolver.DetectProvider(request.ConnectionString);

            if (provider == BifrostDbProvider.Sqlite)
                return Results.Ok(new { databases = Array.Empty<string>() });

            // Peer/ident auth for PostgreSQL — shell out to psql since the .NET process
            // may not be running as the correct OS user for peer authentication
            if (request.PeerAuth && provider == BifrostDbProvider.PostgreSql)
            {
                var databases = await ListDatabasesViaPsqlAsync(request.ConnectionString, request.PsqlUser, ct);
                return Results.Ok(new { databases });
            }

            var factory = DbConnFactoryResolver.Create(request.ConnectionString, provider);
            var databases2 = await factory.ListDatabasesAsync(ct);
            return Results.Ok(new { databases = databases2 });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message, databases = Array.Empty<string>() });
        }
    });

    // Vault credential hardening (task XGSUbdBiIzla): the previous
    // /api/connection/set endpoint used to accept a raw connection string
    // — including the database password — via HTTP POST. That created a
    // path where browser memory and any sniffed localhost traffic held
    // plaintext credentials. It has been deleted; all connection changes
    // now flow through one of:
    //   - POST /api/vault/connect (activate a saved vault entry), or
    //   - the native bridge "request-credential" handler (opens the
    //     isolated Photino child window for password entry and writes
    //     the vault entry server-side — see the handler registration
    //     in the window bootstrap block below).
    // Quickstart-created SQLite databases now self-bind on the server
    // side at the end of /api/database/create-quickstart so they don't
    // need the deleted endpoint either.

    // API endpoint to create a test database (SQL Server only - legacy)
    app.MapPost("/api/database/create", async (CreateDatabaseRequest request, CancellationToken ct) =>
    {
        async IAsyncEnumerable<string> StreamProgress()
        {
            yield return SseEvent("Parsing connection string", 5, "Extracting server details");

            var connBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(request.ConnectionString ??
                "Server=localhost;Database=master;User Id=sa;Password=your_password;TrustServerCertificate=True");
            var originalDatabase = connBuilder.InitialCatalog;
            connBuilder.InitialCatalog = "master";

            yield return SseEvent("Connecting to server", 10, "Establishing connection to master database");

            Microsoft.Data.SqlClient.SqlConnection? conn = null;
            Exception? connectError = null;

            try
            {
                conn = new Microsoft.Data.SqlClient.SqlConnection(connBuilder.ConnectionString);
                await conn.OpenAsync(ct);
            }
            catch (Exception ex)
            {
                connectError = ex;
            }

            if (connectError != null)
            {
                yield return SseEvent("Error", 0, $"Failed to connect to SQL Server: {connectError.Message}", error: true);
                yield break;
            }

            await using var _conn = conn!;

            var dbName = request.Template switch
            {
                "northwind" => "Northwind_Test",
                "adventureworks-lite" => "AdventureWorksLite_Test",
                "simple-blog" => "SimpleBlog_Test",
                _ => "TestDB_" + Guid.NewGuid().ToString("N")[..8]
            };

            yield return SseEvent("Creating database", 20, $"Creating database {dbName}");

            await using (var cmd = new Microsoft.Data.SqlClient.SqlCommand($"IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = '{dbName}') BEGIN CREATE DATABASE [{dbName}] END", _conn))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }

            yield return SseEvent("Creating tables", 30, "Setting up database schema");

            connBuilder.InitialCatalog = dbName;
            await using var newConn = new Microsoft.Data.SqlClient.SqlConnection(connBuilder.ConnectionString);
            await newConn.OpenAsync(ct);

            var sql = request.Template switch
            {
                "northwind" => TestDatabaseSchemas.GetNorthwindSchema(),
                "adventureworks-lite" => TestDatabaseSchemas.GetAdventureWorksLiteSchema(),
                "simple-blog" => TestDatabaseSchemas.GetSimpleBlogSchema(),
                _ => TestDatabaseSchemas.GetSimpleBlogSchema()
            };

            var statements = sql.Split(new[] { "GO" }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < statements.Length; i++)
            {
                var percent = 40 + (i * 50 / statements.Length);
                yield return SseEvent("Creating schema", percent, $"Executing statement {i + 1} of {statements.Length}");

                await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(statements[i].Trim(), newConn);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            yield return SseEvent("Inserting sample data", 90, "Adding sample records");

            var dataSql = request.Template switch
            {
                "northwind" => TestDatabaseSchemas.GetNorthwindData(),
                "adventureworks-lite" => TestDatabaseSchemas.GetAdventureWorksLiteData(),
                "simple-blog" => TestDatabaseSchemas.GetSimpleBlogData(),
                _ => TestDatabaseSchemas.GetSimpleBlogData()
            };

            var dataStatements = dataSql.Split(new[] { "GO" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < dataStatements.Length; i++)
            {
                await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(dataStatements[i].Trim(), newConn);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var newConnectionString = connBuilder.ConnectionString;

            yield return SseEvent("Complete!", 100, "Database created successfully", connectionString: newConnectionString);
        }

        return WriteSseStream(StreamProgress());
    });

    // POST /api/database/create-quickstart - Creates a SQLite quickstart database
    app.MapPost("/api/database/create-quickstart", async (QuickstartRequest request, CancellationToken ct) =>
    {
        var validSchemas = new[] { "blog", "ecommerce", "crm", "classroom", "project-tracker", "sqlite-advanced" };
        if (string.IsNullOrWhiteSpace(request.Schema) || !validSchemas.Contains(request.Schema))
        {
            return Results.BadRequest(new { error = $"Invalid schema. Must be one of: {string.Join(", ", validSchemas)}" });
        }

        var validSizes = new[] { "sample", "full" };
        var dataSize = string.IsNullOrWhiteSpace(request.DataSize) ? "sample" : request.DataSize;
        if (!validSizes.Contains(dataSize))
        {
            return Results.BadRequest(new { error = $"Invalid dataSize. Must be one of: {string.Join(", ", validSizes)}" });
        }

        async IAsyncEnumerable<string> StreamProgress()
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var fileName = $"bifrost-{request.Schema}-{timestamp}.db";
            var dbPath = Path.Combine(Path.GetTempPath(), fileName);
            var sqliteConnectionString = $"Data Source={dbPath}";

            yield return SseEvent("Creating database", 10, $"Creating SQLite database: {fileName}");

            var factory = DbConnFactoryResolver.Create(sqliteConnectionString, BifrostDbProvider.Sqlite);

            yield return SseEvent("Loading schema", 20, $"Reading {request.Schema} schema definition");

            var ddlSql = QuickstartSchemas.LoadSchemaSql(request.Schema);
            if (ddlSql == null)
            {
                yield return SseEvent("Error", 0, $"Schema '{request.Schema}' not found in embedded resources", error: true);
                yield break;
            }

            yield return SseEvent("Creating tables", 30, "Executing DDL statements");

            // Execute DDL - yields not allowed in try-catch, so run all statements then report
            var ddlStatements = ddlSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

            Exception? ddlError = null;
            try
            {
                await QuickstartSchemas.ExecuteStatementsAsync(factory, ddlStatements, ct);
            }
            catch (Exception ex)
            {
                ddlError = ex;
            }

            if (ddlError != null)
            {
                yield return SseEvent("Error", 0, $"Failed to create schema: {ddlError.Message}", error: true);
                yield break;
            }

            yield return SseEvent("Schema created", 70, $"Created {ddlStatements.Length} tables");

            yield return SseEvent("Loading seed data", 75, $"Loading {dataSize} dataset");

            var seedSql = QuickstartSchemas.LoadSeedSql(request.Schema, dataSize);

            if (seedSql != null)
            {
                var seedStatements = seedSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

                Exception? seedError = null;
                try
                {
                    await QuickstartSchemas.ExecuteStatementsAsync(factory, seedStatements, ct);
                }
                catch (Exception ex)
                {
                    seedError = ex;
                }

                if (seedError != null)
                {
                    yield return SseEvent("Error", 0, $"Failed to insert seed data: {seedError.Message}", error: true);
                    yield break;
                }

                yield return SseEvent("Data loaded", 95, $"Inserted {seedStatements.Length} data batches");
            }
            else
            {
                yield return SseEvent("Seed data", 90, "No seed data available for this schema/size combination (DDL only)");
            }

            // Self-bind the freshly created SQLite database on the server
            // side. Previously the client would POST the returned
            // connection string back to /api/connection/set to activate
            // the schema cache, but that endpoint has been deleted (task
            // XGSUbdBiIzla) so passwords can't cross HTTP. SQLite
            // connection strings carry no credentials — just a file path
            // — so it's safe (and simpler) for the server to activate
            // itself here before announcing completion. The client now
            // treats the Complete! event as "ready to switch views" and
            // doesn't need a follow-up bind request.
            currentConnectionString = sqliteConnectionString;
            currentProvider = BifrostDbProvider.Sqlite;
            bifrostOptions?.BindConnectionString(sqliteConnectionString);
            bifrostOptions?.BindProvider("sqlite");
            bifrostOptions?.ResetSchema(app.Services);

            yield return SseEvent("Complete!", 100, "Quickstart database created successfully",
                connectionString: sqliteConnectionString, provider: "sqlite");
        }

        return WriteSseStream(StreamProgress());
    });

    // Health check endpoint
    app.MapGet("/api/health", () => Results.Ok(new
    {
        status = "ok",
        connected = !string.IsNullOrEmpty(currentConnectionString),
        provider = currentProvider?.ToString().ToLowerInvariant()
    }));

    // POST /api/ssh/connect — Start an SSH tunnel
    app.MapPost("/api/ssh/connect", async (SshConnectRequest request, CancellationToken ct) =>
    {
        try
        {
            var config = new BifrostQL.UI.SshTunnelConfig(
                request.SshHost, request.SshPort, request.SshUsername,
                request.IdentityFile, request.RemoteHost, request.RemotePort);
            var localPort = await sshTunnel.StartAsync(config, ct);
            return Results.Ok(new { success = true, localPort });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { success = false, error = ex.Message });
        }
    });

    // POST /api/ssh/disconnect — Stop the SSH tunnel
    app.MapPost("/api/ssh/disconnect", async () =>
    {
        await sshTunnel.StopAsync();
        return Results.Ok(new { success = true });
    });

    // GET /api/ssh/status — Check tunnel status
    app.MapGet("/api/ssh/status", () => Results.Ok(sshTunnel.GetStatus()));

    // POST /api/ssh/wp-discover — Discover WordPress DB credentials via wp-cli over SSH
    app.MapPost("/api/ssh/wp-discover", async (WpDiscoverRequest request, CancellationToken ct) =>
    {
        try
        {
            var sshConfig = new BifrostQL.UI.SshTunnelConfig(
                request.SshHost, request.SshPort, request.SshUsername,
                request.IdentityFile, "localhost", 3306);
            var wpConfig = new BifrostQL.UI.WpDiscoverConfig(request.WpPath, request.WpRoot);
            var creds = await sshTunnel.DiscoverWordPressAsync(sshConfig, wpConfig, ct);
            return Results.Ok(new
            {
                success = true,
                dbName = creds.DbName,
                dbUser = creds.DbUser,
                dbPassword = creds.DbPassword,
                dbHost = creds.DbHost,
            });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { success = false, error = ex.Message });
        }
    });

    // GET /api/vault/servers — List saved servers (metadata only, no passwords)
    app.MapGet("/api/vault/servers", () =>
    {
        try
        {
            var servers = VaultServerProvider.LoadServers(activeVaultPath);
            var result = servers.Select(s => new
            {
                name = s.Server.Name,
                provider = s.Server.Provider,
                host = s.Server.Host,
                port = s.Server.Port,
                database = s.Server.Database,
                tags = s.Server.Tags,
                hasSsh = s.Server.Ssh is not null,
                hasPassword = !string.IsNullOrEmpty(s.Server.Password),
                source = s.Source,
            });
            return Results.Ok(result);
        }
        catch
        {
            return Results.Ok(Array.Empty<object>());
        }
    });

    // POST /api/vault/connect — Connect using a vault server by name (credentials stay server-side)
    app.MapPost("/api/vault/connect", async (VaultConnectRequest request, CancellationToken ct) =>
    {
        try
        {
            var servers = VaultServerProvider.LoadServers(activeVaultPath);
            var match = servers.FirstOrDefault(s => s.Server.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
            if (match.Server is null)
                return Results.NotFound(new { success = false, error = $"Server '{request.Name}' not found" });

            var server = match.Server;
            var connStr = VaultServerProvider.BuildConnectionString(server);

            // If SSH config present, start tunnel and rewrite connection string
            if (server.Ssh is not null)
            {
                var remoteHost = server.Host;
                var remotePort = server.Port;
                var sshConfig = new BifrostQL.UI.SshTunnelConfig(
                    server.Ssh.Host, server.Ssh.Port, server.Ssh.Username,
                    server.Ssh.IdentityFile, remoteHost, remotePort);
                var localPort = await sshTunnel.StartAsync(sshConfig, ct);

                // WordPress credential auto-discovery runs when the vault entry is
                // tagged "wordpress" AND has no explicit username. We `wp config get`
                // over the SSH tunnel to populate DB_USER/DB_PASSWORD/DB_NAME. Other
                // SSH-tunneled entries (no wordpress tag, or with explicit credentials)
                // pass straight through and let the DB driver surface auth failures.
                string? dbUser = null, dbPassword = null, dbName = null;
                var wantsWpDiscovery =
                    server.Tags.Any(t => string.Equals(t, "wordpress", StringComparison.OrdinalIgnoreCase))
                    && string.IsNullOrWhiteSpace(server.Username);
                if (wantsWpDiscovery)
                {
                    BifrostQL.UI.WpCredentials? discovered = null;
                    Exception? lastError = null;

                    foreach (var wpRoot in BifrostQL.UI.Vault.WordPressDiscovery.DefaultRoots)
                    {
                        try
                        {
                            var wpConfig = new BifrostQL.UI.WpDiscoverConfig("wp", wpRoot);
                            var creds = await sshTunnel.DiscoverWordPressAsync(sshConfig, wpConfig, ct);
                            if (!string.IsNullOrWhiteSpace(creds.DbUser))
                            {
                                discovered = creds;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                        }
                    }

                    if (discovered is null)
                    {
                        var rootList = string.Join(", ", BifrostQL.UI.Vault.WordPressDiscovery.DefaultRoots);
                        var detail = lastError?.Message ?? "no installations found";
                        return Results.BadRequest(new
                        {
                            success = false,
                            error = $"WordPress auto-discovery failed (searched {rootList}): {detail}. " +
                                    "Set explicit Username/Password on the vault entry, or drop the " +
                                    "'wordpress' tag to skip auto-discovery."
                        });
                    }

                    dbUser = discovered.DbUser;
                    dbPassword = discovered.DbPassword;
                    dbName = discovered.DbName;
                }

                // Rewrite to tunnel through localhost with discovered credentials
                var tunneled = server with 
                { 
                    Host = "127.0.0.1", 
                    Port = localPort,
                    Username = dbUser ?? server.Username,
                    Password = dbPassword ?? server.Password,
                    Database = dbName ?? server.Database
                };
                connStr = VaultServerProvider.BuildConnectionString(tunneled);
            }

            var provider = DbConnFactoryResolver.ParseProviderName(server.Provider);
            currentConnectionString = connStr;
            currentProvider = provider;

            bifrostOptions?.BindConnectionString(connStr);
            bifrostOptions?.BindProvider(provider.ToString().ToLowerInvariant());
            bifrostOptions?.ResetSchema(app.Services);

            return Results.Ok(new
            {
                success = true,
                provider = server.Provider,
                server = server.Host,
                database = server.Database ?? "",
                name = server.Name,
            });
        }
        catch (Exception ex)
        {
            // SECURITY: every string derived from the exception is routed through
            // SecretScrubber before it leaves this process. DB drivers occasionally
            // embed the full connection string (including Password=...) inside
            // exception messages and Data dictionaries, so we must not forward
            // ex.Message / ex.StackTrace / ex.ToString() verbatim to the browser
            // or the log stream. See BifrostQL.Core.Utils.SecretScrubber for the
            // patterns covered.
            var logger = app.Services.GetRequiredService<ILogger<Program>>();

            var scrubbedMessage = SecretScrubber.Scrub(ex.Message) ?? "";
            var scrubbedDetailsBuilder = new StringBuilder();
            scrubbedDetailsBuilder.Append(scrubbedMessage);
            scrubbedDetailsBuilder.Append("\n\nStack trace:\n");
            scrubbedDetailsBuilder.Append(SecretScrubber.Scrub(ex.StackTrace) ?? "");
            if (ex.InnerException != null)
            {
                scrubbedDetailsBuilder.Append("\n\nInner exception: ");
                scrubbedDetailsBuilder.Append(SecretScrubber.Scrub(ex.InnerException.Message) ?? "");
                scrubbedDetailsBuilder.Append('\n');
                scrubbedDetailsBuilder.Append(SecretScrubber.Scrub(ex.InnerException.StackTrace) ?? "");
            }
            var scrubbedDetails = scrubbedDetailsBuilder.ToString();

            // Do NOT pass `ex` directly to the logger — the default logging
            // formatters call ex.ToString() which would bypass the scrubber.
            // Instead log the exception type + scrubbed message as positional args.
            logger.LogError(
                "Vault connect failed for '{ServerName}' ({ExceptionType}): {ScrubbedMessage}",
                request.Name,
                ex.GetType().FullName,
                scrubbedMessage);

            return Results.BadRequest(new
            {
                success = false,
                error = scrubbedMessage,
                details = scrubbedDetails
            });
        }
    });

    // Serve static files from wwwroot
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // BifrostQL GraphQL endpoint — always registered, connection set dynamically
    app.UseBifrostQL();

    // Fallback to index.html for SPA routing
    app.MapFallbackToFile("index.html");

    // Start the server in the background
    var serverTask = app.RunAsync(cancellationToken);

    Console.WriteLine($"BifrostQL server started at {serverUrl}");
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
        sshTunnel.Dispose();
    }
    else
    {
        // Create the Photino window. DevTools and the WebView context menu are
        // gated to Development so Release builds never expose F12 / right-click-Inspect
        // on the embedded React SPA (protects JS heap + source maps).
        var isDev = app.Environment.IsDevelopment();
        var window = new PhotinoWindow()
            .SetTitle("BifrostQL - Database Explorer")
            .SetSize(1400, 900)
            .Center()
            .SetDevToolsEnabled(isDev)
            .SetContextMenuEnabled(isDev)
            .Load(localUrl);

        // Native bridge: in-process request/response channel between the
        // React SPA and this C# host. Deliberately non-HTTP — messages ride
        // the Photino webview IPC so credentials and host-only features
        // never traverse localhost sockets. The ping handler is a smoke
        // test for the wire format; real handlers land in subsequent tasks.
        var bridgeLogger = app.Services
            .GetService<ILoggerFactory>()?
            .CreateLogger<BifrostQL.UI.NativeBridge.NativeBridgeHost>();
        using var nativeBridge =
            new BifrostQL.UI.NativeBridge.NativeBridgeHost(window, bridgeLogger);
        nativeBridge.Register("ping", (payload, _) =>
        {
            // Echo the raw JSON back so the caller can confirm its payload
            // round-tripped unchanged through System.Text.Json. Using
            // GetRawText keeps us agnostic to payload shape — primitives,
            // objects, null all fall out the same way.
            var echo = payload.ValueKind == JsonValueKind.Undefined
                ? "undefined"
                : payload.GetRawText();
            return Task.FromResult<object?>(new { pong = true, echo });
        });

        // Credential prompt: the renderer asks for vault credentials for
        // a named entry, the host opens an isolated Photino child window
        // (separate WebView2 instance, own NativeBridgeHost, CSP-locked
        // embedded HTML — see CredentialPromptWindow / CredentialPromptHtml)
        // that collects the password and hands it back here.
        //
        // SECURITY:
        //   - The full ConnectionInfo payload (vaultName, provider, host,
        //     port, database, username, ssl) comes from the main SPA over
        //     the bridge. None of these are sensitive.
        //   - The password is ONLY collected by the child window and never
        //     returned to the renderer. This handler writes the complete
        //     VaultServer record to the encrypted vault via VaultStore.Save
        //     inside the process, then drops the CredentialResult reference
        //     so the password string becomes unreachable.
        //   - The bridge response to the renderer is {saved, name}; no
        //     username, no password, nothing sensitive. The renderer then
        //     refetches /api/vault/servers and calls /api/vault/connect
        //     with the returned name to activate the connection.
        var bridgeWindow = window;
        var bridgeLoggerForHandler = bridgeLogger;
        nativeBridge.Register("request-credential", async (payload, innerCt) =>
        {
            // ConnectionInfo extraction — every field is optional on the
            // wire except vaultName and provider (provider is required so
            // we can pick a sensible default port). Missing fields fall
            // through to defaults or persist as null on the vault entry.
            if (payload.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("ConnectionInfo payload required");

            string? ReadString(string key) =>
                payload.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString()
                    : null;
            int? ReadInt(string key) =>
                payload.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)
                    ? i
                    : null;
            bool? ReadBool(string key) =>
                payload.TryGetProperty(key, out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
                    ? p.GetBoolean()
                    : null;

            var vaultName = ReadString("vaultName");
            if (string.IsNullOrWhiteSpace(vaultName))
                throw new ArgumentException("vaultName required");

            var provider = ReadString("provider")?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(provider))
                throw new ArgumentException("provider required");

            var host = ReadString("host") ?? "";
            var port = ReadInt("port") ?? provider switch
            {
                "postgres" => 5432,
                "mysql" => 3306,
                "sqlserver" => 1433,
                _ => 0,
            };
            var database = ReadString("database");
            var username = ReadString("username");
            var ssl = ReadBool("ssl");
            var sslMode = ssl == true ? "Require" : null;

            // Collect the password via the isolated child window. This
            // call blocks until the user clicks Save, Cancel, or closes
            // the window. PromptAsync throws OperationCanceledException
            // on innerCt cancellation, which NativeBridgeHost catches and
            // scrubs into a BridgeError("error", message with "cancel")
            // — the TS wrapper turns that into CredentialCancelledError.
            var result = await BifrostQL.UI.NativeBridge.CredentialPromptWindow
                .PromptAsync(bridgeWindow, vaultName!, bridgeLoggerForHandler, innerCt)
                .ConfigureAwait(false);

            if (!result.IsSaved)
            {
                // User cancelled — surface as a bridge error so the wrapper
                // can map it to CredentialCancelledError. Using a message
                // containing "cancel" is the contract the wrapper matches.
                throw new OperationCanceledException("Credential prompt cancelled by user");
            }

            // Construct the vault entry. The username from the child
            // window takes precedence over the payload username — the
            // child window is authoritative on what the user typed. If
            // somehow the child window returned an empty username, fall
            // back to the payload-supplied one so we still have a value.
            var effectiveUsername = string.IsNullOrEmpty(result.Username)
                ? username
                : result.Username;

            var server = new BifrostQL.UI.Vault.VaultServer(
                Name: vaultName!,
                Provider: provider!,
                Host: host,
                Port: port,
                Database: database,
                Username: effectiveUsername,
                Password: result.Password,
                SslMode: sslMode,
                Ssh: null,
                Tags: new List<string>());

            // Load + upsert + save, same as the CLI `vault add` path.
            var vault = BifrostQL.UI.Vault.VaultStore.Load(activeVaultPath);
            var servers = vault.Servers
                .Where(s => !s.Name.Equals(vaultName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            servers.Add(server);
            vault = vault with { Servers = servers };
            BifrostQL.UI.Vault.VaultStore.Save(vault, activeVaultPath);

            // Drop all references to the password ASAP. The VaultServer
            // `server` local still has it, so null both the local and the
            // result. .NET can't guarantee the heap string is collected
            // immediately, but this severs the only paths from which any
            // subsequent code could reach the plaintext.
            server = null!;
            result = null!;

            return new { saved = true, name = vaultName! };
        });

        window.WaitForClose();

        // Shutdown the server and SSH tunnel when window closes
        sshTunnel.Dispose();
        await app.StopAsync();
    }

    return 0;
});

return await rootCommand.Parse(args).InvokeAsync();

// Lists PostgreSQL databases by shelling out to psql via sudo -u <user>.
// Used for peer/ident auth where the .NET process runs as a different OS user.
static async Task<string[]> ListDatabasesViaPsqlAsync(string connectionString, string? psqlUser, CancellationToken ct)
{
    // Parse host/port from connection string for psql args
    var kvs = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2))
        .Where(p => p.Length == 2)
        .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
    kvs.TryGetValue("host", out var host);
    kvs.TryGetValue("port", out var port);

    // Build psql args: output database names only, no headers, no alignment
    var psqlArgs = new List<string> { "-t", "-A", "-c",
        "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname" };
    // For peer auth, only pass -h if it's a socket path (starts with /).
    // Passing -h localhost forces TCP which bypasses peer auth.
    if (!string.IsNullOrWhiteSpace(host) && host.StartsWith('/'))
    {
        psqlArgs.AddRange(new[] { "-h", host });
    }
    if (!string.IsNullOrWhiteSpace(port) && port != "5432")
    {
        psqlArgs.AddRange(new[] { "-p", port });
    }

    var psi = new System.Diagnostics.ProcessStartInfo();
    if (!string.IsNullOrWhiteSpace(psqlUser))
    {
        // Use sudo -u <user> psql for peer auth as a different OS user
        psi.FileName = "sudo";
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(psqlUser);
        psi.ArgumentList.Add("psql");
    }
    else
    {
        psi.FileName = "psql";
    }
    foreach (var arg in psqlArgs)
        psi.ArgumentList.Add(arg);

    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    psi.UseShellExecute = false;
    psi.CreateNoWindow = true;

    using var proc = System.Diagnostics.Process.Start(psi)
        ?? throw new InvalidOperationException("Failed to start psql");

    var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
    var stderr = await proc.StandardError.ReadToEndAsync(ct);
    await proc.WaitForExitAsync(ct);

    if (proc.ExitCode != 0)
    {
        var msg = string.IsNullOrWhiteSpace(stderr) ? $"psql exited with code {proc.ExitCode}" : stderr.Trim();
        throw new InvalidOperationException($"psql failed: {msg}");
    }

    return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

// Helper to format SSE event JSON consistently
static string SseEvent(string stage, int percent, string message,
    bool error = false, string? connectionString = null, string? provider = null)
{
    var obj = new Dictionary<string, object>
    {
        ["stage"] = stage,
        ["percent"] = percent,
        ["message"] = message
    };
    if (error) obj["error"] = true;
    if (connectionString != null) obj["connectionString"] = connectionString;
    if (provider != null) obj["provider"] = provider;
    return $"data: {JsonSerializer.Serialize(obj)}\n\n";
}

// Writes an IAsyncEnumerable of pre-formatted SSE strings as a text/event-stream response
static IResult WriteSseStream(IAsyncEnumerable<string> events)
{
    return Results.Stream(async stream =>
    {
        await foreach (var evt in events)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(evt);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
    }, contentType: "text/event-stream");
}

// Record types for API requests
record ConnectionTestRequest(string ConnectionString, string? Provider = null);
// ConnectionSetRequest removed with /api/connection/set — see task XGSUbdBiIzla.
record ListDatabasesRequest(string ConnectionString, string? Provider = null, bool PeerAuth = false, string? PsqlUser = null);
record SshConnectRequest(string SshHost, int SshPort, string SshUsername,
    string? IdentityFile, string RemoteHost, int RemotePort);
record WpDiscoverRequest(string SshHost, int SshPort, string SshUsername,
    string? IdentityFile, string? WpPath, string? WpRoot);
record VaultConnectRequest(string Name);
record CreateDatabaseRequest(string Template, string? ConnectionString);
record QuickstartRequest(string Schema, string? DataSize = "sample");

// Embedded resource loader for quickstart schemas
public static class QuickstartSchemas
{
    private static readonly Assembly ResourceAssembly = typeof(QuickstartSchemas).Assembly;

    public static string? LoadSchemaSql(string schemaName)
    {
        return LoadEmbeddedResource($"BifrostQL.UI.Schemas.{schemaName}.sql");
    }

    public static string? LoadSeedSql(string schemaName, string dataSize)
    {
        return LoadEmbeddedResource($"BifrostQL.UI.Schemas.{schemaName}-seed-{dataSize}.sql");
    }

    public static async Task ExecuteStatementsAsync(IDbConnFactory factory, string[] statements, CancellationToken ct)
    {
        await using var conn = factory.GetConnection();
        await conn.OpenAsync(ct);
        await using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA foreign_keys = ON;";
        await pragmaCmd.ExecuteNonQueryAsync(ct);

        foreach (var statement in statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static string? LoadEmbeddedResource(string resourceName)
    {
        using var stream = ResourceAssembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

// SQL Schema generation methods (legacy - SQL Server test databases)
public static class TestDatabaseSchemas
{
    public static string GetNorthwindSchema() => @"
CREATE TABLE Categories (
    CategoryID INT IDENTITY(1,1) PRIMARY KEY,
    CategoryName NVARCHAR(100) NOT NULL,
    Description NVARCHAR(500)
);

CREATE TABLE Products (
    ProductID INT IDENTITY(1,1) PRIMARY KEY,
    ProductName NVARCHAR(100) NOT NULL,
    CategoryID INT FOREIGN KEY REFERENCES Categories(CategoryID),
    UnitPrice DECIMAL(10,2) DEFAULT 0,
    UnitsInStock INT DEFAULT 0,
    Discontinued BIT DEFAULT 0
);

CREATE TABLE Customers (
    CustomerID NVARCHAR(10) PRIMARY KEY,
    CompanyName NVARCHAR(100) NOT NULL,
    ContactName NVARCHAR(100),
    Country NVARCHAR(50)
);

CREATE TABLE Orders (
    OrderID INT IDENTITY(1,1) PRIMARY KEY,
    CustomerID NVARCHAR(10) FOREIGN KEY REFERENCES Customers(CustomerID),
    OrderDate DATETIME DEFAULT GETDATE(),
    ShippedDate DATETIME,
    ShipCountry NVARCHAR(50)
);

CREATE TABLE OrderDetails (
    OrderDetailID INT IDENTITY(1,1) PRIMARY KEY,
    OrderID INT FOREIGN KEY REFERENCES Orders(OrderID),
    ProductID INT FOREIGN KEY REFERENCES Products(ProductID),
    UnitPrice DECIMAL(10,2) NOT NULL,
    Quantity INT DEFAULT 1
);
";

    public static string GetNorthwindData() => @"
INSERT INTO Categories (CategoryName, Description) VALUES
('Beverages', 'Soft drinks, coffees, teas, beers'),
('Condiments', 'Sweet and savory sauces'),
('Confections', 'Desserts and candies');

INSERT INTO Products (ProductName, CategoryID, UnitPrice, UnitsInStock) VALUES
('Chai', 1, 18.00, 39),
('Chang', 1, 19.00, 17),
('Aniseed Syrup', 2, 10.00, 13);

INSERT INTO Customers (CustomerID, CompanyName, ContactName, Country) VALUES
('ALFKI', 'Alfreds Futterkiste', 'Maria Anders', 'Germany'),
('ANATR', 'Ana Trujillo Emparedados', 'Ana Trujillo', 'Mexico'),
('ANTON', 'Antonio Moreno Taqueria', 'Antonio Moreno', 'Mexico');

INSERT INTO Orders (CustomerID, OrderDate, ShipCountry) VALUES
('ALFKI', GETDATE(), 'Germany'),
('ANATR', DATEADD(day, -1, GETDATE()), 'Mexico');

INSERT INTO OrderDetails (OrderID, ProductID, UnitPrice, Quantity) VALUES
(1, 1, 18.00, 10),
(1, 2, 19.00, 5),
(2, 3, 10.00, 20);
";

    public static string GetAdventureWorksLiteSchema() => @"
CREATE TABLE Departments (
    DepartmentID INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    GroupName NVARCHAR(100)
);

CREATE TABLE Employees (
    EmployeeID INT IDENTITY(1,1) PRIMARY KEY,
    FirstName NVARCHAR(50) NOT NULL,
    LastName NVARCHAR(50) NOT NULL,
    DepartmentID INT FOREIGN KEY REFERENCES Departments(DepartmentID),
    HireDate DATETIME DEFAULT GETDATE()
);

CREATE TABLE Shifts (
    ShiftID INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(50) NOT NULL,
    StartTime TIME NOT NULL,
    EndTime TIME NOT NULL
);

CREATE TABLE EmployeeDepartmentHistory (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeID INT FOREIGN KEY REFERENCES Employees(EmployeeID),
    DepartmentID INT FOREIGN KEY REFERENCES Departments(DepartmentID),
    ShiftID INT FOREIGN KEY REFERENCES Shifts(ShiftID),
    StartDate DATETIME NOT NULL,
    EndDate DATETIME NULL
);
";

    public static string GetAdventureWorksLiteData() => @"
INSERT INTO Departments (Name, GroupName) VALUES
('Engineering', 'Research and Development'),
('Sales', 'Sales and Marketing'),
('Finance', 'Executive General and Administration');

INSERT INTO Shifts (Name, StartTime, EndTime) VALUES
('Day', '06:00:00', '14:00:00'),
('Evening', '14:00:00', '22:00:00'),
('Night', '22:00:00', '06:00:00');

INSERT INTO Employees (FirstName, LastName, DepartmentID, HireDate) VALUES
('John', 'Smith', 1, '2020-01-15'),
('Jane', 'Doe', 2, '2021-03-20'),
('Bob', 'Johnson', 1, '2019-11-05');

INSERT INTO EmployeeDepartmentHistory (EmployeeID, DepartmentID, ShiftID, StartDate) VALUES
(1, 1, 1, '2020-01-15'),
(2, 2, 1, '2021-03-20'),
(3, 1, 2, '2019-11-05');
";

    public static string GetSimpleBlogSchema() => @"
CREATE TABLE Users (
    UserID INT IDENTITY(1,1) PRIMARY KEY,
    Username NVARCHAR(50) UNIQUE NOT NULL,
    Email NVARCHAR(100) UNIQUE NOT NULL,
    CreatedAt DATETIME DEFAULT GETDATE()
);

CREATE TABLE Posts (
    PostID INT IDENTITY(1,1) PRIMARY KEY,
    Title NVARCHAR(200) NOT NULL,
    Content NVARCHAR(MAX) NOT NULL,
    AuthorID INT FOREIGN KEY REFERENCES Users(UserID),
    PublishedAt DATETIME DEFAULT GETDATE(),
    UpdatedAt DATETIME DEFAULT GETDATE()
);

CREATE TABLE Comments (
    CommentID INT IDENTITY(1,1) PRIMARY KEY,
    PostID INT FOREIGN KEY REFERENCES Posts(PostID),
    AuthorID INT FOREIGN KEY REFERENCES Users(UserID),
    Content NVARCHAR(1000) NOT NULL,
    CreatedAt DATETIME DEFAULT GETDATE()
);

CREATE TABLE Tags (
    TagID INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(50) UNIQUE NOT NULL
);

CREATE TABLE PostTags (
    PostTagID INT IDENTITY(1,1) PRIMARY KEY,
    PostID INT FOREIGN KEY REFERENCES Posts(PostID),
    TagID INT FOREIGN KEY REFERENCES Tags(TagID)
);
";

    public static string GetSimpleBlogData() => @"
INSERT INTO Users (Username, Email) VALUES
('admin', 'admin@blog.com'),
('johndoe', 'john@example.com'),
('janedoe', 'jane@example.com');

INSERT INTO Posts (Title, Content, AuthorID) VALUES
('Welcome to the Blog', 'This is our first post!', 1),
('GraphQL Basics', 'Learn about GraphQL queries and mutations.', 1),
('Building APIs', 'Tips for building modern APIs.', 2);

INSERT INTO Comments (PostID, AuthorID, Content) VALUES
(1, 2, 'Great first post!'),
(1, 3, 'Looking forward to more content.'),
(2, 3, 'Very helpful explanation!');

INSERT INTO Tags (Name) VALUES
('GraphQL'),
('Tutorial'),
('API');

INSERT INTO PostTags (PostID, TagID) VALUES
(2, 1),
(2, 2),
(3, 3);
";
}
