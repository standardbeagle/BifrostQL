using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Approval;
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
/// End-to-end proof of the approval write-gate (Approval slice 2). A table opted into
/// <c>approval</c> must have its writes INTERCEPTED before commit: the intended change is
/// serialized into a <c>pending_changes</c> row (state <c>pending</c>) and the target write
/// is aborted, so ZERO rows change in the target table and EXACTLY ONE pending row lands.
///
/// The gate is an <see cref="IBeforeCommitMutationHook"/>, so it runs AFTER the security
/// mutation transformers (tenant pin, policy scope) have shaped the intent — the payload it
/// enqueues is the SCOPED intent, never the raw client input. The requester and tenant are
/// captured from the caller's user context, so a slice-3 replay runs under the requester's
/// scope, not the approver's. Every write path (single-row, batch, TreeSync) enqueues rather
/// than applies; there is no path that reaches SQL on a gated table.
/// </summary>
public sealed class ApprovalInterceptHookTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_approval_intercept_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    // orders: approval-gated AND tenant-filtered, so the same fixture proves both the
    // enqueue-not-apply invariant and that the serialized payload is the scoped intent.
    // blogs/posts: an approval-gated parent/child pair, both gated so the nested TreeSync
    // path diverts every node (no gated/ungated mixing). The root user-audit-key resolves
    // the requester from the caller's context.
    private static readonly string[] Rules =
    {
        ":root { user-audit-key: user_id }",
        "main.orders { approval: enabled; approver-role: manager; tenant-filter: tenant_id }",
        "main.blogs { approval: enabled; approver-role: manager }",
        "main.posts { approval: enabled; approver-role: manager }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        foreach (var drop in new[] { "orders", "pending_changes", "posts", "blogs" })
            await Exec($"DROP TABLE IF EXISTS {drop}");

        await Exec(
            """
            CREATE TABLE orders (
                id        INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                name      TEXT NOT NULL
            )
            """);
        await Exec("INSERT INTO orders(id, tenant_id, name) VALUES (10, 1, 'seed-order')");

        await Exec("CREATE TABLE blogs (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await Exec(
            """
            CREATE TABLE posts (
                id      INTEGER PRIMARY KEY,
                blog_id INTEGER NOT NULL REFERENCES blogs(id),
                title   TEXT NOT NULL
            )
            """);

        // The pending_changes store, matching the slice-1 column contract. `table` and
        // `state` are SQL reserved words, so they are quoted in the DDL (and the hook must
        // quote them via the dialect on every fragment).
        await Exec(
            """
            CREATE TABLE pending_changes (
                id               INTEGER PRIMARY KEY,
                "table"          TEXT NOT NULL,
                op               TEXT NOT NULL,
                intended_payload TEXT NOT NULL,
                requester        TEXT NULL,
                tenant           TEXT NULL,
                requester_context TEXT NULL,
                "state"          TEXT NOT NULL,
                approver         TEXT NULL,
                decided_at       TEXT NULL,
                reason           TEXT NULL
            )
            """);

        _model = await new DbModelLoader(
            new SqliteDbConnFactory(ConnString), new MetadataLoader(Rules)).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string table, string where)
    {
        await using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM {table} WHERE {where}", _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private sealed record PendingRow(
        string Table, string Op, string Payload, string? Requester, string? Tenant, string State);

    private async Task<List<PendingRow>> PendingRowsAsync()
    {
        var rows = new List<PendingRow>();
        await using var cmd = new SqliteCommand(
            "SELECT \"table\", op, intended_payload, requester, tenant, \"state\" FROM pending_changes ORDER BY id",
            _keepAlive);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new PendingRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5)));
        return rows;
    }

    private static Dictionary<string, JsonElement> Payload(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static IDictionary<string, object?> TenantContext(int tenantId) =>
        new Dictionary<string, object?> { ["tenant_id"] = tenantId };

    /// <summary>
    /// Builds a single-row/batch intent executor wired with the built-in security/audit
    /// transformer chain (so tenant isolation applies) AND the approval intercept hook,
    /// exactly as the hosted registration composes them.
    /// </summary>
    private static MutationIntentExecutor BuildExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["connFactory"] = factory,
            });
        });

        return new MutationIntentExecutor(pathCache, BuiltInTransformers(), BuildHookProvider());
    }

    // The built-in security/audit transformer chain the server auto-prepends, so tenant
    // isolation shapes the intent BEFORE the approval gate serializes it.
    private static IMutationTransformers BuiltInTransformers() => new MutationTransformersWrap
    {
        Transformers = new IMutationTransformer[]
        {
            new PolicyMutationTransformer(),
            new StateMachineMutationTransformer(),
            new EnumValueMutationTransformer(),
            new SoftDeleteMutationTransformer(),
            new TenantMutationTransformer(),
            new AuditMutationTransformer(),
            new ConcurrencyMutationTransformer(),
        },
    };

    // The before-commit hook composite, built from every registered hook exactly as the
    // host DI does — here the single approval intercept hook.
    private static ServiceProvider BuildHookProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBeforeCommitMutationHook, ApprovalInterceptMutationHook>();
        services.AddSingleton(sp => new BeforeCommitMutationHooks(
            sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Insert_OnGatedTable_EnqueuesExactlyOnePendingChange_AndChangesNoTargetRow()
    {
        var executor = BuildExecutor();

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["name"] = "gated-insert", ["tenant_id"] = 1 },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        // The gate vetoes the write, surfaced as an execution error saying the change is
        // pending approval — never reported as success-as-applied.
        await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*pending approval*");

        // ZERO rows added to the target table (only the seed remains), EXACTLY ONE pending row.
        (await CountAsync("orders", "1 = 1")).Should().Be(1, "the gated insert never reached the target table");
        (await CountAsync("orders", "name = 'gated-insert'")).Should().Be(0);

        var pending = await PendingRowsAsync();
        pending.Should().ContainSingle();
        pending[0].Table.Should().Be("main.orders");
        pending[0].Op.Should().Be("insert");
        pending[0].State.Should().Be(PendingChangeStore.StatePending);
    }

    // ---- criterion 2: every single-row verb enqueues rather than applies ----

    [Fact]
    public async Task Update_OnGatedTable_Enqueues_AndChangesNoTargetRow()
    {
        var executor = BuildExecutor();

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["name"] = "renamed" },
            PrimaryKey = new object?[] { 10 },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*pending approval*");
        (await CountAsync("orders", "id = 10 AND name = 'seed-order'")).Should().Be(1, "the update never applied");
        (await CountAsync("orders", "name = 'renamed'")).Should().Be(0);

        var pending = await PendingRowsAsync();
        pending.Should().ContainSingle();
        pending[0].Op.Should().Be("update");
    }

    [Fact]
    public async Task Delete_OnGatedTable_Enqueues_AndRemovesNoTargetRow()
    {
        var executor = BuildExecutor();

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?>(),
            PrimaryKey = new object?[] { 10 },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*pending approval*");
        (await CountAsync("orders", "id = 10")).Should().Be(1, "the delete never applied");

        var pending = await PendingRowsAsync();
        pending.Should().ContainSingle();
        pending[0].Op.Should().Be("delete");
    }

    [Fact]
    public async Task Batch_OnGatedTable_EnqueuesEachAction_AndAppliesNone()
    {
        var executor = BuildExecutor();

        var act = () => executor.ExecuteBatchAsync(new MutationBatchIntent
        {
            Table = "orders",
            Actions = new[]
            {
                new MutationBatchAction(MutationIntentAction.Insert,
                    new Dictionary<string, object?> { ["name"] = "batch-a", ["tenant_id"] = 1 }),
                new MutationBatchAction(MutationIntentAction.Insert,
                    new Dictionary<string, object?> { ["name"] = "batch-b", ["tenant_id"] = 1 }),
            },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*pending approval*");
        // No rows applied (only the seed remains); one pending row PER action.
        (await CountAsync("orders", "1 = 1")).Should().Be(1, "no batch action reached the target table");
        var pending = await PendingRowsAsync();
        pending.Should().HaveCount(2, "each batch action enqueues its own pending change");
        pending.Should().OnlyContain(p => p.Op == "insert" && p.State == PendingChangeStore.StatePending);
    }

    [Fact]
    public async Task TreeSync_OnGatedTable_EnqueuesEveryNode_AndAppliesNone()
    {
        // A nested sync of two gated tables (blogs + posts): every node diverts, so nothing
        // is applied to either table and one pending row lands per node.
        var result = await ExecuteGraphQlAsync(
            "mutation { blogs(sync: { name: \"B\", posts: [ { title: \"first\" } ] }) }");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Single().Message.Should().Contain("pending approval");
        (await CountAsync("blogs", "1 = 1")).Should().Be(0, "no gated tree node reached the target table");
        (await CountAsync("posts", "1 = 1")).Should().Be(0);

        var pending = await PendingRowsAsync();
        pending.Should().HaveCount(2, "the blog and its post each enqueue a pending change");
        pending.Select(p => p.Table).Should().BeEquivalentTo(new[] { "main.blogs", "main.posts" });
    }

    // ---- criterion 3: enqueued payload is the POST-transformer scoped intent ----

    [Fact]
    public async Task EnqueuedPayload_CarriesTheScopedIntent_NotRawClientInput()
    {
        var executor = BuildExecutor();

        // The caller tries to plant the row in tenant 999; the tenant transformer pins it to
        // the caller's tenant (1) BEFORE the gate serializes it, so the enqueued payload can
        // never carry the out-of-scope value a slice-3 replay would otherwise resurrect.
        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["name"] = "scoped", ["tenant_id"] = 999 },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>();

        var pending = await PendingRowsAsync();
        pending.Should().ContainSingle();
        var payload = Payload(pending[0].Payload);
        payload["tenant_id"].GetInt32().Should().Be(1, "the payload carries the pinned tenant, not the client's 999");
        payload["name"].GetString().Should().Be("scoped");
    }

    // ---- criterion 4: requester + tenant captured from the caller's context ----

    [Fact]
    public async Task EnqueuedPending_CarriesRequesterAndTenant_FromUserContext()
    {
        var executor = BuildExecutor();

        var context = TenantContext(1);
        context["user_id"] = "alice"; // resolved by the root user-audit-key

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["name"] = "who", ["tenant_id"] = 1 },
            UserContext = context,
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>();

        var pending = await PendingRowsAsync();
        pending.Should().ContainSingle();
        pending[0].Requester.Should().Be("alice", "the requester is the caller, not the approver");
        pending[0].Tenant.Should().Be("1", "the requester's tenant is persisted for replay scoping");
    }

    [Fact]
    public async Task GraphQlApprovalMutations_ReplayUnderRequesterContext_AndRejectWithoutWriting()
    {
        var executor = BuildExecutor();
        var requester = TenantContext(1);
        requester["user_id"] = "alice";
        requester["roles"] = new[] { "requester" };

        Func<Task> enqueueApproved = async () => await executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["name"] = "approved", ["tenant_id"] = 999 },
            UserContext = requester,
            Endpoint = EndpointPath,
        });
        await enqueueApproved.Should().ThrowAsync<BifrostExecutionError>();

        var approver = TenantContext(2);
        approver["user_id"] = "bob";
        approver["roles"] = new[] { "manager" };
        var approved = await ExecuteGraphQlAsync("mutation { approve(pendingChangeId: \"1\") }", approver);
        approved.Errors.Should().BeNullOrEmpty();
        (await CountAsync("orders", "name = 'approved' AND tenant_id = 1")).Should().Be(1,
            "the replay uses the persisted requester tenant, not the approver tenant");
        (await CountAsync("pending_changes", "\"state\" = 'approved' AND approver = 'bob'")).Should().Be(1);

        Func<Task> enqueueRejected = async () => await executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["name"] = "rejected", ["tenant_id"] = 1 },
            UserContext = requester,
            Endpoint = EndpointPath,
        });
        await enqueueRejected.Should().ThrowAsync<BifrostExecutionError>();

        var rejected = await ExecuteGraphQlAsync("mutation { reject(pendingChangeId: \"2\", reason: \"duplicate\") }", approver);
        rejected.Errors.Should().BeNullOrEmpty();
        (await CountAsync("orders", "name = 'rejected'")).Should().Be(0, "rejection never replays data");
        (await CountAsync("pending_changes", "\"state\" = 'rejected' AND approver = 'bob' AND reason = 'duplicate'")).Should().Be(1);
    }

    // ---- fail-closed: a gated write with no store table is refused, not applied ----

    [Fact]
    public async Task MissingStoreTable_FailsClosed_AndAppliesNoWrite()
    {
        await Exec("DROP TABLE pending_changes");
        // Rebuild the model without the store table so the gate cannot resolve it.
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
            return new Inputs(new Dictionary<string, object?> { ["model"] = model, ["connFactory"] = factory });
        });
        var executor = new MutationIntentExecutor(pathCache, BuiltInTransformers(), BuildHookProvider());

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["name"] = "no-store", ["tenant_id"] = 1 },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*not present in the model*");
        (await CountAsync("orders", "1 = 1")).Should().Be(1, "a gated write with no store must NOT reach the target table");
    }

    // The GraphQL harness for the TreeSync path: registers the approval intercept hook and the
    // before-commit composite in RequestServices, exactly as the host DI composes them, so the
    // nested sync runs the before-commit phase.
    private async Task<ExecutionResult> ExecuteGraphQlAsync(
        string mutation, IDictionary<string, object?>? userContext = null)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = Array.Empty<IMutationTransformer>(),
        });
        services.AddSingleton<IMutationIntentExecutor>(BuildExecutor());
        services.AddSingleton<IBeforeCommitMutationHook, ApprovalInterceptMutationHook>();
        services.AddSingleton(sp => new BeforeCommitMutationHooks(
            sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        await using var provider = services.BuildServiceProvider();

        var executor = new DocumentExecuter();
        return await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = userContext ?? new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            });
        });
    }
}
