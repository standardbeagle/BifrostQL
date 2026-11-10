using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Retention;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Integration.Test;

/// <summary>
/// A retention delete matches its row only while the anchor still holds the value the expiry
/// read returned (the read and the delete run in separate transactions, so a row touched in
/// between must survive). That makes the purge depend on every engine reading a temporal value
/// back in a form that compares EQUAL when bound as a parameter. A type whose round trip loses
/// or reshapes the value would leave every expired row in place, silently, so each dialect
/// sweeps one table per temporal anchor type it supports, with fractional seconds the type
/// either keeps or rounds.
/// </summary>
public abstract class RetentionAnchorRoundTripTestBase : IAsyncLifetime
{
    private const string EndpointPath = "/graphql";
    private static readonly DateTime Now = new(2050, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Expired = "2000-01-02 03:04:05.1234567";
    private const string ExpiredDate = "2000-01-02";

    private string? _dbName;

    protected IDbConnFactory ConnFactory { get; private set; } = null!;

    protected abstract bool Available { get; }
    protected abstract string TableSchema { get; }
    protected abstract string KeyType { get; }

    /// <summary>The engine's temporal column types, each with the literal its rows are stamped with.</summary>
    protected abstract IReadOnlyList<(string Type, string Literal)> AnchorTypes { get; }

    protected abstract Task CreateDatabaseAsync(string dbName);
    protected abstract Task DropDatabaseAsync(string dbName);
    protected abstract IDbConnFactory CreateConnFactory(string dbName);

    protected static string ExpiredTimestamp => Expired;
    protected static string ExpiredDay => ExpiredDate;

    public async Task InitializeAsync()
    {
        if (!Available) return;
        _dbName = $"bifrost_ret_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(_dbName);
        ConnFactory = CreateConnFactory(_dbName);
    }

    public async Task DisposeAsync()
    {
        if (_dbName is null) return;
        await DropDatabaseAsync(_dbName);
    }

    [SkippableFact]
    public async Task Ttl_ExpiresEveryRow_WhateverTheAnchorType()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        var rules = new List<string>();
        for (var i = 0; i < AnchorTypes.Count; i++)
        {
            var (type, literal) = AnchorTypes[i];
            await ExecAsync($"CREATE TABLE ttl_{i} (id {KeyType} PRIMARY KEY, anchor {type} NOT NULL)");
            await ExecAsync($"INSERT INTO ttl_{i} (id, anchor) VALUES (1, '{literal}'), (2, '{literal}')");
            rules.Add($"{TableSchema}.ttl_{i} {{ ttl: 1d after anchor }}");
        }

        var outcome = await PurgeAsync(rules);

        for (var i = 0; i < AnchorTypes.Count; i++)
            (await CountAsync($"ttl_{i}")).Should().Be(0, $"both expired rows of the {AnchorTypes[i].Type} table are purged");
        outcome.RowsPurged.Should().Be(2 * AnchorTypes.Count);
    }

    [SkippableFact]
    public async Task Retain_HardPurgesEveryRow_WhateverTheSoftDeleteColumnType()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        var rules = new List<string>();
        for (var i = 0; i < AnchorTypes.Count; i++)
        {
            var (type, literal) = AnchorTypes[i];
            await ExecAsync($"CREATE TABLE retain_{i} (id {KeyType} PRIMARY KEY, deleted_at {type} NULL)");
            await ExecAsync($"INSERT INTO retain_{i} (id, deleted_at) VALUES (1, '{literal}'), (2, '{literal}')");
            rules.Add($"{TableSchema}.retain_{i} {{ soft-delete: deleted_at; soft-delete-hard-role: purger; retain: 30d }}");
        }

        var outcome = await PurgeAsync(rules);

        for (var i = 0; i < AnchorTypes.Count; i++)
            (await CountAsync($"retain_{i}")).Should().Be(0, $"both aged soft-deleted rows of the {AnchorTypes[i].Type} table are purged");
        outcome.RowsPurged.Should().Be(2 * AnchorTypes.Count);
    }

    private async Task<RetentionPurgeOutcome> PurgeAsync(IReadOnlyList<string> rules)
    {
        var model = await new DbModelLoader(ConnFactory, new MetadataLoader(rules.ToArray())).LoadAsync();
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, () => Task.FromResult(new Inputs(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["dbSchema"] = DbSchema.FromModel(model),
            ["connFactory"] = ConnFactory,
        })));
        var reader = new QueryIntentExecutor(pathCache, new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new SoftDeleteFilterTransformer() },
        }));
        var writer = new MutationIntentExecutor(pathCache, new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new SoftDeleteMutationTransformer() },
        });
        var engine = new RetentionPurgeEngine(reader, writer, pathCache, clock: () => Now);
        return await engine.PurgeOnceAsync(model, ConnFactory, EndpointPath, CancellationToken.None);
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = ConnFactory.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string table)
    {
        await using var conn = ConnFactory.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}

public sealed class SqliteRetentionAnchorRoundTripTests : RetentionAnchorRoundTripTestBase
{
    private SqliteConnection? _keepAlive;

    protected override bool Available => true;
    protected override string TableSchema => "main";
    protected override string KeyType => "INTEGER";

    protected override IReadOnlyList<(string Type, string Literal)> AnchorTypes => new[]
    {
        ("DATETIME", ExpiredTimestamp),
        ("TIMESTAMP", ExpiredTimestamp),
        ("DATE", ExpiredDay),
    };

    private static string ConnectionString(string dbName) => $"Data Source={dbName};Mode=Memory;Cache=Shared";

    protected override async Task CreateDatabaseAsync(string dbName)
    {
        _keepAlive = new SqliteConnection(ConnectionString(dbName));
        await _keepAlive.OpenAsync();
    }

    protected override async Task DropDatabaseAsync(string dbName)
    {
        if (_keepAlive is not null)
            await _keepAlive.DisposeAsync();
    }

    protected override IDbConnFactory CreateConnFactory(string dbName) => new SqliteDbConnFactory(ConnectionString(dbName));
}

public sealed class PostgresRetentionAnchorRoundTripTests : RetentionAnchorRoundTripTestBase
{
    private static string? Master => Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");

    protected override bool Available => Master is not null;
    protected override string TableSchema => "public";
    protected override string KeyType => "INT";

    protected override IReadOnlyList<(string Type, string Literal)> AnchorTypes => new[]
    {
        ("TIMESTAMP", ExpiredTimestamp),
        ("TIMESTAMP(0)", ExpiredTimestamp),
        ("TIMESTAMPTZ", ExpiredTimestamp + "+05:30"),
        ("DATE", ExpiredDay),
    };

    protected override async Task CreateDatabaseAsync(string dbName)
    {
        await using var conn = new Npgsql.NpgsqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task DropDatabaseAsync(string dbName)
    {
        await using var conn = new Npgsql.NpgsqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE \"{dbName}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override IDbConnFactory CreateConnFactory(string dbName)
        => new BifrostQL.Ngsql.PostgresDbConnFactory(
            new Npgsql.NpgsqlConnectionStringBuilder(Master!) { Database = dbName }.ConnectionString);
}

public sealed class MySqlRetentionAnchorRoundTripTests : RetentionAnchorRoundTripTestBase
{
    private static string? Master => Environment.GetEnvironmentVariable("BIFROST_TEST_MYSQL");
    private string? _dbName;

    protected override bool Available => Master is not null;
    protected override string TableSchema => _dbName!;
    protected override string KeyType => "INT";

    protected override IReadOnlyList<(string Type, string Literal)> AnchorTypes => new[]
    {
        ("DATETIME", ExpiredTimestamp),
        ("DATETIME(6)", ExpiredTimestamp),
        ("TIMESTAMP(3)", ExpiredTimestamp),
        ("DATE", ExpiredDay),
    };

    protected override async Task CreateDatabaseAsync(string dbName)
    {
        _dbName = dbName;
        await using var conn = new MySqlConnector.MySqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE `{dbName}`";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task DropDatabaseAsync(string dbName)
    {
        await using (var pooled = new MySqlConnector.MySqlConnection(ConnectionStringFor(dbName)))
            await MySqlConnector.MySqlConnection.ClearPoolAsync(pooled);
        await using var conn = new MySqlConnector.MySqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE `{dbName}`";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override IDbConnFactory CreateConnFactory(string dbName)
        => new BifrostQL.MySql.MySqlDbConnFactory(ConnectionStringFor(dbName));

    private static string ConnectionStringFor(string dbName)
        => new MySqlConnector.MySqlConnectionStringBuilder(Master!) { Database = dbName }.ConnectionString;
}

public sealed class SqlServerRetentionAnchorRoundTripTests : RetentionAnchorRoundTripTestBase
{
    private static string? Master => Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");

    protected override bool Available => Master is not null;
    protected override string TableSchema => "dbo";
    protected override string KeyType => "INT";

    protected override IReadOnlyList<(string Type, string Literal)> AnchorTypes => new[]
    {
        ("DATETIME", "2000-01-02 03:04:05.123"),
        ("SMALLDATETIME", "2000-01-02 03:04:05"),
        ("DATETIME2", ExpiredTimestamp),
        ("DATETIME2(0)", ExpiredTimestamp),
        ("DATETIMEOFFSET", ExpiredTimestamp + " +05:30"),
        ("DATE", ExpiredDay),
    };

    protected override async Task CreateDatabaseAsync(string dbName)
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{dbName}]";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task DropDatabaseAsync(string dbName)
    {
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override IDbConnFactory CreateConnFactory(string dbName)
        => new BifrostQL.SqlServer.SqlServerDbConnFactory(
            new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(Master!) { InitialCatalog = dbName }.ConnectionString);
}
