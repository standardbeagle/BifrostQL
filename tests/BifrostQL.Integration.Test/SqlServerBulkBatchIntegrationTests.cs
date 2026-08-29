using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.SqlServer;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Integration.Test;

/// <summary>
/// End-to-end coverage for the SQL Server set-based batch fast path (#temp staging +
/// inline SQL transaction): the SAME GraphQL <c>_batch</c> mutation runs once with
/// <c>bulk-batch-threshold: 1</c> (fast path) and once with <c>0</c> (fast path disabled,
/// per-row loop), and the outcomes — returned total, final table state, error surface —
/// must be indistinguishable. Requires BIFROST_TEST_SQLSERVER.
/// </summary>
public sealed class SqlServerBulkBatchIntegrationTests : IAsyncLifetime
{
    private string? _dbName;
    private string? _masterConnString;
    private string _connString = null!;
    private SqlServerDbConnFactory _connFactory = null!;

    public async Task InitializeAsync()
    {
        _masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");
        if (_masterConnString is null)
            return;

        _dbName = $"bifrost_bulk_{Guid.NewGuid():N}";
        await using (var master = new SqlConnection(_masterConnString))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_dbName}]";
            await create.ExecuteNonQueryAsync();
        }

        _connString = new SqlConnectionStringBuilder(_masterConnString) { InitialCatalog = _dbName }.ConnectionString;
        _connFactory = new SqlServerDbConnFactory(_connString);

        await ExecuteSqlAsync("""
            CREATE TABLE orders (
                id INT NOT NULL PRIMARY KEY,
                tenant_id INT NOT NULL,
                status NVARCHAR(50) NOT NULL,
                total DECIMAL(18,2) NOT NULL
            );
            CREATE TABLE versioned (
                id INT NOT NULL PRIMARY KEY,
                status NVARCHAR(50) NOT NULL,
                version INT NOT NULL
            );
            """);
    }

    public async Task DisposeAsync()
    {
        if (_dbName is null || _masterConnString is null) return;
        try
        {
            await using var master = new SqlConnection(_masterConnString);
            await master.OpenAsync();
            await using var drop = master.CreateCommand();
            drop.CommandText = $"""
                ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_dbName}];
                """;
            await drop.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> DumpAsync(string table)
    {
        await using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} ORDER BY id";
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(string.Join("|", values));
        }
        return rows;
    }

    private async Task<IDbModel> LoadModelAsync(string bulkThreshold, string[]? metadataRules = null)
    {
        var loader = new DbModelLoader(_connFactory, new MetadataLoader(metadataRules ?? Array.Empty<string>()));
        var model = await loader.LoadAsync();
        foreach (var table in new[] { "orders", "versioned" })
            model.GetTableFromDbName(table).Metadata[MetadataKeys.Batch.BulkThreshold] = bulkThreshold;
        return model;
    }

    private async Task<ExecutionResult> ExecuteAsync(IDbModel model, string query, IDictionary<string, object?>? userContext = null)
    {
        var schema = DbSchema.FromModel(model);
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new PolicyMutationTransformer(),
                new ConcurrencyMutationTransformer(),
            },
        });
        services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        });
        await using var provider = services.BuildServiceProvider();

        var executor = new SqlExecutionManager(model, schema, BifrostQL.Core.Modules.NullQueryTransformerService.Instance);
        var extensions = new Dictionary<string, object?>
        {
            { "connFactory", _connFactory },
            { "model", model },
            { "tableReaderFactory", executor },
        };

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.Extensions = new Inputs(extensions);
            options.RequestServices = provider;
            options.UserContext = userContext ?? new Dictionary<string, object?>();
        });
    }

    private static int BatchTotal(ExecutionResult result, string field)
    {
        result.Errors.Should().BeNullOrEmpty(
            $"batch must succeed. Errors: {string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>())}");
        var json = System.Text.Json.JsonDocument.Parse(new GraphQL.SystemTextJson.GraphQLSerializer().Serialize(result));
        return json.RootElement.GetProperty("data").GetProperty(field).GetInt32();
    }

    // ---- parity: mixed insert/update/delete ----

    private const string MixedBatchMutation = """
        mutation {
            orders_batch(actions: [
                { insert: { id: 10, tenant_id: 1, status: "a", total: 1.5 } },
                { insert: { id: 11, tenant_id: 1, status: "b", total: 2.5 } },
                { update: { id: 2, tenant_id: 1, status: "paid", total: 20.0 } },
                { delete: { id: 3 } }
            ])
        }
        """;

    private async Task SeedOrdersAsync()
        => await ExecuteSqlAsync("""
            DELETE FROM orders;
            INSERT INTO orders (id, tenant_id, status, total) VALUES
                (1, 1, 'new', 10.0), (2, 1, 'new', 20.0), (3, 1, 'old', 30.0);
            """);

    [SkippableFact]
    public async Task MixedBatch_FastAndSlowPaths_ProduceIdenticalOutcomes()
    {
        Skip.If(_masterConnString is null, "BIFROST_TEST_SQLSERVER environment variable not set");

        await SeedOrdersAsync();
        var fastResult = await ExecuteAsync(await LoadModelAsync(bulkThreshold: "1"), MixedBatchMutation);
        var fastTotal = BatchTotal(fastResult, "orders_batch");
        var fastState = await DumpAsync("orders");

        await SeedOrdersAsync();
        var slowResult = await ExecuteAsync(await LoadModelAsync(bulkThreshold: "0"), MixedBatchMutation);
        var slowTotal = BatchTotal(slowResult, "orders_batch");
        var slowState = await DumpAsync("orders");

        fastTotal.Should().Be(slowTotal);
        fastState.Should().Equal(slowState);
        // And the batch actually did what it says: 2 inserts + 1 update + 1 delete.
        fastTotal.Should().Be(4);
        fastState.Should().HaveCount(4);
        fastState.Should().Contain(r => r.StartsWith("2|") && r.Contains("paid"));
        fastState.Should().NotContain(r => r.StartsWith("3|"));
    }

    // ---- parity: tenant/row-scope veto is silent and per-row ----

    private static readonly string[] PolicyRules =
    {
        "dbo.orders { policy-actions: read,create,update,delete }",
    };

    [SkippableFact]
    public async Task OutOfScopeRow_IsSilentlyUntouched_OnBothPaths()
    {
        Skip.If(_masterConnString is null, "BIFROST_TEST_SQLSERVER environment variable not set");

        var user = new Dictionary<string, object?>
        {
            ["user_id"] = "user-1",
            ["roles"] = new[] { "user" },
            ["tenant_id"] = 1,
        };
        const string mutation = """
            mutation {
                orders_batch(actions: [
                    { update: { id: 1, tenant_id: 1, status: "mine", total: 10.0 } },
                    { update: { id: 2, tenant_id: 2, status: "theirs", total: 20.0 } }
                ])
            }
            """;

        async Task<(int Total, List<string> State)> RunAsync(string threshold)
        {
            await ExecuteSqlAsync("""
                DELETE FROM orders;
                INSERT INTO orders (id, tenant_id, status, total) VALUES
                    (1, 1, 'new', 10.0), (2, 2, 'new', 20.0);
                """);
            var model = await LoadModelAsync(threshold, PolicyRules);
            model.GetTableFromDbName("orders").Metadata[MetadataKeys.Policy.RowScope] = "tenant_id = {tenant_id}";
            var result = await ExecuteAsync(model, mutation, user);
            return (BatchTotal(result, "orders_batch"), await DumpAsync("orders"));
        }

        var fast = await RunAsync("1");
        var slow = await RunAsync("0");

        fast.Total.Should().Be(slow.Total);
        fast.State.Should().Equal(slow.State);
        // The out-of-tenant row is a SILENT zero-row no-op; the in-scope row lands.
        fast.Total.Should().Be(1);
        fast.State.Should().Contain(r => r.StartsWith("1|") && r.Contains("mine"));
        fast.State.Should().Contain(r => r.StartsWith("2|") && r.Contains("new"));
    }

    // ---- parity: stale concurrency token rolls back the WHOLE batch ----

    private static readonly string[] ConcurrencyRules =
    {
        "dbo.versioned { concurrency-token: version }",
    };

    [SkippableFact]
    public async Task StaleConcurrencyToken_RollsBackWholeBatch_OnBothPaths()
    {
        Skip.If(_masterConnString is null, "BIFROST_TEST_SQLSERVER environment variable not set");

        // Both updates carry version 1 (homogeneous filter → the fast path engages),
        // but row 2 was bumped out-of-band, so its guarded update matches nothing.
        const string mutation = """
            mutation {
                versioned_batch(actions: [
                    { update: { id: 1, status: "changed", version: 1 } },
                    { update: { id: 2, status: "changed", version: 1 } }
                ])
            }
            """;

        async Task<(string? Error, List<string> State)> RunAsync(string threshold)
        {
            await ExecuteSqlAsync("""
                DELETE FROM versioned;
                INSERT INTO versioned (id, status, version) VALUES (1, 'new', 1), (2, 'new', 5);
                """);
            var model = await LoadModelAsync(threshold, ConcurrencyRules);
            var result = await ExecuteAsync(model, mutation);
            var error = result.Errors?.FirstOrDefault()?.Message;
            return (error, await DumpAsync("versioned"));
        }

        var fast = await RunAsync("1");
        var slow = await RunAsync("0");

        fast.Error.Should().NotBeNull("a stale token must CONFLICT the batch");
        fast.Error.Should().Be(slow.Error);
        fast.Error.Should().Contain("concurrency token no longer matches");
        // Atomicity: row 1's otherwise-valid update rolled back with the batch.
        fast.State.Should().Equal(slow.State);
        fast.State.Should().Contain("1|new|1").And.Contain("2|new|5");
    }
}
