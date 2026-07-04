using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;

namespace BifrostQL.UI.Web
{
    /// <summary>
    /// Connection-oriented endpoints: test a connection string, list databases on
    /// a server, the disabled legacy create tombstone, and the SQLite quickstart
    /// creation stream (which self-binds the freshly created database).
    /// </summary>
    public static class ConnectionEndpoints
    {
        public static void MapConnectionEndpoints(this WebApplication app, ConnectionState state)
        {
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

                    return Results.Ok(new
                    {
                        success = true,
                        message = $"Successfully connected to {conn.Database} via {provider}",
                        database = conn.Database,
                        provider = provider.ToString().ToLowerInvariant()
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new
                    {
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
                        var databases = await PsqlDatabaseLister.ListDatabasesAsync(request.ConnectionString, request.PsqlUser, ct);
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

            // Legacy SQL Server test-database creation accepted password-bearing
            // connection strings over HTTP. Keep the route as an explicit tombstone so
            // older clients receive a clear migration response without sending secrets
            // through the old streaming path.
            app.MapPost("/api/database/create", () =>
            {
                return Results.Json(new
                {
                    error = "The legacy SQL Server database creation endpoint is disabled because it accepted password-bearing connection strings over HTTP. Use /api/database/create-quickstart for SQLite quickstarts or create a saved vault entry and connect with /api/vault/connect."
                }, statusCode: StatusCodes.Status410Gone);
            });

            // POST /api/database/create-quickstart - Creates a SQLite quickstart database
            app.MapPost("/api/database/create-quickstart", (QuickstartRequest request, CancellationToken ct) =>
            {
                var validSchemas = new[] { "blog", "ecommerce", "crm", "classroom", "project-tracker", "sqlite-advanced", "org-model", "membership-manager" };
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

                    yield return SseWriter.Event("Creating database", 10, $"Creating SQLite database: {fileName}");

                    var factory = DbConnFactoryResolver.Create(sqliteConnectionString, BifrostDbProvider.Sqlite);

                    yield return SseWriter.Event("Loading schema", 20, $"Reading {request.Schema} schema definition");

                    var ddlSql = await QuickstartSchemas.LoadSchemaSql(request.Schema);
                    if (ddlSql == null)
                    {
                        yield return SseWriter.Event("Error", 0, $"Schema '{request.Schema}' not found in embedded resources", error: true);
                        yield break;
                    }

                    yield return SseWriter.Event("Creating tables", 30, "Executing DDL statements");

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
                        yield return SseWriter.Event("Error", 0, $"Failed to create schema: {ddlError.Message}", error: true);
                        yield break;
                    }

                    yield return SseWriter.Event("Schema created", 70, $"Created {ddlStatements.Length} tables");

                    yield return SseWriter.Event("Loading seed data", 75, $"Loading {dataSize} dataset");

                    var seedSql = await QuickstartSchemas.LoadSeedSql(request.Schema, dataSize);

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
                            yield return SseWriter.Event("Error", 0, $"Failed to insert seed data: {seedError.Message}", error: true);
                            yield break;
                        }

                        yield return SseWriter.Event("Data loaded", 95, $"Inserted {seedStatements.Length} data batches");
                    }
                    else
                    {
                        yield return SseWriter.Event("Seed data", 90, "No seed data available for this schema/size combination (DDL only)");
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
                    state.ConnectionString = sqliteConnectionString;
                    state.Provider = BifrostDbProvider.Sqlite;
                    state.Options?.BindConnectionString(sqliteConnectionString);
                    state.Options?.BindProvider("sqlite");
                    // Rebind profiles from this schema's bundled <schema>.bifrost.json BEFORE
                    // ResetSchema, so the next schema rebuild picks up the new profile set.
                    await ProfileActivation.RebindProfilesAsync(app.Services, request.Schema);
                    state.Options?.ResetSchema(app.Services);

                    yield return SseWriter.Event("Complete!", 100, "Quickstart database created successfully",
                        connectionString: sqliteConnectionString, provider: "sqlite");
                }

                return SseWriter.WriteStream(StreamProgress());
            });
        }
    }
}
