using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
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
        "main.Orders { policy-actions: read,update }",
        "main.audit_log { policy-actions: read }",
        "main.audit_log { tenant-filter: tenant_id }",
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
                );", conn);
            await ddl.ExecuteNonQueryAsync();

            var seed = new SqliteCommand(
                @"INSERT INTO Orders (tenant_id, total) VALUES (1, 10.0), (2, 20.0);
                  INSERT INTO audit_log (tenant_id, action) VALUES (1, 'seed.tenant1'), (2, 'seed.tenant2');", conn);
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
}
