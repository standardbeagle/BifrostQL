using System.Text.Json.Nodes;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Cdc;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// End-to-end proof of the CDC outbox dispatcher drain pass (slice 4a). Outbox rows
/// are seeded directly; a fake <see cref="IEventSink"/> drives the two branches the
/// drain must get right:
/// <list type="bullet">
///   <item>Delivered → the row is stamped <c>dispatched_at</c> and its <c>attempts</c> is left alone.</item>
///   <item>TransientFailure → <c>attempts</c> is incremented and <c>dispatched_at</c> stays null.</item>
/// </list>
/// The drain also selects only undelivered, non-dead rows in monotonic id order and
/// stops the pass at the first transient failure so a later row is not delivered ahead
/// of a still-pending earlier one.
/// </summary>
public sealed class OutboxDispatcherTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_outbox_dispatcher_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;
    private SqliteDbConnFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS widgets");
        await Exec("DROP TABLE IF EXISTS __outbox");
        await Exec(
            """
            CREATE TABLE widgets (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL
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

        _factory = new SqliteDbConnFactory(ConnString);
        _model = await new DbModelLoader(_factory, new MetadataLoader(new[]
        {
            "main.widgets { emit-events: insert,update,delete }",
            ":root { outbox-table: main.__outbox }",
        })).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    // Seeds one undelivered outbox row for widget <paramref name="id"/> and returns its outbox id.
    private async Task<long> SeedRowAsync(long id, string op = "insert", int attempts = 0, int dead = 0)
    {
        await using var cmd = new SqliteCommand(
            """
            INSERT INTO __outbox(aggregate, op, payload, created_at, attempts, dead)
            VALUES ('main.widgets', @op, @payload, '2026-07-15T10:00:00.0000000', @attempts, @dead);
            SELECT last_insert_rowid();
            """, _keepAlive);
        cmd.Parameters.AddWithValue("@op", op);
        cmd.Parameters.AddWithValue("@payload", $$"""{"id":{{id}},"name":"widget-{{id}}"}""");
        cmd.Parameters.AddWithValue("@attempts", attempts);
        cmd.Parameters.AddWithValue("@dead", dead);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<(bool dispatched, long attempts)> ReadRowAsync(long outboxId)
    {
        await using var cmd = new SqliteCommand(
            "SELECT dispatched_at, attempts FROM __outbox WHERE id = @id", _keepAlive);
        cmd.Parameters.AddWithValue("@id", outboxId);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (!reader.IsDBNull(0), reader.GetInt64(1));
    }

    private sealed class FakeSink : IEventSink
    {
        private readonly EventDeliveryResult _result;
        public List<JsonObject> Received { get; } = new();
        public FakeSink(EventDeliveryResult result) => _result = result;

        public ValueTask<EventDeliveryResult> DeliverAsync(JsonObject envelope, CancellationToken cancellationToken)
        {
            Received.Add(envelope);
            return ValueTask.FromResult(_result);
        }
    }

    private Task<DrainOutcome> DrainAsync(IEventSink sink) =>
        OutboxDispatcher.DrainOnceAsync(_model, _factory, sink, logger: null, batchSize: 100, CancellationToken.None);

    [Fact]
    public async Task Delivered_StampsDispatchedAt_AndLeavesAttempts()
    {
        var outboxId = await SeedRowAsync(1);
        var sink = new FakeSink(EventDeliveryResult.Delivered);

        var outcome = await DrainAsync(sink);

        outcome.Delivered.Should().Be(1);
        outcome.FailedAttempts.Should().BeNull("a delivered pass has no failure");

        var (dispatched, attempts) = await ReadRowAsync(outboxId);
        dispatched.Should().BeTrue("a delivered event is stamped dispatched_at");
        attempts.Should().Be(0, "a successful delivery does not touch the attempt counter");

        sink.Received.Should().ContainSingle();
        var envelope = sink.Received[0];
        envelope["specversion"]!.ToString().Should().Be("1.0");
        envelope["source"]!.ToString().Should().Be("main.widgets");
        envelope["subject"]!.ToString().Should().Be("1", "the subject is the row primary key");
    }

    [Fact]
    public async Task TransientFailure_IncrementsAttempts_AndLeavesDispatchedAtNull()
    {
        var outboxId = await SeedRowAsync(1);
        var sink = new FakeSink(EventDeliveryResult.TransientFailure);

        var outcome = await DrainAsync(sink);

        outcome.Delivered.Should().Be(0);
        outcome.FailedAttempts.Should().Be(1, "the post-increment attempt count feeds the backoff");

        var (dispatched, attempts) = await ReadRowAsync(outboxId);
        dispatched.Should().BeFalse("a transient failure must NOT stamp dispatched_at");
        attempts.Should().Be(1, "a transient failure increments the attempt counter");
    }

    [Fact]
    public async Task StopsAtFirstTransientFailure_PreservingOrder()
    {
        // Two pending rows in id order; the sink fails every delivery. The pass must
        // stop after the FIRST row so the second is never delivered out of order.
        var first = await SeedRowAsync(1);
        var second = await SeedRowAsync(2);
        var sink = new FakeSink(EventDeliveryResult.TransientFailure);

        var outcome = await DrainAsync(sink);

        outcome.Delivered.Should().Be(0);
        sink.Received.Should().ContainSingle("the pass stops at the first failure");

        (await ReadRowAsync(first)).attempts.Should().Be(1);
        (await ReadRowAsync(second)).attempts.Should().Be(0, "the second row was never attempted");
    }

    [Fact]
    public async Task SkipsAlreadyDispatchedAndDeadRows()
    {
        // A dead row and an already-dispatched row are ineligible; only the pending one drains.
        await Exec(
            """
            INSERT INTO __outbox(aggregate, op, payload, created_at, dead)
            VALUES ('main.widgets', 'insert', '{"id":9,"name":"dead"}', '2026-07-15T10:00:00.0000000', 1)
            """);
        await Exec(
            """
            INSERT INTO __outbox(aggregate, op, payload, created_at, dispatched_at)
            VALUES ('main.widgets', 'insert', '{"id":8,"name":"done"}', '2026-07-15T10:00:00.0000000', '2026-07-15T10:05:00.0000000')
            """);
        var pending = await SeedRowAsync(1);
        var sink = new FakeSink(EventDeliveryResult.Delivered);

        var outcome = await DrainAsync(sink);

        outcome.Delivered.Should().Be(1, "only the pending, non-dead row is eligible");
        sink.Received.Should().ContainSingle();
        (await ReadRowAsync(pending)).dispatched.Should().BeTrue();
    }
}
