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
/// A batch upsert must honour the mutation transformers' <c>AdditionalFilter</c>
/// (tenant row-scope, soft-delete <c>IS NULL</c>) exactly as the single-row
/// resolver does. The batch pipeline previously ran a native single-statement
/// <c>ON CONFLICT DO UPDATE</c> that rendered no WHERE beyond the primary key, so
/// a batch upsert keyed by another tenant's row took it over, and one keyed by a
/// soft-deleted row overwrote (resurrected) it. The path now probes existence and
/// dispatches to the guarded Insert/Update executors — mirroring
/// <see cref="DbTableMutateResolver"/>'s upsert.
///
/// Revert-proof (per .claude/rules/regression-test-non-vacuous.md): restoring the
/// native-upsert branch in BatchMutationPipeline.ExecuteUpsert makes both facts
/// RED — the cross-tenant name becomes "hijacked" and the soft-deleted label
/// becomes "resurrected".
/// </summary>
public sealed class BatchUpsertGuardTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_batch_upsert_guard_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS orders");
        await Exec(
            """
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                name TEXT NOT NULL
            )
            """);
        await Exec(
            """
            INSERT INTO orders(id, tenant_id, name) VALUES
                (10, 1, 'tenant-one-order'),
                (20, 2, 'tenant-two-order')
            """);

        await Exec("DROP TABLE IF EXISTS events");
        await Exec(
            """
            CREATE TABLE events (
                id INTEGER PRIMARY KEY,
                label TEXT NOT NULL,
                deleted_at TEXT NULL
            )
            """);
        await Exec(
            """
            INSERT INTO events(id, label, deleted_at) VALUES
                (1, 'live-event', NULL),
                (2, 'already-soft-deleted', '2000-01-01 00:00:00')
            """);

        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(new[]
        {
            "*.orders { tenant-filter: tenant_id }",
            "*.events { soft-delete: deleted_at }",
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
    public async Task BatchUpsert_KeyedByAnotherTenantsRow_DoesNotTakeItOver()
    {
        // Tenant 1 upserts a row whose primary key belongs to tenant 2. The tenant
        // transformer's AdditionalFilter scopes the update to tenant 1, so it matches
        // zero rows — the victim row is untouched and never reassigned.
        var result = await ExecuteMutationAsync(
            "mutation { orders_batch(actions: [{ upsert: { id: 20, tenant_id: 2, name: \"hijacked\" } }]) }",
            TenantContext(1));

        result.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT name FROM orders WHERE id = 20")).Should().Be(
            "tenant-two-order", "a cross-tenant upsert must not overwrite the victim row");
        (await ScalarAsync("SELECT tenant_id FROM orders WHERE id = 20")).Should().Be(
            "2", "a cross-tenant upsert must not reassign ownership");
    }

    [Fact]
    public async Task BatchUpsert_KeyedByOwnRow_UpdatesInScope()
    {
        // The positive control: the same operation on the caller's own row succeeds,
        // so the guard narrows scope without breaking legitimate upserts.
        var result = await ExecuteMutationAsync(
            "mutation { orders_batch(actions: [{ upsert: { id: 10, tenant_id: 1, name: \"renamed\" } }]) }",
            TenantContext(1));

        result.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT name FROM orders WHERE id = 10")).Should().Be("renamed");
    }

    [Fact]
    public async Task BatchUpsert_KeyedBySoftDeletedRow_DoesNotResurrectIt()
    {
        // Upserting onto a soft-deleted row must not overwrite it: the soft-delete
        // AdditionalFilter (deleted_at IS NULL) scopes the update away from deleted rows.
        var result = await ExecuteMutationAsync(
            "mutation { events_batch(actions: [{ upsert: { id: 2, label: \"resurrected\" } }]) }",
            new Dictionary<string, object?>());

        result.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT label FROM events WHERE id = 2")).Should().Be(
            "already-soft-deleted", "a soft-deleted row must not be overwritten through upsert");
        (await ScalarAsync("SELECT deleted_at FROM events WHERE id = 2")).Should().NotBeNullOrEmpty(
            "the soft-delete marker must survive an upsert keyed by the deleted row");
    }

    private static IDictionary<string, object?> TenantContext(int tenantId) =>
        new Dictionary<string, object?> { ["tenant_id"] = tenantId };

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
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            });
        });
    }
}
