using System.Text.Json.Nodes;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Cdc;
using BifrostQL.Core.Modules.History;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// The history trail's <c>entity_id</c> and the outbox payload's key name one row the same way
/// whichever surface wrote it: a typed key from the host API, or the positional
/// <c>_primaryKey</c> strings a client sends. Both writers share
/// <c>KeyValueCanonicalizer</c>, so these facts drive both through the real
/// <see cref="MutationIntentExecutor"/> with the production in-transaction hooks.
/// </summary>
public sealed class RecordedKeyCanonicalFormTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_recorded_key_canonical_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;
    private PathCache<Inputs> _pathCache = null!;
    private MutationIntentExecutor _writer = null!;
    private IDbModel? _model;

    private static readonly string[] Rules =
    {
        "main.readings { history: enabled; history-columns: value }",
        "main.samples { history: enabled; history-columns: value }",
        "main.widgets { emit-events: insert,update,delete; event-payload: keys }",
        ":root { history-table: main.__history; outbox-table: main.__outbox }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        foreach (var table in new[] { "readings", "samples", "widgets", "__history", "__outbox" })
            await Exec($"DROP TABLE IF EXISTS {table}");

        // The SQL Server shape from the finding: a composite key with a datetimeoffset part.
        await Exec(
            """
            CREATE TABLE readings (
                device_id   INTEGER NOT NULL,
                recorded_at DATETIMEOFFSET NOT NULL,
                value       INTEGER NULL,
                PRIMARY KEY (device_id, recorded_at)
            )
            """);
        await Exec(
            """
            CREATE TABLE samples (
                k     INTEGER NOT NULL,
                at    DATETIME NOT NULL,
                value INTEGER NULL,
                PRIMARY KEY (k, at)
            )
            """);
        await Exec("CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NULL)");
        await Exec(
            """
            CREATE TABLE __history (
                id              INTEGER PRIMARY KEY,
                entity          TEXT NOT NULL,
                entity_id       TEXT NOT NULL,
                op              TEXT NOT NULL,
                actor           TEXT NULL,
                changed_at      TEXT NOT NULL,
                before          TEXT NULL,
                after           TEXT NULL,
                changed_columns TEXT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE __outbox (
                id            INTEGER PRIMARY KEY,
                aggregate     TEXT NOT NULL,
                op            TEXT NOT NULL,
                payload       TEXT NOT NULL,
                tenant        TEXT NULL,
                created_at    TEXT NOT NULL DEFAULT (datetime('now')),
                dispatched_at TEXT NULL,
                attempts      INTEGER NOT NULL DEFAULT 0,
                dead          INTEGER NOT NULL DEFAULT 0
            )
            """);

        _pathCache = new PathCache<Inputs>();
        _pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
            _model = model;
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["dbSchema"] = DbSchema.FromModel(model),
                ["connFactory"] = factory,
            });
        });

        var services = new ServiceCollection();
        services.AddSingleton<HistoryMutationHook>();
        services.AddSingleton<IBeforeCommitMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<IInTransactionMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<IInTransactionMutationHook, OutboxMutationHook>();
        services.AddSingleton(sp => new BeforeCommitMutationHooks(sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        services.AddSingleton(sp => new InTransactionMutationHooks(sp.GetServices<IInTransactionMutationHook>().ToArray()));

        _writer = new MutationIntentExecutor(_pathCache, new MutationTransformersWrap
        {
            Transformers = Array.Empty<IMutationTransformer>(),
        }, services.BuildServiceProvider());
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task<MutationIntentResult> WriteAsync(
        string table, MutationIntentAction action, Dictionary<string, object?> data, IReadOnlyList<object?>? primaryKey = null)
        => _writer.ExecuteAsync(new MutationIntent
        {
            Table = table,
            Action = action,
            Data = data,
            PrimaryKey = primaryKey,
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

    private async Task<List<(string EntityId, string Op)>> TrailAsync(string entity)
    {
        var rows = new List<(string, string)>();
        await using var cmd = new SqliteCommand(
            "SELECT entity_id, op FROM __history WHERE entity = @e ORDER BY id", _keepAlive);
        cmd.Parameters.AddWithValue("@e", entity);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    [Fact]
    public async Task DateTimeOffsetKey_TypedInsertAndStringPrimaryKeyUpdate_ShareOneEntityId()
    {
        var recordedAt = new DateTimeOffset(2026, 3, 1, 12, 0, 0, 100, TimeSpan.FromHours(2));
        await WriteAsync("readings", MutationIntentAction.Insert,
            new() { ["device_id"] = 7, ["recorded_at"] = recordedAt, ["value"] = 1 });

        // The client addresses the same row by the text SQLite stored, plus a padded device id.
        var stored = await StoredRecordedAtAsync();
        await WriteAsync("readings", MutationIntentAction.Update,
            new() { ["value"] = 2 }, primaryKey: new object?[] { "07", stored });

        var trail = await TrailAsync("main.readings");
        trail.Select(r => r.Op).Should().Equal("insert", "update");
        trail.Select(r => r.EntityId).Distinct().Should().ContainSingle()
            // System.Text.Json's default encoder escapes '+' in the serialized entity_id.
            .Which.Should().Be("{\"device_id\":7,\"recorded_at\":\"2026-03-01T10:00:00.1000000\\u002B00:00\"}",
                "the key is the column's canonical value: an integer and the UTC instant in round-trip form");
    }

    private async Task<string> StoredRecordedAtAsync()
    {
        await using var cmd = new SqliteCommand("SELECT recorded_at FROM readings", _keepAlive);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task DateTimeKeys_DifferingOnlyInFractionalSeconds_RecordDistinctEntityIds()
    {
        var early = new DateTime(2026, 3, 1, 12, 0, 0, 100);
        var late = new DateTime(2026, 3, 1, 12, 0, 0, 900);
        await WriteAsync("samples", MutationIntentAction.Insert, new() { ["k"] = 1, ["at"] = early, ["value"] = 1 });
        await WriteAsync("samples", MutationIntentAction.Insert, new() { ["k"] = 1, ["at"] = late, ["value"] = 1 });

        var trail = await TrailAsync("main.samples");
        trail.Select(r => r.EntityId).Should().Equal(
            "{\"k\":1,\"at\":\"2026-03-01T12:00:00.1000000\"}",
            "{\"k\":1,\"at\":\"2026-03-01T12:00:00.9000000\"}");
    }

    [Fact]
    public async Task OutboxKey_TypedInsertAndPaddedStringPrimaryKeyUpdate_ShareTypeAndSubject()
    {
        await WriteAsync("widgets", MutationIntentAction.Insert, new() { ["id"] = 5, ["name"] = "a" });
        await WriteAsync("widgets", MutationIntentAction.Update, new() { ["name"] = "b" }, primaryKey: new object?[] { "05" });
        await WriteAsync("widgets", MutationIntentAction.Delete, new(), primaryKey: new object?[] { "5.0" });

        var payloads = new List<string>();
        await using (var cmd = new SqliteCommand("SELECT payload FROM __outbox ORDER BY id", _keepAlive))
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                payloads.Add(reader.GetString(0));
        payloads.Should().Equal("{\"id\":5}", "{\"id\":5}", "{\"id\":5}");

        var sink = new SubjectSink();
        await OutboxDispatcher.DrainOnceAsync(
            _model!, new SqliteDbConnFactory(ConnString), sink, logger: null, batchSize: 100, maxAttempts: 5, CancellationToken.None);
        sink.Subjects.Should().Equal("5", "5", "5");
    }

    private sealed class SubjectSink : IEventSink
    {
        public List<string> Subjects { get; } = new();

        public ValueTask<EventDeliveryResult> DeliverAsync(
            JsonObject envelope, string idempotencyKey, CancellationToken cancellationToken)
        {
            Subjects.Add(envelope["subject"]!.ToString());
            return ValueTask.FromResult(EventDeliveryResult.Delivered);
        }
    }
}
