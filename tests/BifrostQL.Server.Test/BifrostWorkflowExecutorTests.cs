using System.Security.Claims;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Workflows;
using BifrostQL.Model;
using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// End-to-end proof that <see cref="BifrostWorkflowExecutor"/> — the primitive
/// behind sidecar workflow endpoints — runs every query and mutation through the
/// SAME GraphQL pipeline as a direct <c>/graphql</c> request. The system under
/// test is the real executor wired to a real <see cref="DocumentExecuter"/> with
/// <see cref="PolicyFilterTransformer"/>, <see cref="PolicyMutationTransformer"/>,
/// and <see cref="TenantFilterTransformer"/> in request services — so policy
/// enforcement and tenant isolation are proven to apply, not bypassed.
///
/// Self-contained: a shared-cache in-memory SQLite database loaded via
/// <see cref="DbModelLoader"/>, mirroring the PolicyBypassPreventionTests setup.
/// </summary>
[Collection("PolicyBypassPrevention")]
public sealed class BifrostWorkflowExecutorTests : IAsyncLifetime
{
    private const string GraphQlPath = "/graphql";

    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private IDbModel _model = null!;
    private ISchema _schema = null!;
    private ProfileModelCache _profileCache = null!;
    private BifrostProfileRegistry _profileRegistry = null!;

    // The "policy" profile activates the built-in policy + tenant transformers.
    // The empty default profile runs raw (no opt-in modules), so these executor
    // proofs select a profile that lists those modules.
    private const string ProfileName = "policy";

    // Orders permits read + update and denies the "delete" action; it is
    // tenant-scoped for non-admins. audit_log is read-only to clients
    // (policy-actions: read) — a client insert is rejected, proving the
    // executor traverses PolicyMutationTransformer.
    private static readonly string[] PolicyMetadata =
    {
        ":root { policy-admin-role: admin }",
        "main.Orders { policy-actions: read,update }",
        "main.audit_log { policy-actions: read }",
        "main.audit_log { tenant-filter: tenant_id }",
        "main.tickets { policy-actions: read,update }",
    };

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_workflow_exec_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();

        _connFactory = new SqliteDbConnFactory(_connectionString);

        await using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync();
            var ddl = new SqliteCommand(
                @"CREATE TABLE Orders (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tenant_id INTEGER NOT NULL,
                    total REAL NOT NULL
                );
                CREATE TABLE audit_log (
                    audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tenant_id INTEGER NOT NULL,
                    action TEXT NOT NULL
                );
                CREATE TABLE tags (
                    tag_id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    weight INTEGER NOT NULL
                );
                CREATE TABLE tickets (
                    ticket_id INTEGER PRIMARY KEY,
                    title TEXT NOT NULL,
                    status TEXT NOT NULL,
                    title_upper TEXT GENERATED ALWAYS AS (upper(title)) VIRTUAL
                );", conn);
            await ddl.ExecuteNonQueryAsync();

            var seed = new SqliteCommand(
                @"INSERT INTO Orders (tenant_id, total) VALUES (1, 10.0), (2, 20.0);
                  INSERT INTO audit_log (tenant_id, action) VALUES (1, 'seed.tenant1'), (2, 'seed.tenant2');
                  INSERT INTO tags (tag_id, name, weight) VALUES (1, 'red', 1);
                  INSERT INTO tickets (ticket_id, title, status) VALUES (1, 'one', 'open'), (2, 'two', 'open'), (3, 'three', 'closed');", conn);
            await seed.ExecuteNonQueryAsync();
        }

        var metadataLoader = new MetadataLoader(PolicyMetadata);
        var loader = new DbModelLoader(_connFactory, metadataLoader);

        _profileRegistry = new BifrostProfileRegistry();
        _profileRegistry.Add(new BifrostProfile
        {
            Name = ProfileName,
            Modules = new[] { "policy", "tenant-filter" },
        });

        // The executor drives the same per-profile pipeline as the HTTP endpoint;
        // build the shared-read cache once and apply the row-scope expression to
        // the cached profile model ('{ }' is reserved in the rule grammar).
        var read = await loader.ReadAsync();
        _profileCache = new ProfileModelCache(loader, read, PolicyMetadata, null, _profileRegistry);
        (_model, _schema) = _profileCache.GetFor(ProfileName);
        _model.GetTableFromDbName("Orders")
            .Metadata[MetadataKeys.Policy.RowScope] = "tenant_id = {tenant_id}";
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    /// <summary>
    /// Builds the request services the executor depends on, wiring the policy
    /// and tenant transformers exactly as the production host configures them.
    /// </summary>
    private ServiceProvider BuildRequestServices()
    {
        var filterTransformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new PolicyFilterTransformer(),
                new TenantFilterTransformer(),
            },
        };

        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(GraphQlPath, () => Task.FromResult(new Inputs(new Dictionary<string, object?>
        {
            { "connFactory", _connFactory },
            { "model", _model },
            { "dbSchema", _schema },
            { "profileModelCache", _profileCache },
        })));

        var services = new ServiceCollection();
        services.AddSingleton(_profileRegistry);
        services.AddSingleton<IFilterTransformers>(filterTransformers);
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new PolicyMutationTransformer(),
            },
        });
        services.AddSingleton<IQueryTransformerService>(new QueryTransformerService(filterTransformers));
        services.AddSingleton(pathCache);
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Constructs the executor against a real <see cref="DocumentExecuter"/>,
    /// and a user context for the given role/tenant — the same context shape
    /// <see cref="HttpContextWorkflowExtensions.GetBifrostUserContext"/> yields.
    /// </summary>
    private (BifrostWorkflowExecutor Executor, IDictionary<string, object?> UserContext, ServiceProvider Provider)
        BuildExecutor(string role, int tenantId)
    {
        var provider = BuildRequestServices();
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        // The middleware resolves the active profile from the request; the workflow
        // executor reuses this HttpContext, so select the policy profile here.
        httpContext.Request.QueryString = new QueryString($"?profile={ProfileName}");
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;

        var executor = new BifrostWorkflowExecutor(
            new DocumentExecuter(),
            provider.GetRequiredService<PathCache<Inputs>>(),
            provider);

        var userContext = BuildUserContext(role, tenantId);
        return (executor, userContext, provider);
    }

    private static IDictionary<string, object?> BuildUserContext(string role, int tenantId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "user-1"),
            new(LocalAuthClaims.Provider, "local"),
            new(LocalAuthClaims.Tenant, tenantId.ToString()),
            new(ClaimTypes.Role, role),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
        var context = new DefaultHttpContext { User = principal };
        return context.GetBifrostUserContext();
    }

    // ---- The executor runs through the policy pipeline ----

    [Fact]
    public async Task InsertAsync_OnPolicyReadOnlyTable_RejectedByPolicyPipeline()
    {
        // audit_log carries policy-actions: read — a client insert must be
        // rejected by PolicyMutationTransformer. If the executor bypassed the
        // pipeline, this insert would silently succeed.
        var (executor, userContext, provider) = BuildExecutor(role: "user", tenantId: 1);
        await using var _ = provider;

        var act = () => executor.InsertAsync(
            "audit_log", new { tenant_id = 1, action = "membership.renewed" }, userContext);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "the workflow executor runs InsertAsync through PolicyMutationTransformer");
    }

    [Fact]
    public async Task UpdateAsync_OnPolicyReadOnlyTable_RejectedByPolicyPipeline()
    {
        // audit_log is read-only to clients (policy-actions: read) — an update
        // must be rejected by PolicyMutationTransformer, proving UpdateAsync
        // also traverses the policy pipeline.
        var (executor, userContext, provider) = BuildExecutor(role: "user", tenantId: 1);
        await using var _ = provider;

        var act = () => executor.UpdateAsync(
            "audit_log", new { audit_id = 1, action = "tampered" }, userContext);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "the workflow executor runs UpdateAsync through PolicyMutationTransformer");
    }

    [Fact]
    public async Task UpdateAsync_PermittedInTenantRow_Succeeds()
    {
        // Orders permits update and the row is in the caller's tenant — the
        // happy path completes without error (GREEN proof for the pipeline).
        var (executor, userContext, provider) = BuildExecutor(role: "user", tenantId: 1);
        await using var _ = provider;

        var act = () => executor.UpdateAsync(
            "Orders", new { Id = 1, tenant_id = 1, total = 11.0 }, userContext);

        await act.Should().NotThrowAsync("an in-tenant update is permitted by policy");
    }

    // ---- The executor is tenant-scoped ----

    [Fact]
    public async Task QuerySingleAsync_RowOutsideCallerTenant_ReturnsNull()
    {
        // Order 2 belongs to tenant 2; a tenant-1 caller must not see it. The
        // executor's read runs through PolicyFilterTransformer's row-scope and
        // the tenant-filter, so the row is invisible.
        var (executor, userContext, provider) = BuildExecutor(role: "user", tenantId: 1);
        await using var _ = provider;

        var row = await executor.QuerySingleAsync("Orders", 2, userContext);

        row.Should().BeNull("tenant-scoping hides another tenant's row from QuerySingleAsync");
    }

    [Fact]
    public async Task QuerySingleAsync_RowInsideCallerTenant_ReturnsRow()
    {
        // Order 1 belongs to tenant 1; the same tenant-1 caller sees it.
        var (executor, userContext, provider) = BuildExecutor(role: "user", tenantId: 1);
        await using var _ = provider;

        var row = await executor.QuerySingleAsync("Orders", 1, userContext);

        row.Should().NotBeNull();
        IdOf(row!).Should().Be((long)1);
        row!["tenant_id"].Should().Be((long)1);
    }

    [Fact]
    public async Task QuerySingleAsync_AdminRole_SeesRowAcrossTenants()
    {
        // An admin bypasses the row-scope policy, so QuerySingleAsync resolves
        // a row in another tenant — proving the executor honours the same
        // admin-bypass the direct GraphQL path does.
        var (executor, userContext, provider) = BuildExecutor(role: "admin", tenantId: 1);
        await using var _ = provider;

        var row = await executor.QuerySingleAsync("Orders", 2, userContext);

        row.Should().NotBeNull("an admin is not narrowed by the row-scope filter");
        IdOf(row!).Should().Be((long)2);
    }

    // ---- A workflow step's conflict is read from the error code ----

    /// <summary>
    /// Runs a one-step workflow that inserts <paramref name="valuesJson"/> into
    /// <c>tags</c> through the real executor, and returns the run's error code.
    /// </summary>
    private async Task<string?> RunTagInsertAsync(string valuesJson)
    {
        var (executor, userContext, provider) = BuildExecutor("admin", 1);
        await using var _ = provider;
        var workflow = new WorkflowDefinition
        {
            Name = "tag-insert",
            Trigger = new WorkflowTrigger { Type = "manual" },
            Steps = new[]
            {
                new WorkflowStep
                {
                    Name = "insert",
                    Type = "mutation",
                    Payload = JsonDocument.Parse(
                        $$"""{ "table": "tags", "action": "insert", "values": {{valuesJson}} }""").RootElement.Clone(),
                },
            },
        };
        var runner = new WorkflowRunner(
            new Dictionary<string, WorkflowDefinition> { [workflow.Name] = workflow }, executor);

        var result = await runner.RunAsync(workflow.Name, new Dictionary<string, object?>(), userContext);
        return result.Error?.Code;
    }

    [Fact]
    public async Task WorkflowRunner_DuplicateKeyInsert_FailsAsConflict()
    {
        var code = await RunTagInsertAsync("""{ "name": "red", "weight": 1 }""");

        code.Should().Be("conflict", "the unique name 'red' already exists, so the step lost to a conflicting record");
    }

    [Fact]
    public async Task WorkflowRunner_AValueEchoingTheConflictMessage_IsNotAConflict()
    {
        // weight is an integer column, so this insert fails on the value's type. The
        // failure text quotes the value, which is BifrostQL's own conflict message: a
        // runner that matched the text would report a conflict that never happened.
        var code = await RunTagInsertAsync(
            $$"""{ "name": "blue", "weight": "{{BifrostExecutionError.ConflictMessage}}" }""");

        code.Should().Be("step_failed", "a bad value is not a conflict, whatever text it carries");
    }

    /// <summary>
    /// Reads the Orders primary key from a returned row by the column's GraphQL
    /// name, so the assertion does not assume a particular name casing.
    /// </summary>
    private long IdOf(IDictionary<string, object?> row)
    {
        var idColumn = _model.GetTableFromDbName("Orders").Columns
            .First(c => string.Equals(c.DbName, "Id", StringComparison.OrdinalIgnoreCase));
        return Convert.ToInt64(row[idColumn.GraphQlName]);
    }

    // ---- A transition writes only the key and the state, guarded by the state it read ----

    private static WorkflowDefinition TicketTransition(string from, string to) => new()
    {
        Name = "close-ticket",
        Trigger = new WorkflowTrigger { Type = "manual" },
        Steps = new[]
        {
            new WorkflowStep
            {
                Name = "close",
                Type = "transition",
                Payload = JsonDocument.Parse(
                    """{ "table": "tickets", "id": "{{ inputs.id }}", "stateColumn": "status", "from": "__FROM__", "to": "__TO__" }"""
                        .Replace("__FROM__", from).Replace("__TO__", to))
                    .RootElement.Clone(),
            },
        },
    };

    private async Task<string> TicketStatusAsync(int id)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand($"SELECT status FROM tickets WHERE ticket_id = {id}", conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<WorkflowRunResult> RunTicketTransitionAsync(
        IWorkflowDataExecutor executor, IDictionary<string, object?> userContext, int id, string from, string to)
    {
        var workflow = TicketTransition(from, to);
        var runner = new WorkflowRunner(
            new Dictionary<string, WorkflowDefinition> { [workflow.Name] = workflow }, executor);
        return await runner.RunAsync(workflow.Name, new Dictionary<string, object?> { ["id"] = id }, userContext);
    }

    [Fact]
    public async Task Transition_OnATableWithAComputedColumn_Succeeds()
    {
        // tickets.title_upper is a generated column: the update input type leaves it out, so a
        // transition that echoed the whole row back failed GraphQL validation on every run.
        var (executor, userContext, provider) = BuildExecutor(role: "user", tenantId: 1);
        await using var _ = provider;

        var result = await RunTicketTransitionAsync(executor, userContext, 1, "open", "closed");

        result.Error.Should().BeNull();
        result.Succeeded.Should().BeTrue();
        (await TicketStatusAsync(1)).Should().Be("closed");
    }

    [Fact]
    public async Task Transition_WhenTheRowMovesBetweenReadAndWrite_IsAConflict()
    {
        // Another writer moves ticket 2 to 'review' after the step read 'open' and before it
        // writes. The write must not land: 'review' -> 'closed' was never validated.
        var (executor, userContext, provider) = BuildExecutor(role: "user", tenantId: 1);
        await using var _ = provider;
        var racing = new RaceAfterReadExecutor(executor, _connectionString,
            "UPDATE tickets SET status = 'review' WHERE ticket_id = 2");

        var result = await RunTicketTransitionAsync(racing, userContext, 2, "open", "closed");

        result.Succeeded.Should().BeFalse();
        result.Error!.Code.Should().Be("conflict");
        (await TicketStatusAsync(2)).Should().Be("review", "the racing write wins; the stale transition is refused");
    }

    [Fact]
    public async Task Transition_FromAStateTheRowIsNotIn_IsNotFound()
    {
        // Control: the read-side check still refuses a row that is not in 'from'.
        var (executor, userContext, provider) = BuildExecutor(role: "user", tenantId: 1);
        await using var _ = provider;

        var result = await RunTicketTransitionAsync(executor, userContext, 3, "open", "closed");

        result.Error!.Code.Should().Be("not_found");
        (await TicketStatusAsync(3)).Should().Be("closed");
    }

    /// <summary>
    /// Delegates to the real executor and runs <c>racingSql</c> right after the first read,
    /// standing in for a concurrent writer between a transition's read and its write.
    /// </summary>
    private sealed class RaceAfterReadExecutor(IWorkflowDataExecutor inner, string connectionString, string racingSql)
        : IWorkflowDataExecutor
    {
        private bool _raced;

        public async Task<IDictionary<string, object?>?> QuerySingleAsync(
            string table, object id, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default)
        {
            var row = await inner.QuerySingleAsync(table, id, userContext, cancellationToken);
            if (!_raced)
            {
                _raced = true;
                await using var conn = new SqliteConnection(connectionString);
                await conn.OpenAsync(cancellationToken);
                await using var cmd = new SqliteCommand(racingSql, conn);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            return row;
        }

        public Task InsertAsync(string table, object values, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default)
            => inner.InsertAsync(table, values, userContext, cancellationToken);

        public Task UpdateAsync(string table, object values, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default)
            => inner.UpdateAsync(table, values, userContext, cancellationToken);

        public Task TransitionAsync(WorkflowTransition transition, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default)
            => inner.TransitionAsync(transition, userContext, cancellationToken);
    }
}
