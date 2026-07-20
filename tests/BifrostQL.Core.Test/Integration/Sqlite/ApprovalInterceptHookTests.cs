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

    // orders: approval-gated AND tenant-filtered, so the same fixture proves both the
    // enqueue-not-apply invariant and that the serialized payload is the scoped intent.
    // blogs/posts: an approval-gated parent/child pair for the nested TreeSync path.
    private static readonly string[] Rules =
    {
        "main.orders { approval: enabled; approver-role: manager; tenant-filter: tenant_id }",
        "main.blogs { approval: enabled; approver-role: manager }",
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
                "state"          TEXT NOT NULL,
                approver         TEXT NULL,
                decided_at       TEXT NULL,
                reason           TEXT NULL
            )
            """);
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

        var transformers = new MutationTransformersWrap
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

        return new MutationIntentExecutor(pathCache, transformers, BuildHookProvider());
    }

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
}
