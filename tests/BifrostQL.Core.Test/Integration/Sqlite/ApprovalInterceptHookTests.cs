using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Approval;
using BifrostQL.Core.Modules.Crypto;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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
    private static readonly EnvelopeKeyManager KeyManager = NewKeyManager();

    // orders: approval-gated AND tenant-filtered, so the same fixture proves both the
    // enqueue-not-apply invariant and that the serialized payload is the scoped intent.
    // blogs/posts: an approval-gated parent/child pair, both gated so the nested TreeSync
    // path diverts every node (no gated/ungated mixing). The root user-audit-key resolves
    // the requester from the caller's context.
    private static readonly string[] Rules =
    {
        ":root { user-audit-key: user_id }",
        "main.orders { approval: enabled; approver-role: manager; self-approve: false; tenant-filter: tenant_id; soft-delete: deleted_at }",
        "main.orders.secret { encrypt: aes-256-gcm; key-ref: config:approval; blind-index: secret_bidx }",
        "main.orders.created_by { populate: created-by }",
        "main.orders.updated_by { populate: updated-by }",
        "main.blogs { approval: enabled; approver-role: manager }",
        "main.posts { approval: enabled; approver-role: manager }",
        "main.gated_posts { approval: enabled; approver-role: manager }",
        "main.pending_changes { state-column: state; initial-state: pending; states: pending, approved, rejected, expired; transitions: pending->approved|pending->rejected|pending->expired }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        foreach (var drop in new[] { "gated_posts", "ungated_blogs", "orders", "pending_changes", "posts", "blogs" })
            await Exec($"DROP TABLE IF EXISTS {drop}");

        await Exec(
            """
            CREATE TABLE orders (
                id         INTEGER PRIMARY KEY,
                tenant_id  INTEGER NOT NULL,
                name       TEXT NOT NULL UNIQUE,
                secret     TEXT NULL,
                secret_bidx TEXT NULL,
                deleted_at TEXT NULL,
                created_by TEXT NULL,
                updated_by TEXT NULL
            )
            """);
        await Exec("INSERT INTO orders(id, tenant_id, name) VALUES (10, 1, 'seed-order')");

        await Exec("CREATE TABLE blogs (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await Exec("CREATE TABLE ungated_blogs (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await Exec(
            """
            CREATE TABLE gated_posts (
                id      INTEGER PRIMARY KEY,
                blog_id INTEGER NOT NULL REFERENCES ungated_blogs(id),
                title   TEXT NOT NULL
            )
            """);
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
        string Table, string Op, string Payload, string? Requester, string? Tenant, string State,
        string? RequesterContext);

    private async Task<List<PendingRow>> PendingRowsAsync()
    {
        var rows = new List<PendingRow>();
        await using var cmd = new SqliteCommand(
            "SELECT \"table\", op, intended_payload, requester, tenant, \"state\", requester_context FROM pending_changes ORDER BY id",
            _keepAlive);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new PendingRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
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

        var services = BuildHookProvider();
        return new MutationIntentExecutor(pathCache, BuiltInTransformers(), services);
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
            new EncryptOnWriteMutationTransformer(),
        },
    };

    // The before-commit hook composite, built from every registered hook exactly as the
    // host DI does — here the single approval intercept hook.
    private static ServiceProvider BuildHookProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(KeyManager);
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
        pending[0].Op.Should().Be("delete", "approval authorization and replay retain the caller's logical delete action");
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

    [Fact]
    public async Task TreeSync_MixedGatedAndUngatedNodes_CommitsUngatedNode_AndQueuesGatedNode()
    {
        // Approval is a divert, not a transaction veto: an ungated parent can commit while
        // its gated child is queued. Pin this intentionally so callers use a fully gated tree
        // when they require all-or-nothing approval semantics.
        var factory = new SqliteDbConnFactory(ConnString);
        var services = BuildHookProvider();
        var executor = new TreeSyncExecutor(factory.Dialect);
        var ungatedBlog = _model.GetTableFromDbName("ungated_blogs");
        var gatedPost = _model.GetTableFromDbName("gated_posts");
        var parent = new TreeSyncOperation
        {
            Table = ungatedBlog,
            OperationType = TreeSyncOperationType.Insert,
            Data = new Dictionary<string, object?> { ["name"] = "applied-parent" },
            Depth = 0,
        };
        var child = new TreeSyncOperation
        {
            Table = gatedPost,
            OperationType = TreeSyncOperationType.Insert,
            Data = new Dictionary<string, object?> { ["title"] = "queued-child" },
            ForeignKeyAssignments = new Dictionary<string, string> { ["blog_id"] = ungatedBlog.GraphQlName },
            ParentInstanceId = parent.InstanceId,
            Depth = 1,
        };

        var act = () => executor.ExecuteAsync(
            new[] { parent, child }, factory, new MutationTransformersWrap
            {
                Transformers = Array.Empty<IMutationTransformer>(),
            }, _model, new Dictionary<string, object?>(), services);

        await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*pending approval*");
        (await CountAsync("ungated_blogs", "name = 'applied-parent'")).Should().Be(1);
        (await CountAsync("gated_posts", "title = 'queued-child'")).Should().Be(0);
        var pending = await PendingRowsAsync();
        pending.Should().ContainSingle(p => p.Table == "main.gated_posts" && p.Op == "insert");
        Payload(pending.Single().Payload)["blog_id"].GetInt64().Should().BeGreaterThan(0,
            "the queued child retains the resolved FK of the committed ungated parent");
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

    // ---- regression: an AUTHENTICATED caller's context carries a raw ClaimsPrincipal ----

    [Fact]
    public async Task EnqueuedPending_FromAuthenticatedPrincipalContext_Succeeds_AndProjectsPlainRequesterContext()
    {
        var executor = BuildExecutor();

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["name"] = "principal", ["tenant_id"] = 1 },
            UserContext = PrincipalRequesterContext(),
            Endpoint = EndpointPath,
        });

        // The gate must DIVERT (pending approval), not fail on serializing the principal.
        var thrown = await act.Should().ThrowAsync<BifrostExecutionError>();
        thrown.Which.ErrorCode.Should().Be(ApprovalInterceptMutationHook.PendingApprovalCode,
            "an authenticated caller's gated write must divert, not fail on requester-context serialization");

        var pending = await PendingRowsAsync();
        pending.Should().ContainSingle();
        pending[0].Requester.Should().Be("alice");

        var requesterContext = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            pending[0].RequesterContext!)!;
        requesterContext.Keys.Should().BeEquivalentTo(new[] { "user_id", "roles", "tenant_id" },
            "only the fields the replay consumes are persisted");
        requesterContext["user_id"].GetString().Should().Be("alice");
        requesterContext["tenant_id"].GetInt64().Should().Be(1);
        requesterContext["roles"].EnumerateArray().Select(role => role.GetString())
            .Should().BeEquivalentTo(new[] { "requester" });
        pending[0].RequesterContext.Should().NotContain("alice@example.com",
            "claim PII outside the replay contract must not be persisted");
        pending[0].RequesterContext.Should().NotContain("Claims",
            "the raw principal must never be serialized into the store");
    }

    [Fact]
    public async Task GraphQlApprove_ReplaysChangeEnqueuedByAuthenticatedPrincipalCaller()
    {
        var executor = BuildExecutor();
        await EnqueueAsync(executor, "principal-approved", PrincipalRequesterContext());

        var approver = ApproverContext("bob", "manager");
        var approved = await ExecuteGraphQlAsync("mutation { approve(pendingChangeId: \"1\") }", approver);

        approved.Errors.Should().BeNullOrEmpty();
        (await CountAsync("orders", "name = 'principal-approved' AND tenant_id = 1")).Should().Be(1,
            "the replay runs under the persisted REQUESTER tenant, not the approver's");
        (await CountAsync("orders", "name = 'principal-approved' AND created_by = 'bob'")).Should().Be(1,
            "the approver remains the audit actor");
        (await CountAsync("pending_changes", "\"state\" = 'approved' AND approver = 'bob'")).Should().Be(1);
    }

    [Fact]
    public async Task GraphQlApprove_SelfApprover_IsDenied_AndLeavesChangePending()
    {
        var executor = BuildExecutor();
        var alice = ApproverContext("alice", "manager");
        await EnqueueAsync(executor, "self-denied", alice);

        var result = await ExecuteGraphQlAsync("mutation { approve(pendingChangeId: \"1\") }", alice);

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Single().Message.Should().Contain("cannot approve their own");
        (await CountAsync("orders", "name = 'self-denied'")).Should().Be(0);
        (await CountAsync("pending_changes", "id = 1 AND \"state\" = 'pending'")).Should().Be(1);
    }

    [Fact]
    public async Task GraphQlApprove_DeleteDeniedByPolicy_StaysPendingDespiteSoftDeleteRewrite()
    {
        var executor = BuildExecutor();
        Func<Task> enqueue = async () => await executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders", Action = MutationIntentAction.Delete, Data = new Dictionary<string, object?>(),
            PrimaryKey = new object?[] { 10 }, UserContext = RequesterContext(), Endpoint = EndpointPath,
        });
        await enqueue.Should().ThrowAsync<BifrostExecutionError>();

        ((DbTable)_model.GetTableFromDbName("orders")).Metadata["policy-actions"] = "read,update";
        var result = await ExecuteGraphQlAsync("mutation { approve(pendingChangeId: \"1\") }", ApproverContext("bob", "manager"));

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Single().Message.Should().Contain("not authorized to approve");
        (await CountAsync("orders", "id = 10 AND deleted_at IS NULL")).Should().Be(1);
        (await CountAsync("pending_changes", "id = 1 AND \"state\" = 'pending'")).Should().Be(1);
    }

    [Fact]
    public async Task GraphQlApprove_CallerWithoutApproverRole_IsDenied_AndLeavesChangePending()
    {
        var executor = BuildExecutor();
        await EnqueueAsync(executor, "role-denied", RequesterContext());

        var result = await ExecuteGraphQlAsync("mutation { approve(pendingChangeId: \"1\") }", ApproverContext("bob", "requester"));

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Single().Message.Should().Contain("not an approval-role holder");
        (await CountAsync("orders", "name = 'role-denied'")).Should().Be(0);
        (await CountAsync("pending_changes", "id = 1 AND \"state\" = 'pending'")).Should().Be(1);
    }

    [Fact]
    public async Task GraphQlApprove_UnevaluablePolicy_IsDenied_AndLeavesChangePending()
    {
        var executor = BuildExecutor();
        await EnqueueAsync(executor, "policy-denied", RequesterContext());
        ((DbTable)_model.GetTableFromDbName("orders")).Metadata["policy-actions"] = "invalid";

        var result = await ExecuteGraphQlAsync("mutation { approve(pendingChangeId: \"1\") }", ApproverContext("bob", "manager"));

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Single().Message.Should().Contain("policy could not be evaluated");
        (await CountAsync("orders", "name = 'policy-denied'")).Should().Be(0);
        (await CountAsync("pending_changes", "id = 1 AND \"state\" = 'pending'")).Should().Be(1);
    }

    [Fact]
    public async Task GraphQlApprove_ReplaysEncryptionAndSoftDeleteTransformers()
    {
        var executor = BuildExecutor();
        await EnqueueAsync(executor, "encrypted", RequesterContext(), secret: "approval-secret");
        var approver = ApproverContext("bob", "manager");

        (await ExecuteGraphQlAsync("mutation { approve(pendingChangeId: \"1\") }", approver)).Errors.Should().BeNullOrEmpty();
        var (secret, blindIndex) = await ReadSecretAsync("encrypted");
        secret.Should().NotBe("approval-secret");
        FieldCipher.Decrypt(KeyManager.GetDataKey("config:approval"), secret!, CryptoAad.Build("main", "orders", "secret"))
            .Should().Be("approval-secret", "approved replay preserves the queued ciphertext for normal one-decrypt reads");
        blindIndex.Should().Be(BlindIndexComputer.Compute(KeyManager.GetBlindIndexKey("config:approval"), "approval-secret"));

        Func<Task> enqueueDelete = async () => await executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders", Action = MutationIntentAction.Delete, Data = new Dictionary<string, object?>(),
            PrimaryKey = new object?[] { 11 }, UserContext = RequesterContext(), Endpoint = EndpointPath,
        });
        await enqueueDelete.Should().ThrowAsync<BifrostExecutionError>();
        (await ExecuteGraphQlAsync("mutation { approve(pendingChangeId: \"2\") }", approver)).Errors.Should().BeNullOrEmpty();
        (await CountAsync("orders", "id = 11 AND deleted_at IS NOT NULL")).Should().Be(1);
    }

    [Fact]
    public async Task ExpiredPendingChange_CannotBeApproved_AndUsesTheStateMachineTransition()
    {
        var executor = BuildExecutor();
        await EnqueueAsync(executor, "expired", RequesterContext());
        var service = new ApprovalDecisionService(_model, new SqliteDbConnFactory(ConnString), executor);

        await service.ExpireAsync(1, "approval window elapsed", CancellationToken.None);

        (await CountAsync("pending_changes", "id = 1 AND \"state\" = 'expired' AND reason = 'approval window elapsed'")).Should().Be(1);
        var approval = await ExecuteGraphQlAsync("mutation { approve(pendingChangeId: \"1\") }", ApproverContext("bob", "manager"));
        approval.Errors.Should().NotBeNullOrEmpty();
        approval.Errors!.Single().Message.Should().Contain("already been decided");
        (await CountAsync("orders", "name = 'expired'")).Should().Be(0);
    }

    [Fact]
    public async Task GraphQlApprove_ReplayFailure_RollsBackTargetAndPendingTransition()
    {
        var executor = BuildExecutor();
        await EnqueueAsync(executor, "seed-order", RequesterContext());

        var result = await ExecuteGraphQlAsync("mutation { approve(pendingChangeId: \"1\") }", ApproverContext("bob", "manager"));

        result.Errors.Should().NotBeNullOrEmpty();
        (await CountAsync("orders", "name = 'seed-order'")).Should().Be(1, "the failed replay cannot insert a second target row");
        (await CountAsync("pending_changes", "id = 1 AND \"state\" = 'pending'")).Should().Be(1, "the failed replay rolls back the approval transition");
    }

    [Fact]
    public async Task RejectAsync_UsesMutationPipelineWithApproverAndReason()
    {
        var executor = BuildExecutor();
        await EnqueueAsync(executor, "pipeline-rejected", RequesterContext());
        var mutations = Substitute.For<IMutationIntentExecutor>();
        mutations.ExecuteAsync(Arg.Any<MutationIntent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MutationIntentResult { AffectedRows = 1 }));
        var approver = ApproverContext("bob", "manager");
        var service = new ApprovalDecisionService(_model, new SqliteDbConnFactory(ConnString), mutations);

        await service.RejectAsync(1, "duplicate", approver);

        await mutations.Received(1).ExecuteAsync(Arg.Is<MutationIntent>(intent =>
            intent.Table == PendingChangeStore.TableName &&
            intent.Action == MutationIntentAction.Update &&
            intent.PrimaryKey != null && intent.PrimaryKey.SequenceEqual(new object?[] { 1L }) &&
            Equals(intent.Data[PendingChangeStore.ColState], PendingChangeStore.StateRejected) &&
            Equals(intent.Data[PendingChangeStore.ColApprover], "bob") &&
            Equals(intent.Data[PendingChangeStore.ColReason], "duplicate") &&
            ReferenceEquals(intent.UserContext, approver)), Arg.Any<CancellationToken>());
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
        (await CountAsync("orders", "name = 'approved' AND created_by = 'bob' AND updated_by = 'bob'")).Should().Be(1,
            "the approved replay stamps the approver as the audit actor while retaining requester scope");
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

    private static IDictionary<string, object?> RequesterContext()
        => new Dictionary<string, object?> { ["tenant_id"] = 1, ["user_id"] = "alice", ["roles"] = new[] { "requester" } };

    /// <summary>
    /// The user context an AUTHENTICATED GraphQL caller actually carries: the projected
    /// identity keys PLUS the raw <see cref="ClaimsPrincipal"/> under <c>"user"</c> and the
    /// per-claim-type arrays, exactly as <c>BifrostContext</c> builds it. A dictionary-only
    /// fixture cannot manifest the principal-serialization defect (a
    /// <see cref="Claim.Subject"/> back-reference is an object cycle), so every requester
    /// context assertion needs this variant to be non-vacuous.
    /// </summary>
    private static IDictionary<string, object?> PrincipalRequesterContext(
        string userId = "alice", string role = "requester", int tenantId = 1)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Email, $"{userId}@example.com"),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.Role, role),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        var context = new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId,
            ["user_id"] = userId,
            ["roles"] = new[] { role },
            ["user"] = principal,
        };
        foreach (var group in principal.Claims.GroupBy(claim => claim.Type))
            if (!context.ContainsKey(group.Key))
                context[group.Key] = group.Select(claim => claim.Value).ToArray();
        return context;
    }

    private static IDictionary<string, object?> ApproverContext(string userId, string role)
        => new Dictionary<string, object?> { ["tenant_id"] = 2, ["user_id"] = userId, ["roles"] = new[] { role } };

    private static EnvelopeKeyManager NewKeyManager()
    {
        var rootKey = Enumerable.Range(1, FieldCipher.KeySize).Select(value => (byte)value).ToArray();
        return new EnvelopeKeyManager(new ConfigRootKeyProvider(rootKey), new InMemoryDataEncryptionKeyStore());
    }

    private async Task EnqueueAsync(MutationIntentExecutor executor, string name, IDictionary<string, object?> requester, string? secret = null)
    {
        var data = new Dictionary<string, object?> { ["name"] = name, ["tenant_id"] = 1 };
        if (secret is not null) data["secret"] = secret;
        Func<Task> enqueue = async () => await executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders", Action = MutationIntentAction.Insert, Data = data,
            UserContext = requester, Endpoint = EndpointPath,
        });
        await enqueue.Should().ThrowAsync<BifrostExecutionError>();
    }

    private async Task<(string? Secret, string? BlindIndex)> ReadSecretAsync(string name)
    {
        await using var command = new SqliteCommand("SELECT secret, secret_bidx FROM orders WHERE name = $name", _keepAlive);
        command.Parameters.AddWithValue("$name", name);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
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
        services.AddSingleton(KeyManager);
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
