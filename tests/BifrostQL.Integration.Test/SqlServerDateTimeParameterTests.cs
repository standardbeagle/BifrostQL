using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Integration.Test;

/// <summary>
/// SqlClient infers <c>datetime</c> for an untyped <see cref="DateTime"/> parameter, which
/// rounds it to 1/300 s before it leaves the client. Since SQL Server 2016 (compatibility level
/// 130) a <c>datetime</c> compared to a <c>datetime2</c> is converted exactly, so the rounded
/// value no longer equals what the column holds: <c>.123</c> stored in a <c>datetime2</c> is
/// not equal to a <c>.123</c> parameter typed <c>datetime</c>. Every equality BifrostQL builds
/// against a <c>datetime2</c> column (a filter, a key, a write predicate) then matches nothing.
/// A legacy <c>datetime</c> column must keep matching the value a client spells.
/// </summary>
public sealed class SqlServerDateTimeParameterTests : IAsyncLifetime
{
    private const string EndpointPath = "/graphql";
    private static readonly DateTime At = new DateTime(2000, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddMilliseconds(123);
    private static string? Master => Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");

    private string? _dbName;
    private IDbConnFactory _connFactory = null!;
    private PathCache<Inputs> _pathCache = null!;

    public async Task InitializeAsync()
    {
        if (Master is null) return;
        _dbName = $"bifrost_dt_{Guid.NewGuid():N}";
        await ExecOnMasterAsync($"CREATE DATABASE [{_dbName}]");
        _connFactory = new BifrostQL.SqlServer.SqlServerDbConnFactory(
            new SqlConnectionStringBuilder(Master) { InitialCatalog = _dbName }.ConnectionString);

        await ExecAsync("CREATE TABLE events (id INT PRIMARY KEY, at DATETIME2 NOT NULL, legacy DATETIME NOT NULL)");
        await ExecAsync("INSERT INTO events (id, at, legacy) VALUES (1, '2000-01-02 03:04:05.123', '2000-01-02 03:04:05.123')");
        await ExecAsync("CREATE TABLE slots (room INT NOT NULL, at DATETIME2 NOT NULL, note VARCHAR(20) NULL, PRIMARY KEY (room, at))");
        await ExecAsync("INSERT INTO slots (room, at, note) VALUES (1, '2000-01-02 03:04:05.123', NULL)");

        var model = await new DbModelLoader(_connFactory, new MetadataLoader(Array.Empty<string>())).LoadAsync();
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
        SqlConnection.ClearAllPools();
        await ExecOnMasterAsync($"ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}]");
    }

    [SkippableFact]
    public async Task EqFilter_OnADatetime2Column_FindsTheRow()
    {
        Skip.IfNot(Master is not null, "BIFROST_TEST_SQLSERVER not set");

        (await CountWhereAsync("at", At)).Should().Be(1, "the column holds exactly the value the filter names");
    }

    [SkippableFact]
    public async Task EqFilter_OnALegacyDatetimeColumn_StillFindsTheRow()
    {
        Skip.IfNot(Master is not null, "BIFROST_TEST_SQLSERVER not set");

        (await CountWhereAsync("legacy", At)).Should().Be(1, "a datetime column rounds the stored value; the comparison must round the parameter the same way");
    }

    [SkippableFact]
    public async Task UpdateByCompositeKey_WithADatetime2KeyColumn_UpdatesTheRow()
    {
        Skip.IfNot(Master is not null, "BIFROST_TEST_SQLSERVER not set");
        var writer = new MutationIntentExecutor(_pathCache, new MutationTransformersWrap { Transformers = Array.Empty<IMutationTransformer>() });

        var result = await writer.ExecuteAsync(new MutationIntent
        {
            Table = "dbo.slots",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["note"] = "booked" },
            PrimaryKey = new object?[] { 1, At },
            Endpoint = EndpointPath,
        });

        result.AffectedRows.Should().Be(1, "the key names the row exactly");
    }

    [SkippableFact]
    public async Task Insert_IntoADatetime2Column_KeepsEveryFractionalDigit()
    {
        Skip.IfNot(Master is not null, "BIFROST_TEST_SQLSERVER not set");
        var writer = new MutationIntentExecutor(_pathCache, new MutationTransformersWrap { Transformers = Array.Empty<IMutationTransformer>() });
        var precise = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Unspecified).AddTicks(1234567);

        await writer.ExecuteAsync(new MutationIntent
        {
            Table = "dbo.events",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["id"] = 2, ["at"] = precise, ["legacy"] = precise },
            Endpoint = EndpointPath,
        });

        await using var conn = _connFactory.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT at FROM events WHERE id = 2";
        ((DateTime)(await cmd.ExecuteScalarAsync())!).Should().Be(precise, "datetime2(7) stores 100 ns; nothing may round the value on the way in");
    }

    private async Task<int> CountWhereAsync(string column, DateTime value)
    {
        var model = (IDbModel)(await _pathCache.GetValueAsync(EndpointPath))["model"]!;
        var table = model.GetTableFromDbName("events");
        var query = new GqlObjectQuery
        {
            DbTable = table,
            SchemaName = table.TableSchema,
            TableName = table.DbName,
            GraphQlName = table.GraphQlName,
            Path = table.GraphQlName,
            Filter = TableFilter.FromObject(
                new Dictionary<string, object?> { [column] = new Dictionary<string, object?> { ["_eq"] = value } }, table),
        };
        query.ScalarColumns.Add(new GqlObjectColumn("id"));
        var reader = new QueryIntentExecutor(_pathCache, new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        }));
        var result = await reader.ExecuteAsync(new QueryIntent { Query = query, Endpoint = EndpointPath });
        return result.Rows.Count;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = _connFactory.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecOnMasterAsync(string sql)
    {
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
