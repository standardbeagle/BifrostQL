using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Integration.Test;

/// <summary>
/// End-to-end parity coverage for the staged bulk batch fast path on PostgreSQL and MySQL
/// (temp-table staging + set-based DML inside a SQL-level BEGIN/COMMIT — see
/// <c>StagedBulkBatchExecutorBase</c>), mirroring <see cref="SqlServerBulkBatchIntegrationTests"/>:
/// the SAME GraphQL <c>_batch</c> mutation runs once with <c>bulk-batch-threshold: 1</c>
/// (fast path) and once with <c>0</c> (per-row loop), and the outcomes — returned total,
/// final table state, error surface — must be indistinguishable.
/// </summary>
public abstract class StagedBulkBatchIntegrationTestBase : IAsyncLifetime
{
    protected abstract string? MasterConnectionString { get; }
    protected abstract Task CreateDatabaseAsync(string dbName);
    protected abstract Task DropDatabaseAsync(string dbName);
    protected abstract IDbConnFactory CreateConnFactory(string dbName);
    protected abstract string SchemaDdl { get; }

    private string? _dbName;
    private IDbConnFactory _connFactory = null!;

    protected bool Available => MasterConnectionString is not null;

    public async Task InitializeAsync()
    {
        if (!Available) return;
        _dbName = $"bifrost_bulk_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(_dbName);
        _connFactory = CreateConnFactory(_dbName);
        await ExecuteSqlAsync(SchemaDdl);
    }

    public async Task DisposeAsync()
    {
        if (_dbName is null) return;
        try { await DropDatabaseAsync(_dbName); }
        catch { /* best effort cleanup */ }
    }

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var conn = _connFactory.GetConnection();
        await conn.OpenAsync();
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = statement + ";";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<List<string>> DumpAsync(string table)
    {
        await using var conn = _connFactory.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} ORDER BY id";
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(string.Join("|", values.Select(v => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", v))));
        }
        return rows;
    }

    private async Task<IDbModel> LoadModelAsync(string bulkThreshold, Action<IDbModel>? configure = null)
    {
        var loader = new DbModelLoader(_connFactory, new MetadataLoader(Array.Empty<string>()));
        var model = await loader.LoadAsync();
        foreach (var table in new[] { "orders", "versioned" })
            model.GetTableFromDbName(table).Metadata[MetadataKeys.Batch.BulkThreshold] = bulkThreshold;
        configure?.Invoke(model);
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

        var executor = new SqlExecutionManager(model, schema);
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
            INSERT INTO orders (id, tenant_id, status, total) VALUES (1, 1, 'new', 10.0), (2, 1, 'new', 20.0), (3, 1, 'old', 30.0)
            """);

    [SkippableFact]
    public async Task MixedBatch_FastAndSlowPaths_ProduceIdenticalOutcomes()
    {
        Skip.If(!Available, "test database environment variable not set");

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
        fastTotal.Should().Be(4);
        fastState.Should().HaveCount(4);
        fastState.Should().Contain(r => r.StartsWith("2|") && r.Contains("paid"));
        fastState.Should().NotContain(r => r.StartsWith("3|"));
    }

    // ---- parity: tenant/row-scope veto is silent and per-row ----

    [SkippableFact]
    public async Task OutOfScopeRow_IsSilentlyUntouched_OnBothPaths()
    {
        Skip.If(!Available, "test database environment variable not set");

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
                INSERT INTO orders (id, tenant_id, status, total) VALUES (1, 1, 'new', 10.0), (2, 2, 'new', 20.0)
                """);
            var model = await LoadModelAsync(threshold, m =>
            {
                var orders = m.GetTableFromDbName("orders");
                orders.Metadata[MetadataKeys.Policy.Actions] = "read,create,update,delete";
                orders.Metadata[MetadataKeys.Policy.RowScope] = "tenant_id = {tenant_id}";
            });
            var result = await ExecuteAsync(model, mutation, user);
            return (BatchTotal(result, "orders_batch"), await DumpAsync("orders"));
        }

        var fast = await RunAsync("1");
        var slow = await RunAsync("0");

        fast.Total.Should().Be(slow.Total);
        fast.State.Should().Equal(slow.State);
        fast.Total.Should().Be(1);
        fast.State.Should().Contain(r => r.StartsWith("1|") && r.Contains("mine"));
        fast.State.Should().Contain(r => r.StartsWith("2|") && r.Contains("new"));
    }

    // ---- parity: stale concurrency token rolls back the WHOLE batch ----

    [SkippableFact]
    public async Task StaleConcurrencyToken_RollsBackWholeBatch_OnBothPaths()
    {
        Skip.If(!Available, "test database environment variable not set");

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
                INSERT INTO versioned (id, status, version) VALUES (1, 'new', 1), (2, 'new', 5)
                """);
            var model = await LoadModelAsync(threshold, m =>
                m.GetTableFromDbName("versioned").Metadata[MetadataKeys.Concurrency.Token] = "version");
            var result = await ExecuteAsync(model, mutation);
            var error = result.Errors?.FirstOrDefault()?.Message;
            return (error, await DumpAsync("versioned"));
        }

        var fast = await RunAsync("1");
        var slow = await RunAsync("0");

        fast.Error.Should().NotBeNull("a stale token must CONFLICT the batch");
        fast.Error.Should().Be(slow.Error);
        fast.Error.Should().Contain("concurrency token no longer matches");
        fast.State.Should().Equal(slow.State);
        fast.State.Should().Contain("1|new|1").And.Contain("2|new|5");
    }
}

public sealed class PostgresBulkBatchIntegrationTests : StagedBulkBatchIntegrationTestBase
{
    protected override string? MasterConnectionString => Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");

    protected override async Task CreateDatabaseAsync(string dbName)
    {
        await using var conn = new Npgsql.NpgsqlConnection(MasterConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task DropDatabaseAsync(string dbName)
    {
        await using var conn = new Npgsql.NpgsqlConnection(MasterConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE \"{dbName}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override IDbConnFactory CreateConnFactory(string dbName)
        => new BifrostQL.Ngsql.PostgresDbConnFactory(
            new Npgsql.NpgsqlConnectionStringBuilder(MasterConnectionString!) { Database = dbName }.ConnectionString);

    protected override string SchemaDdl => """
        CREATE TABLE orders (
            id INT NOT NULL PRIMARY KEY,
            tenant_id INT NOT NULL,
            status VARCHAR(50) NOT NULL,
            total NUMERIC(18,2) NOT NULL
        );
        CREATE TABLE versioned (
            id INT NOT NULL PRIMARY KEY,
            status VARCHAR(50) NOT NULL,
            version INT NOT NULL
        )
        """;
}

public sealed class MySqlBulkBatchIntegrationTests : StagedBulkBatchIntegrationTestBase
{
    protected override string? MasterConnectionString => Environment.GetEnvironmentVariable("BIFROST_TEST_MYSQL");

    protected override async Task CreateDatabaseAsync(string dbName)
    {
        await using var conn = new MySqlConnector.MySqlConnection(MasterConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE `{dbName}`";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task DropDatabaseAsync(string dbName)
    {
        await using var conn = new MySqlConnector.MySqlConnection(MasterConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE `{dbName}`";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override IDbConnFactory CreateConnFactory(string dbName)
        => new BifrostQL.MySql.MySqlDbConnFactory(
            new MySqlConnector.MySqlConnectionStringBuilder(MasterConnectionString!) { Database = dbName }.ConnectionString);

    protected override string SchemaDdl => """
        CREATE TABLE orders (
            id INT NOT NULL PRIMARY KEY,
            tenant_id INT NOT NULL,
            status VARCHAR(50) NOT NULL,
            total DECIMAL(18,2) NOT NULL
        );
        CREATE TABLE versioned (
            id INT NOT NULL PRIMARY KEY,
            status VARCHAR(50) NOT NULL,
            version INT NOT NULL
        )
        """;
}
