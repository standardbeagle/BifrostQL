using System.Diagnostics;
using System.Text;
using GraphQL;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Model;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using NpgsqlTypes;

namespace BifrostQL.Benchmarks;

/// <summary>
/// Measures the set-based batch fast path against real database servers, invoked with
/// <c>dotnet run -c Release -- --bulk-paths</c>. For each provider whose connection string
/// is present (BIFROST_BENCH_SQLSERVER / BIFROST_BENCH_POSTGRES / BIFROST_BENCH_MYSQL,
/// falling back to the BIFROST_TEST_* names) it times, per batch size:
///
///  - per-row  : the per-row batch pipeline (bulk-batch-threshold 0) — the baseline every
///               BifrostQL deployment gets without configuration.
///  - bulk     : the set-based fast path (threshold 1) — staging load + set-based DML in
///               one SQL-level transaction, streaming staging where the provider supports it.
///  - floor    : the provider's native bulk API straight into the target table with NO
///               pipeline (SqlBulkCopy / binary COPY / MySqlBulkCopy) — the theoretical
///               minimum, shown so the pipeline's overhead is honest.
///  - graphql  : the bulk path driven through the full GraphQL document executor, so the
///               parse/validation cost of a large _batch mutation is visible.
///
/// Results print as a markdown table (median of 5 iterations after 1 warmup) for the
/// performance reference doc. This is a wall-clock harness, not BenchmarkDotNet: every
/// scenario is database-bound at millisecond scale, where server variance dominates and
/// process-isolated micro-benchmarking buys nothing.
/// </summary>
public static class BulkPathBenchRunner
{
    private const int Iterations = 5;

    private sealed record Provider(
        string Name,
        string? MasterConnString,
        Func<string, string, Task> CreateDb,
        Func<string, string, Task> DropDb,
        Func<string, string, string> BuildDbConnString,
        Func<string, IDbConnFactory> CreateFactory,
        string TableDdl,
        Func<System.Data.Common.DbConnection, int, Task> RawFloorInsert);

    public static async Task RunAsync()
    {
        var providers = new[]
        {
            new Provider(
                "SQL Server",
                Env("BIFROST_BENCH_SQLSERVER", "BIFROST_TEST_SQLSERVER"),
                async (master, db) => await ExecAsync(new SqlConnection(master), $"CREATE DATABASE [{db}]"),
                async (master, db) => await ExecAsync(new SqlConnection(master),
                    $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}];"),
                (master, db) => new SqlConnectionStringBuilder(master) { InitialCatalog = db }.ConnectionString,
                cs => new SqlServer.SqlServerDbConnFactory(cs),
                """
                CREATE TABLE bench_rows (
                    id INT NOT NULL PRIMARY KEY,
                    a INT NOT NULL,
                    b NVARCHAR(100) NOT NULL,
                    c DECIMAL(18,2) NOT NULL
                )
                """,
                SqlServerFloorAsync),
            new Provider(
                "PostgreSQL",
                Env("BIFROST_BENCH_POSTGRES", "BIFROST_TEST_POSTGRES"),
                async (master, db) => await ExecAsync(new NpgsqlConnection(master), $"CREATE DATABASE \"{db}\""),
                async (master, db) => await ExecAsync(new NpgsqlConnection(master), $"DROP DATABASE \"{db}\" WITH (FORCE)"),
                (master, db) => new NpgsqlConnectionStringBuilder(master) { Database = db }.ConnectionString,
                cs => new Ngsql.PostgresDbConnFactory(cs),
                """
                CREATE TABLE bench_rows (
                    id INT NOT NULL PRIMARY KEY,
                    a INT NOT NULL,
                    b VARCHAR(100) NOT NULL,
                    c NUMERIC(18,2) NOT NULL
                )
                """,
                PostgresFloorAsync),
            new Provider(
                "MySQL",
                Env("BIFROST_BENCH_MYSQL", "BIFROST_TEST_MYSQL"),
                async (master, db) => await ExecAsync(new MySqlConnection(master), $"CREATE DATABASE `{db}`"),
                async (master, db) => await ExecAsync(new MySqlConnection(master), $"DROP DATABASE `{db}`"),
                (master, db) => new MySqlConnectionStringBuilder(master) { Database = db }.ConnectionString,
                cs => new MySql.MySqlDbConnFactory(cs),
                """
                CREATE TABLE bench_rows (
                    id INT NOT NULL PRIMARY KEY,
                    a INT NOT NULL,
                    b VARCHAR(100) NOT NULL,
                    c DECIMAL(18,2) NOT NULL
                )
                """,
                MySqlFloorAsync),
        };

        Console.WriteLine("| Provider | Scenario | Rows | Median ms | Rows/s |");
        Console.WriteLine("|----------|----------|------|-----------|--------|");

        foreach (var provider in providers)
        {
            if (provider.MasterConnString is null)
            {
                Console.Error.WriteLine($"-- {provider.Name}: connection string not set, skipping");
                continue;
            }
            await RunProviderAsync(provider);
        }
    }

    private static async Task RunProviderAsync(Provider provider)
    {
        var dbName = $"bifrost_bench_{Guid.NewGuid():N}";
        await provider.CreateDb(provider.MasterConnString!, dbName);
        try
        {
            var connString = provider.BuildDbConnString(provider.MasterConnString!, dbName);
            var factory = provider.CreateFactory(connString);
            await using (var conn = factory.GetConnection())
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = provider.TableDdl;
                await cmd.ExecuteNonQueryAsync();
            }

            var perRowModel = await LoadModelAsync(factory, bulkThreshold: "0");
            var bulkModel = await LoadModelAsync(factory, bulkThreshold: "1");

            // Insert scenarios. The per-row path at 10k costs 10k round trips; it is
            // reported at 100/1000 and the trend extrapolates linearly.
            foreach (var rows in new[] { 100, 1000 })
                await ReportAsync(provider.Name, "insert per-row", rows, factory,
                    () => RunBatchAsync(perRowModel, factory, InsertActions(rows)));
            foreach (var rows in new[] { 100, 1000, 10000 })
                await ReportAsync(provider.Name, "insert bulk", rows, factory,
                    () => RunBatchAsync(bulkModel, factory, InsertActions(rows)));
            foreach (var rows in new[] { 1000, 10000 })
                await ReportAsync(provider.Name, "insert floor", rows, factory, async () =>
                {
                    await using var conn = factory.GetConnection();
                    await conn.OpenAsync();
                    await provider.RawFloorInsert(conn, rows);
                });

            // Update / delete at 1000 rows over a seeded table.
            await ReportAsync(provider.Name, "update per-row", 1000, factory,
                () => RunBatchAsync(perRowModel, factory, UpdateActions(1000)), seedRows: 1000, bulkSeedModel: bulkModel);
            await ReportAsync(provider.Name, "update bulk", 1000, factory,
                () => RunBatchAsync(bulkModel, factory, UpdateActions(1000)), seedRows: 1000, bulkSeedModel: bulkModel);
            await ReportAsync(provider.Name, "delete per-row", 1000, factory,
                () => RunBatchAsync(perRowModel, factory, DeleteActions(1000)), seedRows: 1000, bulkSeedModel: bulkModel);
            await ReportAsync(provider.Name, "delete bulk", 1000, factory,
                () => RunBatchAsync(bulkModel, factory, DeleteActions(1000)), seedRows: 1000, bulkSeedModel: bulkModel);

            // Full GraphQL document execution over the bulk path: shows parse + validation
            // cost of a large _batch mutation on top of the pipeline. Schema construction is
            // hoisted out of the timed region — it is a startup cost, not a per-batch cost.
            var schema = Core.Schema.DbSchema.FromModel(bulkModel);
            var mutation = BuildInsertMutation(1000);
            await ReportAsync(provider.Name, "insert graphql+bulk", 1000, factory,
                () => RunGraphQlAsync(bulkModel, schema, factory, mutation));
        }
        finally
        {
            try { await provider.DropDb(provider.MasterConnString!, dbName); }
            catch { /* best effort */ }
        }
    }

    // ---- scenario plumbing ----

    private static async Task ReportAsync(
        string providerName, string scenario, int rows, IDbConnFactory factory,
        Func<Task> run, int seedRows = 0, IDbModel? bulkSeedModel = null)
    {
        var samples = new List<double>(Iterations);
        for (var i = 0; i <= Iterations; i++)
        {
            await ResetAsync(factory);
            if (seedRows > 0)
                await RunBatchAsync(bulkSeedModel!, factory, InsertActions(seedRows));
            var sw = Stopwatch.StartNew();
            await run();
            sw.Stop();
            if (i > 0) // first iteration is warmup
                samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        var median = samples[samples.Count / 2];
        Console.WriteLine($"| {providerName} | {scenario} | {rows:N0} | {median:F1} | {rows / median * 1000:N0} |");
    }

    private static async Task ResetAsync(IDbConnFactory factory)
    {
        await using var conn = factory.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM bench_rows";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<IDbModel> LoadModelAsync(IDbConnFactory factory, string bulkThreshold)
    {
        var loader = new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>()));
        var model = await loader.LoadAsync();
        var table = model.GetTableFromDbName("bench_rows");
        table.Metadata[MetadataKeys.Batch.MaxSize] = "1000000";
        table.Metadata[MetadataKeys.Batch.BulkThreshold] = bulkThreshold;
        return model;
    }

    private static IReadOnlyList<BatchMutationPipeline.BatchAction> InsertActions(int rows)
        => Enumerable.Range(1, rows).Select(i => new BatchMutationPipeline.BatchAction(
            MutationAction.Insert,
            new Dictionary<string, object?>
            {
                ["id"] = i,
                ["a"] = i * 3,
                ["b"] = $"row-{i}-{Guid.NewGuid():N}",
                ["c"] = 1000.50m + i,
            })).ToList();

    private static IReadOnlyList<BatchMutationPipeline.BatchAction> UpdateActions(int rows)
        => Enumerable.Range(1, rows).Select(i => new BatchMutationPipeline.BatchAction(
            MutationAction.Update,
            new Dictionary<string, object?>
            {
                ["id"] = i,
                ["a"] = i * 7,
                ["b"] = $"upd-{i}",
                ["c"] = 2000.25m + i,
            })).ToList();

    private static IReadOnlyList<BatchMutationPipeline.BatchAction> DeleteActions(int rows)
        => Enumerable.Range(1, rows).Select(i => new BatchMutationPipeline.BatchAction(
            MutationAction.Delete,
            new Dictionary<string, object?> { ["id"] = i })).ToList();

    private static async Task RunBatchAsync(
        IDbModel model, IDbConnFactory factory, IReadOnlyList<BatchMutationPipeline.BatchAction> actions)
    {
        var table = model.GetTableFromDbName("bench_rows");
        var total = await BatchMutationPipeline.ExecuteBatchAsync(table, actions, new MutationPipelineContext
        {
            Model = model,
            ConnFactory = factory,
            Transformers = new MutationTransformersWrap(),
            UserContext = new Dictionary<string, object?>(),
        });
        if (total != actions.Count)
            throw new InvalidOperationException($"expected {actions.Count} affected, got {total}");
    }

    private static string BuildInsertMutation(int rows)
    {
        var sb = new StringBuilder("mutation { bench_rows_batch(actions: [");
        for (var i = 1; i <= rows; i++)
            sb.Append($"{{ insert: {{ id: {i}, a: {i * 3}, b: \"row-{i}\", c: {1000.50m + i} }} }},");
        sb.Length -= 1;
        sb.Append("]) }");
        return sb.ToString();
    }

    private static async Task RunGraphQlAsync(IDbModel model, GraphQL.Types.ISchema schema, IDbConnFactory factory, string mutation)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IMutationTransformers>(
            services, new MutationTransformersWrap());
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IFilterTransformers>(
            services, new FilterTransformersWrap());
        await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);

        var executor = new SqlExecutionManager(model, schema);
        var result = await new GraphQL.DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.Extensions = new GraphQL.Inputs(new Dictionary<string, object?>
            {
                { "connFactory", factory },
                { "model", model },
                { "tableReaderFactory", executor },
            });
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>();
        });
        if (result.Errors is { Count: > 0 })
            throw new InvalidOperationException($"GraphQL failed: {result.Errors[0].Message}");
    }

    // ---- raw floors ----

    private static async Task SqlServerFloorAsync(System.Data.Common.DbConnection conn, int rows)
    {
        using var bulk = new SqlBulkCopy((SqlConnection)conn) { DestinationTableName = "bench_rows" };
        var table = new System.Data.DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("a", typeof(int));
        table.Columns.Add("b", typeof(string));
        table.Columns.Add("c", typeof(decimal));
        for (var i = 1; i <= rows; i++)
            table.Rows.Add(i, i * 3, $"row-{i}", 1000.50m + i);
        await bulk.WriteToServerAsync(table);
    }

    private static async Task PostgresFloorAsync(System.Data.Common.DbConnection conn, int rows)
    {
        var npgsql = (NpgsqlConnection)conn;
        await using var importer = await npgsql.BeginBinaryImportAsync(
            "COPY bench_rows (id, a, b, c) FROM STDIN (FORMAT BINARY)");
        for (var i = 1; i <= rows; i++)
        {
            await importer.StartRowAsync();
            await importer.WriteAsync(i, NpgsqlDbType.Integer);
            await importer.WriteAsync(i * 3, NpgsqlDbType.Integer);
            await importer.WriteAsync($"row-{i}", NpgsqlDbType.Varchar);
            await importer.WriteAsync(1000.50m + i, NpgsqlDbType.Numeric);
        }
        await importer.CompleteAsync();
    }

    private static async Task MySqlFloorAsync(System.Data.Common.DbConnection conn, int rows)
    {
        var bulk = new MySqlBulkCopy((MySqlConnection)conn) { DestinationTableName = "bench_rows" };
        var table = new System.Data.DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("a", typeof(int));
        table.Columns.Add("b", typeof(string));
        table.Columns.Add("c", typeof(decimal));
        for (var i = 1; i <= rows; i++)
            table.Rows.Add(i, i * 3, $"row-{i}", 1000.50m + i);
        var result = await bulk.WriteToServerAsync(table);
        if (result.RowsInserted != rows)
            throw new InvalidOperationException($"MySQL floor inserted {result.RowsInserted}/{rows}");
    }

    private static string? Env(params string[] names)
        => names.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static async Task ExecAsync(System.Data.Common.DbConnection conn, string sql)
    {
        await using (conn)
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
