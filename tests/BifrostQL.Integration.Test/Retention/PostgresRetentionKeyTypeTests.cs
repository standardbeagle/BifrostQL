using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Retention;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using FluentAssertions;
using GraphQL;
using Npgsql;

namespace BifrostQL.Integration.Test;

/// <summary>
/// The retention purge hard-deletes an expired row by its key through the keyed-write
/// pipeline. The key comes back from the read as the client sends keys, so on PostgreSQL a
/// <c>uuid</c> key arrives as text, and a predicate that compares it without a cast fails with
/// 42883 <c>operator does not exist: uuid = text</c>. That is the agency shape: a hidden
/// <c>sessions</c> table keyed by <c>uuid</c> whose expired rows were never removed. A
/// <c>bigint</c> and a <c>text</c> key run through the same sweep so the cast stays limited to
/// the values that need it.
/// </summary>
public sealed class PostgresRetentionKeyTypeTests : IAsyncLifetime
{
    private const string EndpointPath = "/graphql";
    private const string ExpiredUuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string LiveUuid = "12345678-9abc-def0-1234-56789abcdef0";
    private static readonly DateTime Now = new(2050, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Rules =
    {
        "public.sessions { visibility: hidden; ttl: 1d after expires_at }",
        "public.counters { ttl: 1d after expires_at }",
        "public.codes { ttl: 1d after expires_at }",
    };

    private static string? Master => Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");

    private string? _dbName;
    private string _connectionString = null!;
    private IDbConnFactory _connFactory = null!;
    private PathCache<Inputs> _pathCache = null!;

    public async Task InitializeAsync()
    {
        if (Master is null) return;
        _dbName = $"bifrost_ret_key_{Guid.NewGuid():N}";
        await ExecAsync(Master, $"CREATE DATABASE \"{_dbName}\"");
        _connectionString = new NpgsqlConnectionStringBuilder(Master) { Database = _dbName }.ConnectionString;
        await ExecAsync(_connectionString,
            $"""
            CREATE TABLE sessions (id UUID PRIMARY KEY, expires_at TIMESTAMP NOT NULL);
            INSERT INTO sessions (id, expires_at) VALUES ('{ExpiredUuid}', '2000-01-01'), ('{LiveUuid}', '2100-01-01');
            CREATE TABLE counters (id BIGINT PRIMARY KEY, expires_at TIMESTAMP NOT NULL);
            INSERT INTO counters (id, expires_at) VALUES (9000000001, '2000-01-01'), (9000000002, '2100-01-01');
            CREATE TABLE codes (id TEXT PRIMARY KEY, expires_at TIMESTAMP NOT NULL);
            INSERT INTO codes (id, expires_at) VALUES ('old', '2000-01-01'), ('new', '2100-01-01');
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
    public async Task PurgeOnce_UuidBigintAndTextKeys_DeletesEachExpiredRow_KeepsEachLiveRow()
    {
        Skip.If(Master is null, "BIFROST_TEST_POSTGRES environment variable not set");

        var reader = new QueryIntentExecutor(_pathCache, new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        }));
        var writer = new MutationIntentExecutor(_pathCache, new MutationTransformersWrap
        {
            Transformers = Array.Empty<IMutationTransformer>(),
        });
        var engine = new RetentionPurgeEngine(reader, writer, _pathCache, clock: () => Now);
        var model = await reader.GetModelAsync(EndpointPath);

        var outcome = await engine.PurgeOnceAsync(model, _connFactory, EndpointPath, CancellationToken.None);

        outcome.TablesSwept.Should().Be(3);
        outcome.RowsPurged.Should().Be(3, "one expired row per table");
        (await ScalarAsync($"SELECT COUNT(*) FROM sessions WHERE id = '{ExpiredUuid}'")).Should().Be(0, "the expired uuid-keyed session is purged");
        (await ScalarAsync($"SELECT COUNT(*) FROM sessions WHERE id = '{LiveUuid}'")).Should().Be(1, "the live session is kept");
        (await ScalarAsync("SELECT COUNT(*) FROM counters WHERE id = 9000000001")).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM counters WHERE id = 9000000002")).Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM codes WHERE id = 'old'")).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM codes WHERE id = 'new'")).Should().Be(1);
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
