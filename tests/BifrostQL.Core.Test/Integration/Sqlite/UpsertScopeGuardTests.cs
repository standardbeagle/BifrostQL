using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
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
/// M5: an upsert whose primary key lives outside the caller's row scope (another
/// tenant, or soft-deleted) dispatches to the UPDATE branch, where the transformer
/// AdditionalFilter narrows it to zero rows. The pipeline still returned
/// <c>.Value</c> — the KEY on a single-key table (invariant 8(b)) — so the caller
/// saw "their" key come back for a row they never touched, and could tell a taken
/// cross-tenant key (key returned / batch total 0) from a free one (insert,
/// batch total 1).
///
/// The update branch now checks <c>AffectedRows</c>: a zero-row upsert update
/// answers exactly what a plain <c>update</c> answers for a missing row (0), never
/// the key. The batch pipeline already counted only affected rows; (c) pins that
/// against the two scoped-away kinds so it cannot drift.
///
/// Revert-proof (per .claude/rules/regression-test-non-vacuous.md): restoring the
/// pre-fix UpsertObject/UpdateObject makes (a) answer 20 and (b) answer 30 — the
/// victim rows' keys — instead of the not-found 0.
/// </summary>
public sealed class UpsertScopeGuardTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_upsert_scope_guard_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS docs");
        await Exec(
            """
            CREATE TABLE docs (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                label TEXT NOT NULL,
                deleted_at TEXT NULL
            )
            """);
        await Exec(
            """
            INSERT INTO docs(id, tenant_id, label, deleted_at) VALUES
                (10, 1, 'own-live', NULL),
                (20, 2, 'other-tenant', NULL),
                (30, 1, 'own-soft-deleted', '2000-01-01 00:00:00')
            """);

        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(new[]
        {
            "*.docs { tenant-filter: tenant_id; soft-delete: deleted_at }",
        }));
        _model = await loader.LoadAsync();
    }

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

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task Upsert_CrossTenantPrimaryKey_AnswersNotFound_NeverTheKey()
    {
        // (a) Tenant 1 upserts tenant 2's key. The update branch's tenant scope
        // matches zero rows, so the answer must be the update path's not-found
        // response — identical to a plain update of a key that does not exist —
        // and never the victim's key.
        var upsert = await ExecuteMutationAsync(
            "mutation { docs(upsert: { id: 20, tenant_id: 1, label: \"hijack\" }) }",
            TenantContext(1));
        var updateOfMissing = await ExecuteMutationAsync(
            "mutation { docs(update: { id: 999, tenant_id: 1, label: \"hijack\" }) }",
            TenantContext(1));

        upsert.Errors.Should().BeNullOrEmpty();
        updateOfMissing.Errors.Should().BeNullOrEmpty();
        var upsertBody = Serialize(upsert);
        upsertBody.Should().Be(Serialize(updateOfMissing),
            "a scoped-away upsert is indistinguishable from updating a row that does not exist");
        upsertBody.Should().Contain("\"docs\":0", "a zero-row upsert update is a not-found, never the key");
        upsertBody.Should().NotContain("\"docs\":20");
        (await ScalarAsync("SELECT label FROM docs WHERE id = 20")).Should().Be("other-tenant");
        (await ScalarAsync("SELECT tenant_id FROM docs WHERE id = 20")).Should().Be("2");
    }

    [Fact]
    public async Task Upsert_OwnSoftDeletedRow_AnswersNotFound_AndStaysDeleted()
    {
        // (b) The soft-delete IS NULL guard scopes the update branch away from a
        // deleted row exactly like the tenant scope did above.
        var result = await ExecuteMutationAsync(
            "mutation { docs(upsert: { id: 30, tenant_id: 1, label: \"resurrected\" }) }",
            TenantContext(1));

        result.Errors.Should().BeNullOrEmpty();
        (Serialize(result)).Should().Contain("\"docs\":0");
        (await ScalarAsync("SELECT label FROM docs WHERE id = 30")).Should().Be("own-soft-deleted");
        (await ScalarAsync("SELECT deleted_at FROM docs WHERE id = 30")).Should().NotBeNullOrEmpty(
            "an upsert keyed by a soft-deleted row must not resurrect it");
    }

    [Fact]
    public async Task BatchUpsert_ScopedAwayKeys_CountOnlyAffectedRows()
    {
        // (c) Batch total counts only rows actually affected: both scoped-away
        // kinds (cross-tenant, soft-deleted) contribute 0, so TotalAffected is 0
        // and both victim rows are untouched. The batch pipeline already behaved
        // this way (BatchUpsertGuardTests); this pins the per-row path against
        // drift now that the single-row resolver shares the not-found contract.
        var result = await ExecuteMutationAsync(
            "mutation { docs_batch(actions: [" +
            "{ upsert: { id: 20, tenant_id: 1, label: \"hijack\" } }, " +
            "{ upsert: { id: 30, tenant_id: 1, label: \"resurrected\" } }]) }",
            TenantContext(1));

        result.Errors.Should().BeNullOrEmpty();
        (Serialize(result)).Should().Contain("\"docs_batch\":0");
        (await ScalarAsync("SELECT label FROM docs WHERE id = 20")).Should().Be("other-tenant");
        (await ScalarAsync("SELECT label FROM docs WHERE id = 30")).Should().Be("own-soft-deleted");
        (await ScalarAsync("SELECT deleted_at FROM docs WHERE id = 30")).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Upsert_OwnLiveRow_UpdatesAndReturnsTheKey()
    {
        // (d) Positive control: an in-scope upsert still updates and returns the
        // key, so the guard narrows the answer without breaking real upserts.
        var result = await ExecuteMutationAsync(
            "mutation { docs(upsert: { id: 10, tenant_id: 1, label: \"renamed\" }) }",
            TenantContext(1));

        result.Errors.Should().BeNullOrEmpty();
        (Serialize(result)).Should().Contain("\"docs\":10");
        (await ScalarAsync("SELECT label FROM docs WHERE id = 10")).Should().Be("renamed");
    }

    [Fact]
    public async Task Upsert_NewPrimaryKey_InsertsAndReturnsTheKey()
    {
        // Insert-branch control: a key that exists nowhere inserts and returns the
        // new key. The not-found answer above cannot be "always 0".
        var result = await ExecuteMutationAsync(
            "mutation { docs(upsert: { id: 999, tenant_id: 1, label: \"fresh\" }) }",
            TenantContext(1));

        result.Errors.Should().BeNullOrEmpty();
        (Serialize(result)).Should().Contain("\"docs\":999");
        (await ScalarAsync("SELECT label FROM docs WHERE id = 999")).Should().Be("fresh");
    }

    private static IDictionary<string, object?> TenantContext(int tenantId) =>
        new Dictionary<string, object?> { ["tenant_id"] = tenantId };

    private static readonly GraphQL.SystemTextJson.GraphQLSerializer Serializer = new();

    private static string Serialize(ExecutionResult result) =>
        Serializer.Serialize(result);

    private async Task<ExecutionResult> ExecuteMutationAsync(
        string mutation, IDictionary<string, object?> userContext)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new SoftDeleteMutationTransformer(),
                new TenantMutationTransformer(),
            },
        });
        await using var provider = services.BuildServiceProvider();

        var executor = new DocumentExecuter();
        return await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>(userContext);
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, BifrostQL.Core.Modules.NullQueryTransformerService.Instance),
            });
        });
    }
}
