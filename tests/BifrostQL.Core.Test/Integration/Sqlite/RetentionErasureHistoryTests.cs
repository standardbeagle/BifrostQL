using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Cdc;
using BifrostQL.Core.Modules.History;
using BifrostQL.Core.Modules.Retention;
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
/// The Retention epic's right-to-erasure compliance proof. A retention purge deletes a row
/// through the SAME mutation pipeline as any other delete, so the built-in
/// <see cref="HistoryMutationHook"/> and <see cref="OutboxMutationHook"/> fire in the purge's
/// own transaction. This pins the epic-closing decisions:
///
/// <list type="bullet">
///   <item><b>History-trail handling under erasure is TOMBSTONE, not append.</b> When the
///     purge physically deletes a tracked row, the history writer PURGES the entity's existing
///     trail (its before/after images are themselves the PII being erased) and records ONE
///     payload-free <c>op='erase'</c> tombstone — never a before-image of the row it just
///     erased. The erasure terminates (no history-of-history) and leaves no PII behind.</item>
///   <item><b>Audit/CDC compose with the EXISTING in-transaction hooks, atomically.</b> The
///     erase tombstone and the CDC event are written inside the purge's own transaction, so a
///     rolled-back purge leaves NO orphan history/outbox row — not a bespoke pre-delete write.</item>
///   <item><b>Tenant isolation of the trail is structural.</b> A tenant-1 erasure purges only
///     entity 1's trail; another tenant's trail rows survive untouched.</item>
/// </list>
/// </summary>
public sealed class RetentionErasureHistoryTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_retention_erasure_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;

    private static readonly DateTime Now = new(2050, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Expired = "2000-01-01 00:00:00";

    // people is tenant-scoped + soft-delete + retain + history + emit-events, so a retain
    // hard-purge of an aged soft-deleted row exercises the full erasure path through both hooks.
    private static readonly string[] Rules =
    {
        "*.people { tenant-filter: tenant_id; soft-delete: deleted_at; soft-delete-hard-role: purge_admin; " +
        "retain: 30d; history: enabled; history-columns: name,email; emit-events: delete; event-payload: keys }",
        ":root { history-table: main.__history; outbox-table: main.__outbox }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS people");
        await Exec(
            """
            CREATE TABLE people (
                id         INTEGER PRIMARY KEY,
                tenant_id  INTEGER NOT NULL,
                name       TEXT NULL,
                email      TEXT NULL,
                deleted_at DATETIME NULL
            )
            """);
        // Two aged soft-deleted rows in DIFFERENT tenants: person 1 (tenant 1) is what a
        // tenant-1 sweep erases; person 2 (tenant 2) proves the trail purge cannot cross.
        await Exec(
            $"""
            INSERT INTO people(id, tenant_id, name, email, deleted_at) VALUES
                (1, 1, 'alice', 'alice@x', '{Expired}'),
                (2, 2, 'bob',   'bob@x',   '{Expired}')
            """);

        await Exec("DROP TABLE IF EXISTS __history");
        // Tenant-scoped trail: a tenant-filtered tracked table materializes its scope column.
        await Exec(
            """
            CREATE TABLE __history (
                id              INTEGER PRIMARY KEY,
                entity          TEXT NOT NULL,
                entity_id       TEXT NOT NULL,
                op              TEXT NOT NULL,
                actor           TEXT NULL,
                changed_at      TEXT NOT NULL,
                before          TEXT NULL,
                after           TEXT NULL,
                changed_columns TEXT NULL,
                tenant_id       INTEGER NULL
            )
            """);
        // Pre-existing trail rows carrying PII (the before/after images an erasure must clear).
        await Exec(
            """
            INSERT INTO __history(entity, entity_id, op, actor, changed_at, before, after, changed_columns, tenant_id) VALUES
                ('main.people', '{"id":1}', 'insert', 'u1', '2000-01-01', NULL,               '{"name":"alice"}', '["name"]', 1),
                ('main.people', '{"id":1}', 'update', 'u1', '2000-02-01', '{"name":"al"}',    '{"name":"alice"}', '["name"]', 1),
                ('main.people', '{"id":2}', 'insert', 'u2', '2000-01-01', NULL,               '{"name":"bob"}',   '["name"]', 2)
            """);

        await Exec("DROP TABLE IF EXISTS __outbox");
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
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private sealed record TrailRow(string EntityId, string Op, string? Before, string? After, string ChangedColumns, long? TenantId);

    private async Task<List<TrailRow>> TrailAsync(string entityId)
    {
        var rows = new List<TrailRow>();
        await using var cmd = new SqliteCommand(
            "SELECT entity_id, op, before, after, changed_columns, tenant_id FROM __history " +
            "WHERE entity = 'main.people' AND entity_id = @e ORDER BY id", _keepAlive);
        cmd.Parameters.AddWithValue("@e", entityId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new TrailRow(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5)));
        return rows;
    }

    // A poison in-transaction hook: throws AFTER the history/outbox hooks have written, so the
    // whole purge transaction rolls back — the lever that proves the trail/event writes are
    // atomic with the delete (a rolled-back purge leaves no orphan).
    private sealed class ThrowingInTransactionHook : IInTransactionMutationHook
    {
        public ValueTask AfterWriteInTransactionAsync(MutationObserverContext context)
            => throw new BifrostExecutionError("boom: forced rollback after in-transaction hooks wrote");
    }

    private (RetentionPurgeEngine Engine, QueryIntentExecutor Reader) Build(bool poison = false)
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["dbSchema"] = DbSchema.FromModel(model),
                ["connFactory"] = factory,
            });
        });

        var reader = new QueryIntentExecutor(pathCache, new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new TenantFilterTransformer(),
                new SoftDeleteFilterTransformer(),
                new PolicyFilterTransformer(),
            },
        }));

        // The in-transaction hooks fire from the writer's service provider — exactly as the
        // host DI wires them — so the purge's delete runs the SAME hook choreography a GraphQL
        // or adapter write does. Registration order: History, Outbox, then (optionally) poison.
        var services = new ServiceCollection();
        services.AddSingleton<HistoryMutationHook>();
        services.AddSingleton<IBeforeCommitMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<IInTransactionMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<IInTransactionMutationHook, OutboxMutationHook>();
        if (poison)
            services.AddSingleton<IInTransactionMutationHook, ThrowingInTransactionHook>();
        services.AddSingleton(sp => new BeforeCommitMutationHooks(sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        services.AddSingleton(sp => new InTransactionMutationHooks(sp.GetServices<IInTransactionMutationHook>().ToArray()));
        var provider = services.BuildServiceProvider();

        var writer = new MutationIntentExecutor(pathCache, new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new PolicyMutationTransformer(),
                new SoftDeleteMutationTransformer(),
                new TenantMutationTransformer(),
                new AuditMutationTransformer(),
            },
        }, provider);

        var engine = new RetentionPurgeEngine(reader, writer, pathCache, clock: () => Now);
        return (engine, reader);
    }

    private async Task<int> SweepTenantOneAsync(RetentionPurgeEngine engine, QueryIntentExecutor reader)
    {
        var model = await reader.GetModelAsync(EndpointPath);
        var people = model.GetTableFromDbName("people");
        var config = RetentionConfig.FromTable(people);
        return await engine.SweepTableForTenantAsync(
            model, people, config, "tenant_id", tenantValue: 1L, EndpointPath, Now, CancellationToken.None);
    }

    [Fact]
    public async Task ErasurePurge_TombstonesTheTrail_PurgesExistingImages_AndTerminates()
    {
        var (engine, reader) = Build();

        var purged = await SweepTenantOneAsync(engine, reader);

        purged.Should().Be(1, "person 1 is the only aged soft-deleted row past the retain window for tenant 1");
        // The row is PHYSICALLY erased.
        (await CountAsync("SELECT COUNT(*) FROM people WHERE id = 1")).Should().Be(0);

        // Entity 1's trail is now a SINGLE payload-free tombstone. The two pre-existing PII-bearing
        // trail rows are gone (purged), and exactly one 'erase' row replaces them.
        var trail = await TrailAsync("{\"id\":1}");
        trail.Should().ContainSingle("the erasure terminates: one tombstone, no history-of-history");
        trail[0].Op.Should().Be("erase");
        trail[0].Before.Should().BeNull("the erased before-image PII must not be left behind in the trail");
        trail[0].After.Should().BeNull();
        JsonSerializer.Deserialize<string[]>(trail[0].ChangedColumns)!.Should().BeEmpty();
        trail[0].TenantId.Should().Be(1, "the tombstone materializes the purge's tenant scope");

        // No 'alice' PII survives anywhere in the trail — the whole point of the erasure.
        (await CountAsync("SELECT COUNT(*) FROM __history WHERE before LIKE '%alice%' OR after LIKE '%alice%'"))
            .Should().Be(0);

        // Tenant isolation: tenant 2's trail for entity 2 is untouched by a tenant-1 erasure.
        var otherTrail = await TrailAsync("{\"id\":2}");
        otherTrail.Should().ContainSingle();
        otherTrail[0].Op.Should().Be("insert", "another tenant's trail rows survive the erasure");
    }

    [Fact]
    public async Task ErasurePurge_EmitsCdcEvent_InThePurgeTransaction()
    {
        var (engine, reader) = Build();

        await SweepTenantOneAsync(engine, reader);

        // The existing outbox hook fired inside the purge's own transaction — the CDC event and
        // the deletion committed together (not a bespoke pre-delete write).
        (await CountAsync("SELECT COUNT(*) FROM __outbox WHERE aggregate = 'main.people' AND op = 'delete'"))
            .Should().Be(1);
    }

    [Fact]
    public async Task RolledBackErasure_LeavesNoOrphanTrailOrOutboxRow()
    {
        var (engine, reader) = Build(poison: true);

        // The poison hook throws after the history + outbox hooks wrote, rolling the whole
        // purge transaction back.
        var act = async () => await SweepTenantOneAsync(engine, reader);
        await act.Should().ThrowAsync<BifrostExecutionError>();

        // The delete rolled back: the row survives.
        (await CountAsync("SELECT COUNT(*) FROM people WHERE id = 1")).Should().Be(1);
        // No orphan erase tombstone — the history write rolled back with the delete.
        (await CountAsync("SELECT COUNT(*) FROM __history WHERE op = 'erase'")).Should().Be(0);
        // The entity's original trail is intact (the trail-purge rolled back too).
        (await TrailAsync("{\"id\":1}")).Should().HaveCount(2, "the pre-existing trail rows are restored on rollback");
        // No orphan CDC event.
        (await CountAsync("SELECT COUNT(*) FROM __outbox")).Should().Be(0);
    }
}
