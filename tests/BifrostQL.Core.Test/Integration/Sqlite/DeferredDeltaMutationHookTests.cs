using System.Text.Json;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Deferred;
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

/// <summary>End-to-end SQLite coverage for durable reverse deltas.</summary>
public sealed class DeferredDeltaMutationHookTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_deferred_delta_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        foreach (var table in new[] { "__outbox", "__history", "tenant_widgets", "soft_widgets", "widgets", "change_set_deltas", "change_sets" })
            await Exec($"DROP TABLE IF EXISTS {table}");
        await Exec("CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NULL CHECK (name <> 'boom'), version INTEGER NULL DEFAULT 1)");
        await Exec("CREATE TABLE soft_widgets (id INTEGER PRIMARY KEY, name TEXT NULL, deleted_at TEXT NULL, version INTEGER NULL DEFAULT 1)");
        await Exec("CREATE TABLE tenant_widgets (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, name TEXT NULL, version INTEGER NULL DEFAULT 1)");
        await Exec("CREATE TABLE __history (id INTEGER PRIMARY KEY, entity TEXT NOT NULL, entity_id TEXT NOT NULL, op TEXT NOT NULL, actor TEXT NULL, tenant_id INTEGER NULL, changed_at TEXT NOT NULL, before TEXT NULL, after TEXT NULL, changed_columns TEXT NULL)");
        await Exec("CREATE TABLE change_sets (id INTEGER PRIMARY KEY, state TEXT NOT NULL, undo_window_expires_at TEXT NOT NULL, requester TEXT NULL, tenant TEXT NULL, tables TEXT NOT NULL, created_at TEXT NOT NULL, applied_at TEXT NULL, reversed_at TEXT NULL)");
        await Exec("CREATE TABLE change_set_deltas (id INTEGER PRIMARY KEY, change_set_id INTEGER NOT NULL, \"table\" TEXT NOT NULL, pk TEXT NOT NULL, op TEXT NOT NULL, inverse_op TEXT NOT NULL, before_image TEXT NULL, after_image TEXT NULL, created_at TEXT NOT NULL)");
        await Exec("CREATE TABLE __outbox (id INTEGER PRIMARY KEY, aggregate TEXT NOT NULL, op TEXT NOT NULL, payload TEXT NOT NULL, tenant TEXT NULL, created_at TEXT NOT NULL, dispatched_at TEXT NULL, attempts INTEGER NOT NULL DEFAULT 0, dead INTEGER NOT NULL DEFAULT 0, change_set_id INTEGER NULL, state TEXT NULL)");
        await Exec("INSERT INTO widgets(id, name) VALUES (1, 'original')");
        await Exec("INSERT INTO soft_widgets(id, name) VALUES (1, 'soft-original')");
        await Exec("INSERT INTO tenant_widgets(id, tenant_id, name) VALUES (1, 1, 'tenant-original')");
        _model = await LoadModelAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task SingleRowWrites_RecordReversibleInsertUpdateDeleteAndSoftDeleteDeltas()
    {
        (await ExecuteMutationAsync("mutation { widgets(insert: { name: \"new\" }) }", registerConcurrency: true)).Errors.Should().BeNullOrEmpty();
        (await ExecuteMutationAsync("mutation { widgets(update: { id: 1, name: \"edited\", version: 1 }) }", registerConcurrency: true)).Errors.Should().BeNullOrEmpty();
        (await ExecuteMutationAsync("mutation { widgets(delete: { id: 1 }) }", registerConcurrency: true)).Errors.Should().BeNullOrEmpty();
        (await ExecuteMutationAsync("mutation { soft_widgets(delete: { id: 1 }) }", registerConcurrency: true)).Errors.Should().BeNullOrEmpty();

        var deltas = await DeltasAsync();
        deltas.Should().HaveCount(4);
        deltas.Select(d => (d.Op, d.InverseOp)).Should().Equal(("insert", "delete"), ("update", "restore"), ("delete", "restore"), ("update", "restore"));
        deltas[0].BeforeImage.Should().BeNull();
        Json(deltas[0].AfterImage!)["id"].GetInt64().Should().BeGreaterThan(0, "the delete inverse must target the generated row");
        Json(deltas[0].AfterImage!)["version"].GetInt64().Should().Be(1, "the delete inverse must carry the stored default concurrency token");
        Json(deltas[1].BeforeImage!)["name"].GetString().Should().Be("original");
        Json(deltas[1].AfterImage!)["version"].GetInt64().Should().Be(2, "undo must carry the post-write concurrency token");
        Json(deltas[2].BeforeImage!)["name"].GetString().Should().Be("edited");
        Json(deltas[2].BeforeImage!)["version"].GetInt64().Should().Be(2, "restore must carry the pre-delete concurrency token");
        Json(deltas[3].BeforeImage!)["deleted_at"].ValueKind.Should().Be(JsonValueKind.Null);
        (await CountAsync("soft_widgets", "id = 1 AND deleted_at IS NOT NULL")).Should().Be(1);
    }

    [Fact]
    public async Task UndoUpdate_UsesRealPipelineAndCapturedPostWriteToken()
    {
        await Exec("INSERT INTO change_sets (id, state, undo_window_expires_at, tables, created_at) VALUES (42, 'held', '2099-01-01T00:00:00Z', '[]', '2026-07-20T00:00:00Z')");
        await Exec("INSERT INTO change_set_deltas (id, change_set_id, \"table\", pk, op, inverse_op, before_image, after_image, created_at) VALUES (42, 42, 'main.widgets', '{\"id\":1}', 'update', 'restore', '{\"id\":1,\"name\":\"original\",\"version\":1}', '{\"id\":1,\"name\":\"edited\",\"version\":2}', '2026-07-20T00:00:00Z')");
        await Exec("UPDATE widgets SET name = 'edited', version = 2 WHERE id = 1");

        var result = await UndoAsync(42);

        result.Should().Be(new DeferredUndoResult(42, 1, 0, false));
        (await ScalarAsync("SELECT name || ':' || version FROM widgets WHERE id = 1")).Should().Be("original:3");
    }

    [Fact]
    public async Task UndoUpdate_WithDrift_RecordsConflictAndLeavesRowUntouched()
    {
        await SeedChangeSetAsync(43, "main.widgets", 1, "update", "restore",
            "{\"id\":1,\"name\":\"original\",\"version\":1}", "{\"id\":1,\"name\":\"edited\",\"version\":2}");
        await Exec("UPDATE widgets SET name = 'newer', version = 3 WHERE id = 1");

        (await UndoAsync(43)).Should().Be(new DeferredUndoResult(43, 0, 1, false));
        (await ScalarAsync("SELECT name || ':' || version FROM widgets WHERE id = 1")).Should().Be("newer:3");
        (await ScalarAsync("SELECT state FROM change_sets WHERE id = 43")).Should().Be("partial");
    }

    [Fact]
    public async Task Undo_MixedRows_IsPartialAndSecondCallIsUnavailable()
    {
        await Exec("INSERT INTO widgets(id, name, version) VALUES (2, 'two-edited', 2)");
        await Exec("UPDATE widgets SET name = 'one-edited', version = 2 WHERE id = 1");
        await Exec("INSERT INTO change_sets (id, state, undo_window_expires_at, tables, created_at) VALUES (44, 'held', '2099-01-01T00:00:00Z', '[]', '2026-07-20T00:00:00Z')");
        await Exec("INSERT INTO change_set_deltas VALUES (440,44,'main.widgets','{\"id\":1}','update','restore','{\"id\":1,\"name\":\"original\",\"version\":1}','{\"id\":1,\"name\":\"one-edited\",\"version\":2}','2026-07-20T00:00:00Z')");
        await Exec("INSERT INTO change_set_deltas VALUES (441,44,'main.widgets','{\"id\":2}','update','restore','{\"id\":2,\"name\":\"two\",\"version\":1}','{\"id\":2,\"name\":\"two-edited\",\"version\":2}','2026-07-20T00:00:00Z')");
        await Exec("UPDATE widgets SET version = 3 WHERE id = 2");

        (await UndoAsync(44)).Should().Be(new DeferredUndoResult(44, 1, 1, false));
        Func<Task> retry = async () => await UndoAsync(44);
        await retry.Should().ThrowAsync<BifrostExecutionError>();
    }

    [Fact]
    public async Task UndoInsertThenReEdit_ConflictsWhileUndoDeleteRestoresRow()
    {
        await Exec("INSERT INTO widgets(id, name, version) VALUES (10, 'inserted', 1)");
        await SeedChangeSetAsync(45, "main.widgets", 10, "insert", "delete", null,
            "{\"id\":10,\"name\":\"inserted\",\"version\":1}");
        await Exec("UPDATE widgets SET name = 're-edited', version = 2 WHERE id = 10");
        (await UndoAsync(45)).ConflictRows.Should().Be(1);
        (await CountAsync("widgets", "id = 10")).Should().Be(1);

        await Exec("DELETE FROM widgets WHERE id = 1");
        await SeedChangeSetAsync(46, "main.widgets", 1, "delete", "restore",
            "{\"id\":1,\"name\":\"original\",\"version\":1}", null);
        (await UndoAsync(46)).UndoneRows.Should().Be(1);
        (await ScalarAsync("SELECT name FROM widgets WHERE id = 1")).Should().Be("original");
    }

    [Fact]
    public async Task Undo_CrossTenant_IsDeniedThroughPipelineScoping()
    {
        await Exec("UPDATE tenant_widgets SET name = 'edited', version = 2 WHERE id = 1");
        await SeedChangeSetAsync(47, "main.tenant_widgets", 1, "update", "restore",
            "{\"id\":1,\"tenant_id\":1,\"name\":\"tenant-original\",\"version\":1}",
            "{\"id\":1,\"tenant_id\":1,\"name\":\"edited\",\"version\":2}");

        (await UndoAsync(47, new Dictionary<string, object?> { ["tenant_id"] = 2 })).ConflictRows.Should().Be(1);
        (await ScalarAsync("SELECT name FROM tenant_widgets WHERE id = 1")).Should().Be("edited");
    }

    [Fact]
    public async Task GraphQlUndo_WiresDispatcherAndIsIdempotent()
    {
        await Exec("UPDATE widgets SET name = 'edited', version = 2 WHERE id = 1");
        await SeedChangeSetAsync(48, "main.widgets", 1, "update", "restore",
            "{\"id\":1,\"name\":\"original\",\"version\":1}", "{\"id\":1,\"name\":\"edited\",\"version\":2}");
        var executor = BuildMutationExecutor(_model);
        await using var provider = new ServiceCollection().AddSingleton<IMutationIntentExecutor>(executor).BuildServiceProvider();
        var schema = DbSchema.FromModel(_model);

        async Task<ExecutionResult> Run() => await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = "mutation { undo(changeSetId: 48) { changeSetId undoneRows conflictRows alreadyUndone } }";
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString), ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            });
        });

        (await Run()).Errors.Should().BeNullOrEmpty();
        var second = await Run();
        second.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT state FROM change_sets WHERE id = 48")).Should().Be("undone");
        (await ScalarAsync("SELECT version FROM widgets WHERE id = 1")).Should().Be("3", "the second GraphQL undo must not replay the inverse");
    }

    [Fact]
    public async Task BatchUpsertThatInserts_RecordsDeleteInverseForInsertedKey()
    {
        var result = await ExecuteMutationAsync("mutation { widgets_batch(actions: [ { upsert: { id: 77, name: \"fresh\" } } ]) }");

        result.Errors.Should().BeNullOrEmpty();
        var delta = (await DeltasAsync()).Should().ContainSingle().Subject;
        delta.Op.Should().Be("insert");
        delta.InverseOp.Should().Be("delete");
        Json(delta.Pk)["id"].GetInt64().Should().Be(77);
        delta.BeforeImage.Should().BeNull();
        Json(delta.AfterImage!)["version"].GetInt64().Should().Be(1, "the delete inverse must use the stored post-upsert token");
    }

    [Fact]
    public async Task Batch_RecordsEveryDeltaInOneChangeSetTransaction()
    {
        var result = await ExecuteMutationAsync("mutation { widgets_batch(actions: [ { insert: { name: \"a\" } }, { update: { id: 1, name: \"batch-edited\" } } ]) }");

        result.Errors.Should().BeNullOrEmpty();
        (await CountAsync("change_sets", "1=1")).Should().Be(1);
        var deltas = await DeltasAsync();
        deltas.Should().HaveCount(2);
        deltas.Select(d => d.ChangeSetId).Distinct().Should().ContainSingle("a batch shares its mutation transaction and active change set");
        (await CountAsync("widgets", "name = 'batch-edited'")).Should().Be(1);
    }

    [Fact]
    public async Task TreeSync_RecordsDeltasWithConnectionScopedTransactionState()
    {
        await Exec("CREATE TABLE blogs (id INTEGER PRIMARY KEY, name TEXT NULL, version INTEGER NULL DEFAULT 1)");
        await Exec("CREATE TABLE posts (id INTEGER PRIMARY KEY, blog_id INTEGER NOT NULL REFERENCES blogs(id), title TEXT NULL, version INTEGER NULL DEFAULT 1)");
        var model = await LoadModelAsync("main.blogs { deferrable: enabled; undo-window: 1h; concurrency-token: version; history: enabled }", "main.posts { deferrable: enabled; undo-window: 1h; concurrency-token: version; history: enabled }");

        var result = await ExecuteMutationAsync("mutation { blogs(sync: { name: \"B\", posts: [ { title: \"first\" } ] }) }", model);

        result.Errors.Should().BeNullOrEmpty();
        var deltas = await DeltasAsync();
        deltas.Should().HaveCount(2);
        deltas.Select(d => d.Table).Should().BeEquivalentTo("main.blogs", "main.posts");
        deltas.Select(d => d.ChangeSetId).Distinct().Should().ContainSingle("TreeSync shares its connection-scoped active state when its transaction is null");
    }

    [Fact]
    public async Task HookFailure_RollsBackDataChangeSetAndDeltaAtomically()
    {
        var result = await ExecuteMutationAsync("mutation { widgets(insert: { name: \"will-roll-back\" }) }", addFailingHook: true);

        result.Errors.Should().NotBeNullOrEmpty();
        (await CountAsync("widgets", "name = 'will-roll-back'")).Should().Be(0);
        (await CountAsync("change_sets", "1=1")).Should().Be(0);
        (await CountAsync("change_set_deltas", "1=1")).Should().Be(0);
    }

    [Fact]
    public async Task HoldEvents_WritesPendingHoldAndTimedReleaseIsIdempotent()
    {
        var model = await LoadModelAsync(
            "main.widgets { emit-events: insert; hold-events: until-window }",
            ":root { outbox-table: main.__outbox }");

        var result = await ExecuteMutationAsync("mutation { widgets(insert: { name: \"held-event\" }) }", model);

        result.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT state || ':' || change_set_id FROM __outbox")).Should().MatchRegex("pending_hold:[0-9]+");
        (await new DeferredOutboxReleaseEngine(model, new SqliteDbConnFactory(ConnString))
            .ReleaseOnceAsync(DateTimeOffset.UtcNow.AddHours(2))).Should().Be(1);
        (await new DeferredOutboxReleaseEngine(model, new SqliteDbConnFactory(ConnString))
            .ReleaseOnceAsync(DateTimeOffset.UtcNow.AddHours(2))).Should().Be(0);
        (await ScalarAsync("SELECT state FROM __outbox")).Should().Be("pending");
    }

    [Fact]
    public async Task Undo_SuppressesHeldEvent_AndCompensatesEventReleasedThroughDispatcher()
    {
        var model = await LoadModelAsync(":root { outbox-table: main.__outbox }");
        await Exec("UPDATE widgets SET name='edited', version=2 WHERE id=1");
        await SeedChangeSetAsync(90, "main.widgets", 1, "update", "restore", "{\"id\":1,\"name\":\"original\",\"version\":1}", "{\"id\":1,\"name\":\"edited\",\"version\":2}");
        await Exec("INSERT INTO __outbox(id,aggregate,op,payload,created_at,attempts,dead,change_set_id,state) VALUES(90,'main.widgets','update','{\"id\":1}','2026-07-20',0,0,90,'pending_hold')");

        await new DeferredUndoEngine(model, new SqliteDbConnFactory(ConnString), BuildMutationExecutor(model))
            .UndoAsync(90, new Dictionary<string, object?>());
        (await ScalarAsync("SELECT state FROM __outbox WHERE id=90")).Should().Be("suppressed");

        await Exec("UPDATE widgets SET name='edited-again', version=4 WHERE id=1");
        await SeedChangeSetAsync(91, "main.widgets", 1, "update", "restore", "{\"id\":1,\"name\":\"original\",\"version\":3}", "{\"id\":1,\"name\":\"edited-again\",\"version\":4}");
        await Exec("INSERT INTO __outbox(id,aggregate,op,payload,created_at,attempts,dead,change_set_id,state) VALUES(91,'main.widgets','update','{\"id\":1}','2026-07-20',0,0,91,'pending_hold')");
        var afterExpiry = DateTimeOffset.Parse("2100-01-01T00:00:00Z");
        (await new DeferredOutboxReleaseEngine(model, new SqliteDbConnFactory(ConnString))
            .ReleaseOnceAsync(afterExpiry)).Should().Be(1);
        (await OutboxDispatcher.DrainOnceAsync(model, new SqliteDbConnFactory(ConnString), new DeliveredSink(), null, 100, 5, default))
            .Delivered.Should().Be(1);
        await new DeferredUndoEngine(model, new SqliteDbConnFactory(ConnString), BuildMutationExecutor(model), () => afterExpiry)
            .UndoAsync(91, new Dictionary<string, object?>());
        (await CountAsync("__outbox", "change_set_id=91 AND op='compensate' AND state='pending'")).Should().Be(1);
    }

    [Fact]
    public async Task UndoingState_ResumesAfterAppliedInverse_AndReleaseCannotAlsoWin()
    {
        var model = await LoadModelAsync(":root { outbox-table: main.__outbox }");
        await SeedChangeSetAsync(92, "main.widgets", 1, "update", "restore", "{\"id\":1,\"name\":\"original\",\"version\":1}", "{\"id\":1,\"name\":\"edited\",\"version\":2}");
        await Exec("UPDATE widgets SET name='edited', version=2 WHERE id=1");
        await Exec("INSERT INTO __outbox(id,aggregate,op,payload,created_at,attempts,dead,change_set_id,state) VALUES(92,'main.widgets','update','{\"id\":1}','2026-07-20',0,0,92,'pending_hold')");
        var clock = DateTimeOffset.Parse("2090-01-01T00:00:00Z");
        Func<Task> interrupted = async () => await new DeferredUndoEngine(
                model, new SqliteDbConnFactory(ConnString), new InterruptingMutationExecutor(), () => clock)
            .UndoAsync(92, new Dictionary<string, object?>());
        await interrupted.Should().ThrowAsync<OperationCanceledException>();
        (await ScalarAsync("SELECT state FROM change_sets WHERE id=92")).Should().Be("undoing",
            "the pre-expiry conditional claim is durable before inverse execution");
        // Simulate a process crash after the inverse committed but before outbox settlement/finalization.
        await Exec("UPDATE widgets SET name='original', version=3 WHERE id=1");

        clock = DateTimeOffset.Parse("2100-01-01T00:00:00Z");
        var resumed = await new DeferredUndoEngine(model, new SqliteDbConnFactory(ConnString), BuildMutationExecutor(model), () => clock)
            .UndoAsync(92, new Dictionary<string, object?>());

        resumed.Should().Be(new DeferredUndoResult(92, 1, 0, false));
        (await ScalarAsync("SELECT state FROM change_sets WHERE id=92")).Should().Be("undone");
        (await ScalarAsync("SELECT state FROM __outbox WHERE id=92")).Should().Be("suppressed");
        (await new DeferredOutboxReleaseEngine(model, new SqliteDbConnFactory(ConnString))
            .ReleaseOnceAsync(DateTimeOffset.Parse("2100-01-01T00:00:00Z"))).Should().Be(0,
                "the conditional state transition lets undo win exactly once");
    }

    [Fact]
    public async Task OrdinaryExpiredHeldState_RemainsClosed()
    {
        var model = await LoadModelAsync(":root { outbox-table: main.__outbox }");
        await SeedChangeSetAsync(93, "main.widgets", 1, "update", "restore", "{\"id\":1,\"name\":\"original\",\"version\":1}", "{\"id\":1,\"name\":\"edited\",\"version\":2}");
        var afterExpiry = DateTimeOffset.Parse("2100-01-01T00:00:00Z");

        Func<Task> undo = async () => await new DeferredUndoEngine(
                model, new SqliteDbConnFactory(ConnString), BuildMutationExecutor(model), () => afterExpiry)
            .UndoAsync(93, new Dictionary<string, object?>());

        await undo.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*expired*");
        (await ScalarAsync("SELECT state FROM change_sets WHERE id=93")).Should().Be("held");
    }

    [Fact]
    public async Task ReviewRejection_ClaimsAndCompensatesExpiredUntilApprovedHold()
    {
        var model = await LoadModelAsync(
            "main.widgets { hold-events: until-approved; approval: enabled; approver-role: manager }",
            ":root { outbox-table: main.__outbox }");
        await SeedChangeSetAsync(94, "main.widgets", 1, "update", "restore", "{\"id\":1,\"name\":\"original\",\"version\":1}", "{\"id\":1,\"name\":\"edited\",\"version\":2}");
        await Exec("UPDATE widgets SET name='edited', version=2 WHERE id=1");
        await Exec("UPDATE change_sets SET undo_window_expires_at='2020-01-01T00:00:00Z', requester='requester', tenant='tenant-1', tables='[\"main.widgets\"]' WHERE id=94");
        await Exec("INSERT INTO __outbox(id,aggregate,op,payload,created_at,attempts,dead,change_set_id,state) VALUES(94,'main.widgets','update','{\"id\":1}','2026-07-20',0,0,94,'pending_hold')");
        var reviewer = new AppIdentity("reviewer", "test", tenantId: "tenant-1", roles: ["manager"]);

        var result = await new DeferredReviewQueue(model, new SqliteDbConnFactory(ConnString),
                BuildMutationExecutor(model), new PolicyEvaluator(), () => DateTimeOffset.Parse("2026-07-20T00:00:00Z"))
            .RejectAsync(94, reviewer);

        result.Should().Be(new DeferredUndoResult(94, 1, 0, false));
        (await ScalarAsync("SELECT state FROM change_sets WHERE id=94")).Should().Be("undone");
        (await ScalarAsync("SELECT state FROM __outbox WHERE id=94")).Should().Be("suppressed");
        (await ScalarAsync("SELECT name FROM widgets WHERE id=1")).Should().Be("original");
    }

    [Fact]
    public async Task GraphQlReviewQueue_ListsAndApprovesHeldChangeSetToRelease()
    {
        var model = await ReviewModelAsync();
        await SeedReviewHoldAsync(95, "requester", "tenant-1");

        var listed = await ExecuteReviewGraphQlAsync(model,
            "query { deferredReviewQueue { changeSetId requester tenant tables createdAt } }", Reviewer("reviewer", "tenant-1"));
        listed.Errors.Should().BeNullOrEmpty();
        listed.Data!.ToString().Should().Contain("95").And.Contain("requester");

        var approved = await ExecuteReviewGraphQlAsync(model,
            "mutation { approveDeferredChangeSet(changeSetId: 95) }", Reviewer("reviewer", "tenant-1"));
        approved.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT state FROM change_sets WHERE id=95")).Should().Be("released");
        (await ScalarAsync("SELECT state FROM __outbox WHERE id=95")).Should().Be("pending");
    }

    [Fact]
    public async Task GraphQlReviewQueue_DeniesCrossTenantApproval()
    {
        var model = await ReviewModelAsync();
        await SeedReviewHoldAsync(96, "requester", "tenant-1");

        var result = await ExecuteReviewGraphQlAsync(model,
            "mutation { approveDeferredChangeSet(changeSetId: 96) }", Reviewer("reviewer", "tenant-2"));

        result.Errors.Should().BeNullOrEmpty();
        result.Data!.ToString().Should().Contain("False");
        (await ScalarAsync("SELECT state FROM change_sets WHERE id=96")).Should().Be("held");
    }

    [Fact]
    public async Task GraphQlReviewQueue_DeniesSelfApprovalWhenDisabled()
    {
        var model = await ReviewModelAsync();
        await SeedReviewHoldAsync(97, "requester", "tenant-1");

        var result = await ExecuteReviewGraphQlAsync(model,
            "mutation { approveDeferredChangeSet(changeSetId: 97) }", Reviewer("requester", "tenant-1"));

        result.Errors.Should().BeNullOrEmpty();
        result.Data!.ToString().Should().Contain("False");
        (await ScalarAsync("SELECT state FROM change_sets WHERE id=97")).Should().Be("held");
    }

    [Fact]
    public async Task GraphQlReviewQueue_ExcludesChangeSetWhenPolicyCannotBeEvaluated()
    {
        var model = await ReviewModelAsync();
        await SeedReviewHoldAsync(98, "requester", "tenant-1");
        await Exec("UPDATE change_sets SET tables='[\"main.missing_policy_target\"]' WHERE id=98");

        var result = await ExecuteReviewGraphQlAsync(model,
            "query { deferredReviewQueue { changeSetId } }", Reviewer("reviewer", "tenant-1"));

        result.Errors.Should().BeNullOrEmpty();
        result.Data!.ToString().Should().NotContain("98");
    }

    [Fact]
    public async Task MissingBeforeImage_FailsClosedAndRollsBackWrite()
    {
        var result = await ExecuteMutationAsync("mutation { widgets(update: { id: 1, name: \"must-not-commit\" }) }", registerHistory: false);

        result.Errors.Should().NotBeNullOrEmpty();
        (await CountAsync("widgets", "id = 1 AND name = 'original'")).Should().Be(1);
        (await CountAsync("change_set_deltas", "1=1")).Should().Be(0);
    }

    [Theory]
    [InlineData("change_sets", "state: \"held\", undo_window_expires_at: \"2026-07-21T00:00:00Z\", tables: \"[]\", created_at: \"2026-07-20T00:00:00Z\"")]
    [InlineData("change_set_deltas", "change_set_id: 1, table: \"main.widgets\", pk: \"{}\", op: \"insert\", inverse_op: \"delete\", created_at: \"2026-07-20T00:00:00Z\"")]
    public async Task DeferredStore_PublicMutation_IsRejected(string table, string fields)
    {
        var result = await ExecuteMutationAsync($"mutation {{ {table}(insert: {{ {fields} }}) }}");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Should().Contain(e => e.Message.Contains("internal deferred-effects table") && e.Message.Contains("not writable"));
    }

    private static async Task<IDbModel> LoadModelAsync(params string[] extra) => await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(new[]
    {
        "main.widgets { deferrable: enabled; undo-window: 1h; concurrency-token: version; history: enabled }",
        "main.soft_widgets { deferrable: enabled; undo-window: 1h; soft-delete: deleted_at; concurrency-token: version; history: enabled }",
        "main.tenant_widgets { deferrable: enabled; undo-window: 1h; tenant-filter: tenant_id; concurrency-token: version; history: enabled }",
        ":root { history-table: main.__history }",
    }.Concat(extra).ToArray())).LoadAsync();

    private async Task<ExecutionResult> ExecuteMutationAsync(string mutation, IDbModel? model = null, bool registerHistory = true, bool addFailingHook = false, bool registerConcurrency = false)
    {
        model ??= _model;
        var schema = DbSchema.FromModel(model);
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap { Transformers = registerConcurrency ? new IMutationTransformer[] { new ConcurrencyMutationTransformer(), new SoftDeleteMutationTransformer() } : new IMutationTransformer[] { new SoftDeleteMutationTransformer() } });
        if (registerHistory)
        {
            services.AddSingleton<HistoryMutationHook>();
            services.AddSingleton<IBeforeCommitMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
            services.AddSingleton<IInTransactionMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        }
        services.AddSingleton<IInTransactionMutationHook, DeferredDeltaMutationHook>();
        services.AddSingleton<IInTransactionMutationHook, OutboxMutationHook>();
        if (addFailingHook)
            services.AddSingleton<IInTransactionMutationHook, FailingHook>();
        services.AddSingleton<BeforeCommitMutationHooks>(sp => new BeforeCommitMutationHooks(sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        services.AddSingleton<InTransactionMutationHooks>(sp => new InTransactionMutationHooks(sp.GetServices<IInTransactionMutationHook>().ToArray()));
        await using var provider = services.BuildServiceProvider();
        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString), ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema),
            });
        });
    }

    private static MutationIntentExecutor BuildMutationExecutor(IDbModel model)
    {
        var cache = new PathCache<Inputs>();
        cache.AddLoader("/graphql", () => Task.FromResult(new Inputs(new Dictionary<string, object?>
        {
            ["model"] = model, ["connFactory"] = new SqliteDbConnFactory(ConnString),
        })));
        return new MutationIntentExecutor(cache, new MutationTransformersWrap { Transformers = new IMutationTransformer[]
        {
            new TenantMutationTransformer(), new SoftDeleteMutationTransformer(), new ConcurrencyMutationTransformer(),
        }});
    }

    private static AppIdentity Reviewer(string id, string tenant) =>
        new(id, "test", tenantId: tenant, roles: ["manager"]);

    private static Task<IDbModel> ReviewModelAsync(string extraMetadata = "") => LoadModelAsync(
        $"main.widgets {{ hold-events: until-approved; approval: enabled; approver-role: manager; self-approve: false{extraMetadata} }}",
        ":root { outbox-table: main.__outbox }");

    private async Task SeedReviewHoldAsync(long id, string requester, string tenant)
    {
        await SeedChangeSetAsync(id, "main.widgets", 1, "update", "restore",
            "{\"id\":1,\"name\":\"original\",\"version\":1}", "{\"id\":1,\"name\":\"edited\",\"version\":2}");
        await Exec($"UPDATE change_sets SET requester='{requester}', tenant='{tenant}', tables='[\"main.widgets\"]' WHERE id={id}");
        await Exec($"INSERT INTO __outbox(id,aggregate,op,payload,created_at,attempts,dead,change_set_id,state) VALUES({id},'main.widgets','update','{{\"id\":1}}','2026-07-20',0,0,{id},'pending_hold')");
    }

    private static async Task<ExecutionResult> ExecuteReviewGraphQlAsync(IDbModel model, string query, AppIdentity identity)
    {
        var schema = DbSchema.FromModel(model);
        var mutations = BuildMutationExecutor(model);
        await using var provider = new ServiceCollection().AddSingleton<IMutationIntentExecutor>(mutations).BuildServiceProvider();
        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.RequestServices = provider;
            options.UserContext = new IdentityContextMapper().ToUserContext(identity);
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString), ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema),
            });
        });
    }

    private Task<DeferredUndoResult> UndoAsync(long id, IDictionary<string, object?>? user = null) =>
        new DeferredUndoEngine(_model, new SqliteDbConnFactory(ConnString), BuildMutationExecutor(_model))
            .UndoAsync(id, user ?? new Dictionary<string, object?>());

    private async Task SeedChangeSetAsync(long id, string table, long pk, string op, string inverse, string? before, string? after)
    {
        await Exec($"INSERT INTO change_sets (id, state, undo_window_expires_at, tables, created_at) VALUES ({id}, 'held', '2099-01-01T00:00:00Z', '[]', '2026-07-20T00:00:00Z')");
        await Exec($"INSERT INTO change_set_deltas (id, change_set_id, \"table\", pk, op, inverse_op, before_image, after_image, created_at) VALUES ({id}, {id}, '{table}', '{{\"id\":{pk}}}', '{op}', '{inverse}', {Sql(before)}, {Sql(after)}, '2026-07-20T00:00:00Z')");
    }

    private static string Sql(string? value) => value is null ? "NULL" : $"'{value.Replace("'", "''")}'";

    private sealed class FailingHook : IInTransactionMutationHook
    {
        public ValueTask AfterWriteInTransactionAsync(MutationObserverContext context) => throw new InvalidOperationException("forced deferred rollback");
    }

    private sealed class DeliveredSink : IEventSink
    {
        public ValueTask<EventDeliveryResult> DeliverAsync(
            System.Text.Json.Nodes.JsonObject cloudEvent, string idempotencyKey, CancellationToken cancellationToken)
            => ValueTask.FromResult(EventDeliveryResult.Delivered);
    }

    private sealed class InterruptingMutationExecutor : IMutationIntentExecutor
    {
        public Task<MutationIntentResult> ExecuteAsync(MutationIntent intent, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException("simulated process interruption");

        public Task<MutationBatchIntentResult> ExecuteBatchAsync(MutationBatchIntent intent, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException("simulated process interruption");
    }

    private sealed record Delta(long ChangeSetId, string Table, string Pk, string Op, string InverseOp, string? BeforeImage, string? AfterImage);
    private async Task<List<Delta>> DeltasAsync()
    {
        var result = new List<Delta>();
        await using var command = new SqliteCommand("SELECT change_set_id, \"table\", pk, op, inverse_op, before_image, after_image FROM change_set_deltas ORDER BY id", _keepAlive);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return result;
    }

    private static Dictionary<string, JsonElement> Json(string value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value)!;
    private async Task<long> CountAsync(string table, string where)
    {
        await using var command = new SqliteCommand($"SELECT COUNT(*) FROM {table} WHERE {where}", _keepAlive);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    private async Task<string?> ScalarAsync(string sql)
    {
        await using var command = new SqliteCommand(sql, _keepAlive);
        return (await command.ExecuteScalarAsync())?.ToString();
    }
    private async Task Exec(string sql)
    {
        await using var command = new SqliteCommand(sql, _keepAlive);
        await command.ExecuteNonQueryAsync();
    }
}
