using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.History;
using BifrostQL.Core.Modules.Retention;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using FluentAssertions;
using GraphQL;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BifrostQL.Integration.Test;

/// <summary>
/// A retention erasure deletes the row and purges its history trail by exact equality on
/// <c>entity_id</c>, the serialized key. On PostgreSQL the documented trail column is
/// <c>JSONB</c>, which PostgreSQL will not compare to a text parameter, so the purge must compare
/// in the column's own type or no tracked row can ever be erased.
/// </summary>
public sealed class PostgresHistoryErasureTests : IAsyncLifetime
{
    private const string EndpointPath = "/graphql";
    private const string Person = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Inserted = "12345678-9abc-def0-1234-56789abcdef0";
    private static readonly DateTime Now = new(2050, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Rules =
    {
        "public.people { ttl: 1d after expires_at; history: enabled }",
        ":root { history-table: public.__history }",
    };

    private static string? Master => Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");

    private string? _dbName;
    private string _connectionString = null!;
    private IDbConnFactory _connFactory = null!;
    private PathCache<Inputs> _pathCache = null!;

    public async Task InitializeAsync()
    {
        if (Master is null) return;
        _dbName = $"bifrost_hist_key_{Guid.NewGuid():N}";
        await ExecAsync(Master, $"CREATE DATABASE \"{_dbName}\"");
        _connectionString = new NpgsqlConnectionStringBuilder(Master) { Database = _dbName }.ConnectionString;
        await ExecAsync(_connectionString,
            $"""
            CREATE TABLE people (id UUID PRIMARY KEY, name TEXT NOT NULL, expires_at TIMESTAMP NOT NULL);
            INSERT INTO people (id, name, expires_at) VALUES ('{Person}', 'alice', '2000-01-01');
            CREATE TABLE __history (
                id BIGSERIAL PRIMARY KEY, entity TEXT NOT NULL, entity_id JSONB NOT NULL, op TEXT NOT NULL,
                actor TEXT NULL, changed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                before JSONB NULL, after JSONB NULL, changed_columns JSONB NULL);
            """);
        _connFactory = new BifrostQL.Ngsql.PostgresDbConnFactory(_connectionString);
        var model = await new DbModelLoader(_connFactory, new MetadataLoader(Rules)).LoadAsync();
        _pathCache = new PathCache<Inputs>();
        _pathCache.AddLoader(EndpointPath, () => Task.FromResult(new Inputs(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["dbSchema"] = DbSchema.FromModel(model),
            ["connFactory"] = _connFactory,
        })));
    }

    public async Task DisposeAsync()
    {
        if (_dbName is null) return;
        NpgsqlConnection.ClearAllPools();
        await ExecAsync(Master!, $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    [SkippableFact]
    public async Task ATrailInAJsonbColumn_IsErasedWithTheRow()
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        await Writer().ExecuteAsync(new MutationIntent
        {
            Table = "public.people",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["name"] = "alice-renamed" },
            PrimaryKey = new object?[] { Person },
            Endpoint = EndpointPath,
        });

        await PurgeAsync();

        (await ScalarAsync("SELECT COUNT(*) FROM people")).Should().Be(0, "the row expired");
        (await ScalarAsync("SELECT COUNT(*) FROM __history WHERE op <> 'erase'"))
            .Should().Be(0, "the erasure purges the trail of the row it deleted");
        (await ScalarAsync("SELECT COUNT(*) FROM __history WHERE op = 'erase'")).Should().Be(1);
    }

    private MutationIntentExecutor Writer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<HistoryMutationHook>();
        services.AddSingleton<IBeforeCommitMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<IInTransactionMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton(sp => new BeforeCommitMutationHooks(sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        services.AddSingleton(sp => new InTransactionMutationHooks(sp.GetServices<IInTransactionMutationHook>().ToArray()));
        return new MutationIntentExecutor(_pathCache, new MutationTransformersWrap
        {
            Transformers = Array.Empty<IMutationTransformer>(),
        }, services.BuildServiceProvider());
    }

    private async Task PurgeAsync()
    {
        var reader = new QueryIntentExecutor(_pathCache, new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        }));
        var engine = new RetentionPurgeEngine(reader, Writer(), _pathCache, clock: () => Now);
        var model = await reader.GetModelAsync(EndpointPath);
        var outcome = await engine.PurgeOnceAsync(model, _connFactory, EndpointPath, CancellationToken.None);
        outcome.RowsPurged.Should().Be(1);
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
