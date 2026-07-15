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
/// End-to-end proof of the CDC outbox dispatcher drain pass (slices 4a + 4b). Outbox rows
/// are seeded directly; a scripted fake <see cref="IEventSink"/> drives the branches the
/// drain must get right:
/// <list type="bullet">
///   <item>Delivered → the row is stamped <c>dispatched_at</c> and its <c>attempts</c> is left alone.</item>
///   <item>TransientFailure → <c>attempts</c> is incremented and <c>dispatched_at</c> stays null.</item>
///   <item>Per-PK ordering: same-key events deliver in id order; a stuck key never blocks a healthy one.</item>
///   <item>Dead-letter: a row that exhausts its attempt budget flips <c>dead</c> and is never re-dispatched.</item>
///   <item>The CloudEvents id is passed as the delivery idempotency key on every call.</item>
/// </list>
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
        await Exec("DROP TABLE IF EXISTS orders");
        await Exec("DROP TABLE IF EXISTS __outbox");
        await Exec(
            """
            CREATE TABLE widgets (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL
            )
            """);
        // A SECOND tracked aggregate sharing widgets' single-column integer PK shape. Because the
        // __outbox table is shared across ALL tracked tables, an orders row and a widgets row can
        // carry the SAME PK value (subject) — the case the per-key grouping must keep isolated.
        await Exec(
            """
            CREATE TABLE orders (
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
            "main.orders { emit-events: insert,update,delete }",
            ":root { outbox-table: main.__outbox }",
        })).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    // Seeds one undelivered outbox row for widget <paramref name="widgetId"/> and returns its outbox id.
    // The widget id becomes the CloudEvents subject (row PK), so two rows sharing widgetId share a key.
    private async Task<long> SeedRowAsync(long widgetId, string op = "insert", int attempts = 0, int dead = 0)
        => await SeedRowForAsync("main.widgets", widgetId, op, attempts, dead);

    // Seeds one undelivered outbox row for an arbitrary tracked <paramref name="aggregate"/> whose PK
    // value is <paramref name="pk"/>. Two aggregates may share a PK value; the grouping key must keep
    // them in separate buckets so one cannot head-of-line-block the other.
    private async Task<long> SeedRowForAsync(string aggregate, long pk, string op = "insert", int attempts = 0, int dead = 0)
    {
        await using var cmd = new SqliteCommand(
            """
            INSERT INTO __outbox(aggregate, op, payload, created_at, attempts, dead)
            VALUES (@aggregate, @op, @payload, '2026-07-15T10:00:00.0000000', @attempts, @dead);
            SELECT last_insert_rowid();
            """, _keepAlive);
        cmd.Parameters.AddWithValue("@aggregate", aggregate);
        cmd.Parameters.AddWithValue("@op", op);
        cmd.Parameters.AddWithValue("@payload", $$"""{"id":{{pk}},"name":"row-{{pk}}"}""");
        cmd.Parameters.AddWithValue("@attempts", attempts);
        cmd.Parameters.AddWithValue("@dead", dead);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<(bool dispatched, long attempts, bool dead)> ReadRowAsync(long outboxId)
    {
        await using var cmd = new SqliteCommand(
            "SELECT dispatched_at, attempts, dead FROM __outbox WHERE id = @id", _keepAlive);
        cmd.Parameters.AddWithValue("@id", outboxId);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (!reader.IsDBNull(0), reader.GetInt64(1), reader.GetInt64(2) != 0);
    }

    // A scripted sink: the delivery body is supplied per test (it may return a result, throw,
    // or cancel). It records the id, subject, and the idempotency key it was handed.
    private sealed class ScriptedSink : IEventSink
    {
        private readonly Func<JsonObject, EventDeliveryResult> _decide;
        public List<string> ReceivedSubjects { get; } = new();
        public List<string> ReceivedIds { get; } = new();
        public List<string> IdempotencyKeys { get; } = new();
        public List<JsonObject> ReceivedEnvelopes { get; } = new();

        public ScriptedSink(EventDeliveryResult result) => _decide = _ => result;
        public ScriptedSink(Func<JsonObject, EventDeliveryResult> decide) => _decide = decide;

        public ValueTask<EventDeliveryResult> DeliverAsync(
            JsonObject envelope, string idempotencyKey, CancellationToken cancellationToken)
        {
            ReceivedSubjects.Add(envelope["subject"]!.ToString());
            ReceivedIds.Add(envelope["id"]!.ToString());
            IdempotencyKeys.Add(idempotencyKey);
            ReceivedEnvelopes.Add(envelope);
            return ValueTask.FromResult(_decide(envelope));
        }
    }

    private Task<DrainOutcome> DrainAsync(IEventSink sink, int maxAttempts = 5) =>
        OutboxDispatcher.DrainOnceAsync(
            _model, _factory, sink, logger: null, batchSize: 100, maxAttempts, CancellationToken.None);

    [Fact]
    public async Task Delivered_StampsDispatchedAt_AndLeavesAttempts()
    {
        var outboxId = await SeedRowAsync(1);
        var sink = new ScriptedSink(EventDeliveryResult.Delivered);

        var outcome = await DrainAsync(sink);

        outcome.Delivered.Should().Be(1);
        outcome.FailedAttempts.Should().BeNull("a delivered pass has no failure");

        var (dispatched, attempts, dead) = await ReadRowAsync(outboxId);
        dispatched.Should().BeTrue("a delivered event is stamped dispatched_at");
        attempts.Should().Be(0, "a successful delivery does not touch the attempt counter");
        dead.Should().BeFalse();

        sink.ReceivedSubjects.Should().ContainSingle().Which.Should().Be("1", "the subject is the row primary key");
        // The idempotency key handed to the sink is exactly the CloudEvents id (the outbox row id).
        sink.IdempotencyKeys.Should().ContainSingle().Which.Should().Be(sink.ReceivedIds[0]);
        sink.IdempotencyKeys[0].Should().Be(outboxId.ToString());
    }

    [Fact]
    public async Task TransientFailure_IncrementsAttempts_AndLeavesDispatchedAtNull()
    {
        var outboxId = await SeedRowAsync(1);
        var sink = new ScriptedSink(EventDeliveryResult.TransientFailure);

        var outcome = await DrainAsync(sink);

        outcome.Delivered.Should().Be(0);
        outcome.FailedAttempts.Should().Be(1, "the post-increment attempt count feeds the backoff");

        var (dispatched, attempts, dead) = await ReadRowAsync(outboxId);
        dispatched.Should().BeFalse("a transient failure must NOT stamp dispatched_at");
        attempts.Should().Be(1, "a transient failure increments the attempt counter");
        dead.Should().BeFalse("one failure is far below the attempt budget");
    }

    [Fact]
    public async Task SinkThrow_IsConvertedToTransientFailure()
    {
        // A sink that throws must be caught and treated as a transient failure — never leaked
        // to the host — with the attempt counter advanced just like a returned TransientFailure.
        var outboxId = await SeedRowAsync(1);
        var sink = new ScriptedSink(_ => throw new InvalidOperationException("sink is down"));

        var outcome = await DrainAsync(sink);

        outcome.Delivered.Should().Be(0);
        outcome.FailedAttempts.Should().Be(1, "an escaped exception is a transient failure");

        var (dispatched, attempts, _) = await ReadRowAsync(outboxId);
        dispatched.Should().BeFalse();
        attempts.Should().Be(1, "a throwing sink advances the attempt counter");
    }

    [Fact]
    public async Task Cancellation_MidDrain_PropagatesAndStopsWork()
    {
        // Two eligible rows for DIFFERENT keys. The sink cancels while delivering the first,
        // so the drain must observe the token and abort the second — not swallow the cancel.
        await SeedRowAsync(1);
        await SeedRowAsync(2);
        using var cts = new CancellationTokenSource();
        var sink = new ScriptedSink(_ =>
        {
            cts.Cancel();
            return EventDeliveryResult.Delivered;
        });

        var act = () => OutboxDispatcher.DrainOnceAsync(
            _model, _factory, sink, logger: null, batchSize: 100, maxAttempts: 5, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("a cancelled drain must not be swallowed");
        sink.ReceivedSubjects.Should().ContainSingle("the drain aborts after the cancellation is observed");
    }

    [Fact]
    public async Task NoOutboxConfigured_IsANoOp()
    {
        // A host with no outbox-table metadata must read nothing and call nothing.
        var noCdcModel = await new DbModelLoader(_factory, new MetadataLoader(Array.Empty<string>())).LoadAsync();
        var sink = new ScriptedSink(EventDeliveryResult.Delivered);

        var outcome = await OutboxDispatcher.DrainOnceAsync(
            noCdcModel, _factory, sink, logger: null, batchSize: 100, maxAttempts: 5, CancellationToken.None);

        outcome.Should().Be(DrainOutcome.Idle);
        sink.ReceivedSubjects.Should().BeEmpty("a non-CDC host never touches the sink");
    }

    [Fact]
    public async Task PerKeyOrdering_StuckKeyDoesNotBlockHealthyKey()
    {
        // Key "1": two events in id order (insert then update). Key "2": one event.
        // The sink fails key "1" but delivers key "2" — proving key "2" drains PAST the
        // stuck key "1" (no global head-of-line stall) while key "1"'s second event stays
        // blocked behind its still-pending first event (same-key order preserved).
        var oneInsert = await SeedRowAsync(1, op: "insert");
        var oneUpdate = await SeedRowAsync(1, op: "update");
        var twoInsert = await SeedRowAsync(2, op: "insert");

        var failKeyOne = new ScriptedSink(env =>
            env["subject"]!.ToString() == "1"
                ? EventDeliveryResult.TransientFailure
                : EventDeliveryResult.Delivered);

        var outcome = await DrainAsync(failKeyOne);

        outcome.Delivered.Should().Be(1, "only key \"2\" delivered");
        outcome.FailedAttempts.Should().Be(1);

        (await ReadRowAsync(oneInsert)).attempts.Should().Be(1, "key \"1\" head failed once");
        (await ReadRowAsync(oneInsert)).dispatched.Should().BeFalse();
        (await ReadRowAsync(oneUpdate)).attempts.Should().Be(0, "key \"1\" second event is blocked, never attempted");
        (await ReadRowAsync(oneUpdate)).dispatched.Should().BeFalse();
        (await ReadRowAsync(twoInsert)).dispatched.Should().BeTrue("key \"2\" drained past the stuck key \"1\"");

        // The stuck key's second event was never handed to the sink this pass.
        failKeyOne.ReceivedSubjects.Should().Equal("1", "2");

        // Recover: the sink now delivers everything. Key "1"'s two events must arrive in id order.
        var deliverAll = new ScriptedSink(EventDeliveryResult.Delivered);
        var recovery = await DrainAsync(deliverAll);

        recovery.Delivered.Should().Be(2, "both of key \"1\"'s events deliver now");
        (await ReadRowAsync(oneInsert)).dispatched.Should().BeTrue();
        (await ReadRowAsync(oneUpdate)).dispatched.Should().BeTrue();
        // id order within the key: the insert (lower id) is delivered before the update.
        deliverAll.ReceivedIds.Should().Equal(oneInsert.ToString(), oneUpdate.ToString());
    }

    [Fact]
    public async Task PerKeyOrdering_StuckAggregateDoesNotBlockOtherAggregateSharingPk()
    {
        // The __outbox is shared across ALL tracked tables, so two DIFFERENT aggregates can carry
        // the SAME PK value: widgets/1 and orders/1 both produce subject "1". If the grouping key
        // were the subject alone they would collapse into one bucket and a stuck widgets/1 would
        // head-of-line-block orders/1. The key is (aggregate, subject), so they are distinct keys:
        // widgets/1 backing off must NOT stop orders/1 from delivering this same pass.
        var widgetOne = await SeedRowForAsync("main.widgets", 1, op: "insert");
        var orderOne = await SeedRowForAsync("main.orders", 1, op: "insert");

        // Fail only the widgets aggregate; deliver everything else. Both rows share subject "1",
        // so the sink MUST discriminate on the envelope source (the aggregate), not the subject.
        var failWidgets = new ScriptedSink(env =>
            env["source"]!.ToString() == "main.widgets"
                ? EventDeliveryResult.TransientFailure
                : EventDeliveryResult.Delivered);

        var outcome = await DrainAsync(failWidgets);

        outcome.Delivered.Should().Be(1, "orders/1 delivered even though widgets/1 shares its subject and is stuck");
        outcome.FailedAttempts.Should().Be(1, "only widgets/1 failed");

        (await ReadRowAsync(widgetOne)).attempts.Should().Be(1, "the stuck widgets/1 was attempted and backed off");
        (await ReadRowAsync(widgetOne)).dispatched.Should().BeFalse("widgets/1 must stay pending for retry");
        (await ReadRowAsync(orderOne)).dispatched.Should().BeTrue(
            "orders/1 drained despite sharing PK value with the stuck widgets/1 — cross-aggregate isolation holds");

        // Both rows (same subject "1") were handed to the sink; the failing aggregate did not
        // block the healthy one.
        failWidgets.ReceivedSubjects.Should().Equal("1", "1");
        failWidgets.ReceivedSubjects.Should().OnlyContain(s => s == "1", "both aggregates share the PK subject");
    }

    [Fact]
    public async Task DeadLetter_AfterExactlyMaxAttempts_AndNeverReDispatched()
    {
        // An always-failing sink must dead-letter the row after EXACTLY maxAttempts attempts,
        // then never hand it to the sink again (the eligible read excludes dead rows).
        const int maxAttempts = 3;
        var outboxId = await SeedRowAsync(1);
        var alwaysFail = new ScriptedSink(EventDeliveryResult.TransientFailure);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await DrainAsync(alwaysFail, maxAttempts);
            var (_, attempts, dead) = await ReadRowAsync(outboxId);
            attempts.Should().Be(attempt, "each pass makes exactly one more attempt on the key head");
            if (attempt < maxAttempts)
                dead.Should().BeFalse("still within the attempt budget");
            else
                dead.Should().BeTrue("the budget is exhausted at exactly maxAttempts");
        }

        alwaysFail.ReceivedSubjects.Should().HaveCount(maxAttempts, "the sink was tried exactly maxAttempts times");

        // A further pass must not re-dispatch the dead-lettered row.
        await DrainAsync(alwaysFail, maxAttempts);
        alwaysFail.ReceivedSubjects.Should().HaveCount(maxAttempts, "a dead-lettered row is never re-dispatched");

        // The row is left in place for operator inspection — never deleted.
        var (dispatched, finalAttempts, finalDead) = await ReadRowAsync(outboxId);
        dispatched.Should().BeFalse("a dead-lettered row was never delivered");
        finalDead.Should().BeTrue();
        finalAttempts.Should().Be(maxAttempts);
    }

    // Seeds one undelivered outbox row for widgets with an explicit tenant and a custom
    // payload (so a redactable non-key column can be asserted on the delivered event).
    private async Task<long> SeedWidgetWithTenantAsync(long widgetId, string? tenant, string payload)
    {
        await using var cmd = new SqliteCommand(
            """
            INSERT INTO __outbox(aggregate, op, payload, tenant, created_at, attempts, dead)
            VALUES ('main.widgets', 'insert', @payload, @tenant, '2026-07-15T10:00:00.0000000', 0, 0);
            SELECT last_insert_rowid();
            """, _keepAlive);
        cmd.Parameters.AddWithValue("@payload", payload);
        cmd.Parameters.AddWithValue("@tenant", (object?)tenant ?? DBNull.Value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Subscription_TenantScopesDelivery_AndRedactsPayload()
    {
        // A subscription bound to tenant-x with the 'secret' column redacted. widgets is on
        // the allow-list. Seed a tenant-x row, a tenant-y row, and a null-tenant row — only
        // the tenant-x event may reach the sink, and its payload must carry 'name' but not
        // 'secret'. The excluded rows must be consumed (stamped) so they never recirculate.
        var subscriptionModel = await new DbModelLoader(_factory, new MetadataLoader(new[]
        {
            "main.widgets { emit-events: insert,update,delete }",
            "main.orders { emit-events: insert,update,delete }",
            ":root { outbox-table: main.__outbox; subscription-tables: main.widgets; " +
                "subscription-tenant: tenant-x; subscription-redact: secret }",
        })).LoadAsync();

        var tenantX = await SeedWidgetWithTenantAsync(
            1, "tenant-x", """{"id":1,"name":"keep-me","secret":"strip-me"}""");
        var tenantY = await SeedWidgetWithTenantAsync(
            2, "tenant-y", """{"id":2,"name":"other-tenant","secret":"strip-me"}""");
        var nullTenant = await SeedWidgetWithTenantAsync(
            3, null, """{"id":3,"name":"no-tenant","secret":"strip-me"}""");

        var sink = new ScriptedSink(EventDeliveryResult.Delivered);
        var outcome = await OutboxDispatcher.DrainOnceAsync(
            subscriptionModel, _factory, sink, logger: null, batchSize: 100, maxAttempts: 5, CancellationToken.None);

        // Exactly the tenant-x event delivered; the tenant-y and null-tenant rows never
        // reached the sink.
        outcome.Delivered.Should().Be(1, "only the tenant-x row is in scope");
        sink.ReceivedSubjects.Should().ContainSingle().Which.Should().Be("1");

        var delivered = sink.ReceivedEnvelopes.Single();
        var data = delivered["data"]!.AsObject();
        data.ContainsKey("secret").Should().BeFalse("the redacted column is stripped before the sink");
        data.ContainsKey("name").Should().BeTrue("a non-redacted column is preserved");
        data["id"]!.GetValue<int>().Should().Be(1, "the key column is never redacted");
        delivered["tenant"]!.ToString().Should().Be("tenant-x", "the CloudEvents tenant extension still emits");

        // Delivered tenant-x row is stamped; the out-of-scope rows are ALSO stamped
        // (consumed) so a subsequent poll does not re-evaluate them forever.
        (await ReadRowAsync(tenantX)).dispatched.Should().BeTrue("the delivered row is stamped");
        (await ReadRowAsync(tenantY)).dispatched.Should().BeTrue("an out-of-tenant row is consumed, not recirculated");
        (await ReadRowAsync(nullTenant)).dispatched.Should().BeTrue("a null-tenant row is consumed, not recirculated");
        (await ReadRowAsync(tenantY)).attempts.Should().Be(0, "a filtered row is not a delivery failure");

        // A second drain delivers nothing — the filtered rows did not recirculate.
        var second = new ScriptedSink(EventDeliveryResult.Delivered);
        var secondOutcome = await OutboxDispatcher.DrainOnceAsync(
            subscriptionModel, _factory, second, logger: null, batchSize: 100, maxAttempts: 5, CancellationToken.None);
        secondOutcome.Delivered.Should().Be(0);
        second.ReceivedSubjects.Should().BeEmpty("every eligible row was consumed on the first pass");
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
        var sink = new ScriptedSink(EventDeliveryResult.Delivered);

        var outcome = await DrainAsync(sink);

        outcome.Delivered.Should().Be(1, "only the pending, non-dead row is eligible");
        sink.ReceivedSubjects.Should().ContainSingle();
        (await ReadRowAsync(pending)).dispatched.Should().BeTrue();
    }
}
