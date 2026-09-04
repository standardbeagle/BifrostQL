using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Finding M1 — the state-machine gate must not leak another tenant's current state.
///
/// <para>Two failures compounded. The mutation pipelines notified
/// <see cref="StateTransitionObservers"/> from the transformer's
/// <c>StateTransition</c> regardless of how many rows the write affected, so a
/// tenant-scoped-away UPDATE (zero rows) still published an event describing the
/// OTHER tenant's row. And the current-state load was keyed by primary key ONLY —
/// unscoped — so the state-machine transformer gated on a row the caller could not
/// write: a legal transition returned success while an illegal one returned a
/// denial, making the victim's stored state probeable one guess at a time
/// (.claude/rules/protocol-adapter-security.md invariant 2 — the response must not
/// vary with the target's existence or contents).</para>
///
/// <para>The fixture is deliberately two-tenant with a real state machine: neither
/// bug can manifest against a single-tenant or state-machine-free table
/// (.claude/rules/regression-test-non-vacuous.md).</para>
/// </summary>
public sealed class StateTransitionScopeTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_state_transition_scope_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;

    private static readonly string[] Rules =
    {
        "*.tickets { tenant-filter: tenant_id; state-column: status; initial-state: open; states: open,review,closed; transitions: open->review }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS tickets");
        await Exec(
            """
            CREATE TABLE tickets (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                status TEXT NOT NULL
            )
            """);
        await Exec(
            """
            INSERT INTO tickets(id, tenant_id, status) VALUES
                (1, 1, 'open'),
                (2, 2, 'open')
            """);
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    private static (MutationIntentExecutor Executor, CapturingObserver Observer) BuildExecutor()
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

        var observer = new CapturingObserver();
        var services = new SingleServiceProvider(
            new StateTransitionObservers(new IStateTransitionObserver[] { observer }));

        return (new MutationIntentExecutor(pathCache, transformers, services), observer);
    }

    private static IDictionary<string, object?> TenantContext(int tenantId) =>
        new Dictionary<string, object?> { ["tenant_id"] = tenantId };

    /// <summary>
    /// Collapses a mutation's whole observable answer — success value, affected
    /// rows, or the exception's type, message and error code — into one comparable
    /// string, so the anti-enumeration assertions compare the FULL response rather
    /// than one convenient field of it.
    /// </summary>
    private static async Task<string> ResponseOf(Func<Task<MutationIntentResult>> act)
    {
        try
        {
            var result = await act();
            return $"ok value={result.Value ?? "<null>"} affected={result.AffectedRows?.ToString() ?? "<null>"}";
        }
        catch (Exception ex)
        {
            var code = ex is BifrostExecutionError bifrost ? bifrost.ErrorCode ?? "<null>" : "<n/a>";
            return $"error type={ex.GetType().FullName} code={code} message={ex.Message}";
        }
    }

    private static Task<MutationIntentResult> UpdateStatus(
        MutationIntentExecutor executor, int tenantId, object key, string status) =>
        executor.ExecuteAsync(new MutationIntent
        {
            Table = "tickets",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["status"] = status },
            PrimaryKey = new[] { key },
            UserContext = TenantContext(tenantId),
            Endpoint = EndpointPath,
        });

    private static Task<MutationBatchIntentResult> BatchUpdateStatus(
        MutationIntentExecutor executor, int tenantId, object key, string status) =>
        executor.ExecuteBatchAsync(new MutationBatchIntent
        {
            Table = "tickets",
            Actions = new[]
            {
                new MutationBatchAction(MutationIntentAction.Update,
                    new Dictionary<string, object?> { ["id"] = key, ["status"] = status }),
            },
            UserContext = TenantContext(tenantId),
            Endpoint = EndpointPath,
        });

    // ---- single-row pipeline ------------------------------------------------

    [Fact]
    public async Task Update_InTenant_ValidTransition_EmitsTransitionEvent()
    {
        // Positive control. Without it, "emit nothing, ever" would pass every
        // cross-tenant assertion below while silently killing the feature.
        var (executor, observer) = BuildExecutor();

        var result = await UpdateStatus(executor, tenantId: 1, key: 1, status: "review");

        result.AffectedRows.Should().Be(1);
        (await ScalarAsync("SELECT status FROM tickets WHERE id = 1")).Should().Be("review");
        var transition = observer.Transitions.Should().ContainSingle().Subject;
        transition.From.Should().Be("open");
        transition.To.Should().Be("review");
    }

    [Fact]
    public async Task Update_CrossTenant_EmitsNoTransitionEvent_AndLeavesRowUntouched()
    {
        var (executor, observer) = BuildExecutor();

        // Tenant 1 addresses tenant 2's ticket with a transition that IS legal for
        // the stored state. The write is scoped away (zero rows), so no event may
        // describe it — the observer chain is a real side channel (webhooks, CDC,
        // workflow triggers) carrying the victim's from/to states.
        await ResponseOf(() => UpdateStatus(executor, tenantId: 1, key: 2, status: "review"));

        observer.Transitions.Should().BeEmpty("a zero-row update changed no state");
        (await ScalarAsync("SELECT status FROM tickets WHERE id = 2")).Should().Be("open");
    }

    [Fact]
    public async Task Update_CrossTenant_AnswersIdenticallyForValidInvalidAndMissingRows()
    {
        var (executor, _) = BuildExecutor();

        // open->review is declared; open->closed is not. Both target tenant 2's row
        // from tenant 1, and id 9999 exists for nobody. If the responses differ, the
        // caller can walk the state space of a row it cannot read.
        var validTransition = await ResponseOf(() => UpdateStatus(executor, tenantId: 1, key: 2, status: "review"));
        var invalidTransition = await ResponseOf(() => UpdateStatus(executor, tenantId: 1, key: 2, status: "closed"));
        var missingRow = await ResponseOf(() => UpdateStatus(executor, tenantId: 1, key: 9999, status: "review"));

        validTransition.Should().Be(invalidTransition,
            "a legal transition on an unreachable row must not be distinguishable from an illegal one");
        validTransition.Should().Be(missingRow,
            "an out-of-scope row must read exactly like a row that does not exist");
        (await ScalarAsync("SELECT status FROM tickets WHERE id = 2")).Should().Be("open");
    }

    // ---- batch pipeline -----------------------------------------------------

    [Fact]
    public async Task Batch_InTenant_ValidTransition_EmitsTransitionEvent()
    {
        var (executor, observer) = BuildExecutor();

        var result = await BatchUpdateStatus(executor, tenantId: 1, key: 1, status: "review");

        result.TotalAffected.Should().Be(1);
        (await ScalarAsync("SELECT status FROM tickets WHERE id = 1")).Should().Be("review");
        observer.Transitions.Should().ContainSingle().Which.To.Should().Be("review");
    }

    [Fact]
    public async Task Batch_CrossTenant_EmitsNoTransitionEvent_AndLeavesRowUntouched()
    {
        var (executor, observer) = BuildExecutor();

        try
        {
            await BatchUpdateStatus(executor, tenantId: 1, key: 2, status: "review");
        }
        catch (BifrostExecutionError)
        {
            // The scoped state load reads the unreachable row as absent, so the
            // state-machine transformer denies and the batch rolls back. Either way
            // no event may describe tenant 2's row.
        }

        observer.Transitions.Should().BeEmpty("a zero-row batch update changed no state");
        (await ScalarAsync("SELECT status FROM tickets WHERE id = 2")).Should().Be("open");
    }

    [Fact]
    public async Task Batch_CrossTenant_AnswersIdenticallyForValidInvalidAndMissingRows()
    {
        var (executor, _) = BuildExecutor();

        var validTransition = await BatchResponseOf(() => BatchUpdateStatus(executor, 1, 2, "review"));
        var invalidTransition = await BatchResponseOf(() => BatchUpdateStatus(executor, 1, 2, "closed"));
        var missingRow = await BatchResponseOf(() => BatchUpdateStatus(executor, 1, 9999, "review"));

        validTransition.Should().Be(invalidTransition,
            "a legal transition on an unreachable row must not be distinguishable from an illegal one");
        validTransition.Should().Be(missingRow,
            "an out-of-scope row must read exactly like a row that does not exist");
        (await ScalarAsync("SELECT status FROM tickets WHERE id = 2")).Should().Be("open");
    }

    private static async Task<string> BatchResponseOf(Func<Task<MutationBatchIntentResult>> act)
    {
        try
        {
            var result = await act();
            return $"ok affected={result.TotalAffected}";
        }
        catch (Exception ex)
        {
            var code = ex is BifrostExecutionError bifrost ? bifrost.ErrorCode ?? "<null>" : "<n/a>";
            return $"error type={ex.GetType().FullName} code={code} message={ex.Message}";
        }
    }

    private sealed class CapturingObserver : IStateTransitionObserver
    {
        public List<StateTransitionInfo> Transitions { get; } = new();

        public ValueTask OnTransitionAsync(
            StateTransitionInfo transition,
            IDictionary<string, object?> userContext)
        {
            Transitions.Add(transition);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly StateTransitionObservers _observers;

        public SingleServiceProvider(StateTransitionObservers observers) => _observers = observers;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(StateTransitionObservers) ? _observers : null;
    }
}
