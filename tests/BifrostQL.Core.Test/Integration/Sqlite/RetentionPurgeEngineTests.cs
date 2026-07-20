using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Retention;
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
/// End-to-end proof of the metadata-driven retention purge (scheduler slice). The
/// load-bearing security properties: a purge run CANNOT delete another tenant's expired
/// rows, and that isolation holds because the mutation PIPELINE narrows every delete to the
/// synthesized per-tenant system context — not because the engine filtered. Beyond that:
/// <c>retain</c> hard-purges already-soft-deleted rows via the pipeline's declared
/// hard-delete route and never touches a live row; <c>ttl</c> routes a plain Delete intent
/// and lets the pipeline decide soft-vs-hard; and the sweep is bounded, resumable, and
/// commits each delete independently.
/// </summary>
public sealed class RetentionPurgeEngineTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_retention_purge_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;

    // Fixed clock so cutoffs are deterministic. Expired rows are stamped in year 2000,
    // fresh rows in 2100; with a 1d/30d window and "now" in 2050 the cutoff lands cleanly
    // between them.
    private static readonly DateTime Now = new(2050, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Expired = "2000-01-01 00:00:00";
    private const string Fresh = "2100-01-01 00:00:00";

    private static readonly string[] Rules =
    {
        // ttl + soft-delete + tenant: live expired rows soft-delete on expiry.
        "*.sessions { tenant-filter: tenant_id; soft-delete: deleted_at; ttl: 1d after last_seen }",
        // retain + soft-delete + hard-role + tenant: already-soft-deleted rows hard-purge.
        "*.audit_log { tenant-filter: tenant_id; soft-delete: deleted_at; soft-delete-hard-role: purge_admin; retain: 30d }",
        // ttl only, no soft-delete, no tenant: expired rows hard-delete.
        "*.metrics { ttl: 1d after ts }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS sessions");
        await Exec(
            """
            CREATE TABLE sessions (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                last_seen DATETIME NOT NULL,
                deleted_at DATETIME NULL
            )
            """);
        await Exec(
            $"""
            INSERT INTO sessions(id, tenant_id, name, last_seen, deleted_at) VALUES
                (1, 1, 'tenant-one-expired', '{Expired}', NULL),
                (2, 2, 'tenant-two-expired', '{Expired}', NULL),
                (3, 1, 'tenant-one-fresh',   '{Fresh}',   NULL)
            """);

        await Exec("DROP TABLE IF EXISTS audit_log");
        await Exec(
            """
            CREATE TABLE audit_log (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                msg TEXT NOT NULL,
                deleted_at DATETIME NULL
            )
            """);
        await Exec(
            $"""
            INSERT INTO audit_log(id, tenant_id, msg, deleted_at) VALUES
                (1, 1, 'aged-soft-deleted',   '{Expired}'),
                (2, 1, 'recent-soft-deleted', '{Fresh}'),
                (3, 1, 'still-live',          NULL)
            """);

        await Exec("DROP TABLE IF EXISTS metrics");
        await Exec(
            """
            CREATE TABLE metrics (
                id INTEGER PRIMARY KEY,
                ts DATETIME NOT NULL
            )
            """);
        await Exec(
            $"""
            INSERT INTO metrics(id, ts) VALUES
                (1, '{Expired}'), (2, '{Expired}'), (3, '{Expired}'), (4, '{Expired}'), (5, '{Expired}')
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

    // The reader and writer SHARE one PathCache so both resolve the same cached model.
    private static (RetentionPurgeEngine Engine, IMutationIntentExecutor Writer, QueryIntentExecutor Reader, PathCache<Inputs> Cache) Build(int batchSize = RetentionPurgeEngine.DefaultBatchSize)
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

        var writer = new MutationIntentExecutor(pathCache, new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new PolicyMutationTransformer(),
                new SoftDeleteMutationTransformer(),
                new TenantMutationTransformer(),
                new AuditMutationTransformer(),
            },
        });

        var engine = new RetentionPurgeEngine(reader, writer, pathCache, clock: () => Now, batchSize: batchSize);
        return (engine, writer, reader, pathCache);
    }

    // ---- criterion 2: tenant isolation is structural (pipeline narrows) ----------

    [Fact]
    public async Task Sweep_ForTenantA_PurgesOnlyTenantAExpiredRows_LeavesTenantBUntouched()
    {
        var (engine, _, reader, _) = Build();
        var model = await reader.GetModelAsync(EndpointPath);
        var sessions = model.GetTableFromDbName("sessions");
        var config = RetentionConfig.FromTable(sessions);

        // Run the sweep under tenant 1's synthesized system context only.
        var purged = await engine.SweepTableForTenantAsync(
            model, sessions, config, "tenant_id", tenantValue: 1L, EndpointPath, Now, CancellationToken.None);

        purged.Should().Be(1, "only tenant 1's single expired live session should be expired");
        // Tenant 1's expired session was soft-deleted (row still present, deleted_at stamped).
        (await ScalarAsync("SELECT deleted_at FROM sessions WHERE id = 1")).Should().NotBeNullOrEmpty();
        // Tenant 2's expired session is UNTOUCHED — the sweep ran under tenant 1.
        (await ScalarAsync("SELECT deleted_at FROM sessions WHERE id = 2")).Should().BeNullOrEmpty("tenant 2's row must survive a tenant-1 sweep");
        // Tenant 1's fresh session is untouched (not expired).
        (await ScalarAsync("SELECT deleted_at FROM sessions WHERE id = 3")).Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task CrossTenantDelete_UnderTenantAContext_AffectsZeroRows_BecausePipelineNarrows()
    {
        // The structural proof: bypass the engine and hit the writer directly with tenant B's
        // primary key under tenant A's context. The pipeline ANDs tenant_id = A onto the WHERE,
        // so B's row matches ZERO rows — isolation holds by construction, not because the sweep's
        // scoped read happened to omit B.
        var (_, writer, _, _) = Build();

        var result = await writer.ExecuteAsync(new MutationIntent
        {
            Table = "sessions",
            Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?>(),
            PrimaryKey = new object?[] { 2 }, // tenant 2's session
            UserContext = new Dictionary<string, object?> { ["tenant_id"] = 1L }, // tenant 1's context
            Endpoint = EndpointPath,
        });

        result.Value.Should().Be(0, "a cross-tenant primary key matches zero rows once the pipeline scopes the write");
        (await ScalarAsync("SELECT deleted_at FROM sessions WHERE id = 2")).Should().BeNullOrEmpty("tenant 2's row was never touched");
    }

    // ---- criterion 3: retain hard-purges via declared route, never live; ttl lets pipeline decide ----

    [Fact]
    public async Task Retain_HardPurgesAgedSoftDeletedRows_ThroughDeclaredRoute_NeverLiveRows()
    {
        var (engine, _, reader, _) = Build();
        var model = await reader.GetModelAsync(EndpointPath);
        var auditLog = model.GetTableFromDbName("audit_log");
        var config = RetentionConfig.FromTable(auditLog);

        var purged = await engine.SweepTableForTenantAsync(
            model, auditLog, config, "tenant_id", tenantValue: 1L, EndpointPath, Now, CancellationToken.None);

        purged.Should().Be(1, "only the aged soft-deleted row is past the retain window");
        // The aged already-soft-deleted row is PHYSICALLY gone (hard delete via _hardDelete route).
        (await ScalarAsync("SELECT COUNT(*) FROM audit_log WHERE id = 1")).Should().Be("0");
        // A recently soft-deleted row is still inside the window — kept.
        (await ScalarAsync("SELECT COUNT(*) FROM audit_log WHERE id = 2")).Should().Be("1");
        // A LIVE row (deleted_at IS NULL) is NEVER selected by retain — anchored on soft-delete.
        (await ScalarAsync("SELECT COUNT(*) FROM audit_log WHERE id = 3 AND deleted_at IS NULL")).Should().Be("1");
    }

    [Fact]
    public async Task Ttl_OnSoftDeleteTable_RoutesDeleteIntent_PipelineSoftDeletes_NotHardDeletes()
    {
        var (engine, _, reader, _) = Build();
        var model = await reader.GetModelAsync(EndpointPath);
        var sessions = model.GetTableFromDbName("sessions");
        var config = RetentionConfig.FromTable(sessions);

        await engine.SweepTableForTenantAsync(
            model, sessions, config, "tenant_id", tenantValue: 1L, EndpointPath, Now, CancellationToken.None);

        // The engine routed a plain Delete intent; the pipeline decided soft-delete (row still
        // present, deleted_at stamped) — the engine never special-cased soft-delete itself.
        (await ScalarAsync("SELECT COUNT(*) FROM sessions WHERE id = 1")).Should().Be("1", "ttl on a soft-delete table soft-deletes, not hard-deletes");
        (await ScalarAsync("SELECT deleted_at FROM sessions WHERE id = 1")).Should().NotBeNullOrEmpty();
    }

    // ---- criterion 4/6: bounded batch, resumable, independent commits -------------

    [Fact]
    public async Task Ttl_BoundedBatch_ResumesAcrossPasses_LosingNoProgress()
    {
        var (engine, _, reader, _) = Build(batchSize: 2);
        var model = await reader.GetModelAsync(EndpointPath);
        var metrics = model.GetTableFromDbName("metrics");
        var config = RetentionConfig.FromTable(metrics);

        // Pass 1: at most batchSize (2) rows removed — the batch is bounded.
        var first = await engine.SweepTableForTenantAsync(
            model, metrics, config, null, null, EndpointPath, Now, CancellationToken.None);
        first.Should().Be(2, "the batch caps at 2 rows per pass");
        (await ScalarAsync("SELECT COUNT(*) FROM metrics")).Should().Be("3", "each delete committed independently — the 2 removed are gone");

        // Pass 2 resumes over the shrunken set (no re-scan from zero, no lost progress).
        var second = await engine.SweepTableForTenantAsync(
            model, metrics, config, null, null, EndpointPath, Now, CancellationToken.None);
        second.Should().Be(2);
        (await ScalarAsync("SELECT COUNT(*) FROM metrics")).Should().Be("1");

        // Pass 3 drains the remainder.
        var third = await engine.SweepTableForTenantAsync(
            model, metrics, config, null, null, EndpointPath, Now, CancellationToken.None);
        third.Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM metrics")).Should().Be("0");

        // Idempotent once drained.
        var fourth = await engine.SweepTableForTenantAsync(
            model, metrics, config, null, null, EndpointPath, Now, CancellationToken.None);
        fourth.Should().Be(0);
    }

    // ---- dry-run: same selection path, zero deletes ------------------------------

    [Fact]
    public async Task DryRun_ReportsCandidatesViaSameSelectionPath_ChangesZeroRows_AndLiveRunDeletesExactlyThatSet()
    {
        var (engine, _, reader, _) = Build();
        var model = await reader.GetModelAsync(EndpointPath);
        var connFactory = new SqliteDbConnFactory(ConnString);

        // Dry-run: enumerate what WOULD be purged, without routing a single Delete intent.
        var dry = await engine.PurgeOnceAsync(model, connFactory, EndpointPath, CancellationToken.None, dryRun: true);

        // The candidate set is exactly the same 8 rows a live pass removes (sessions t1+t2
        // expired = 2, audit_log aged = 1, metrics = 5), reported from the SAME scoped read.
        dry.Candidates.Should().HaveCount(8);
        CandidateIds(dry, "metrics", RetentionMode.Ttl).Should().BeEquivalentTo(new[] { 1L, 2L, 3L, 4L, 5L });
        CandidateIds(dry, "audit_log", RetentionMode.Retain).Should().BeEquivalentTo(new[] { 1L });
        CandidateIds(dry, "sessions", RetentionMode.Ttl).Should().BeEquivalentTo(new[] { 1L, 2L });

        // NOT ONE ROW CHANGED — a dry-run routes zero Delete intents.
        (await ScalarAsync("SELECT COUNT(*) FROM metrics")).Should().Be("5");
        (await ScalarAsync("SELECT COUNT(*) FROM audit_log")).Should().Be("3");
        (await ScalarAsync("SELECT COUNT(*) FROM sessions WHERE deleted_at IS NULL")).Should().Be("3");

        // A subsequent LIVE run deletes EXACTLY the reported set.
        var live = await engine.PurgeOnceAsync(model, connFactory, EndpointPath, CancellationToken.None);
        live.RowsPurged.Should().Be(8);
        live.Candidates.Should().BeEmpty("a live pass reports no dry-run candidates");
        (await ScalarAsync("SELECT COUNT(*) FROM metrics")).Should().Be("0", "the 5 reported metrics rows were deleted");
        (await ScalarAsync("SELECT COUNT(*) FROM audit_log WHERE id = 1")).Should().Be("0", "the reported aged row was purged");
        (await ScalarAsync("SELECT deleted_at FROM sessions WHERE id = 1")).Should().NotBeNullOrEmpty();
        (await ScalarAsync("SELECT deleted_at FROM sessions WHERE id = 2")).Should().NotBeNullOrEmpty();
        (await ScalarAsync("SELECT deleted_at FROM sessions WHERE id = 3")).Should().BeNullOrEmpty("the fresh session was never a candidate");
    }

    private static long[] CandidateIds(RetentionPurgeOutcome outcome, string table, RetentionMode mode)
        => outcome.Candidates
            .Where(c => c.Table == table && c.Mode == mode)
            .Select(c => Convert.ToInt64(c.PrimaryKey[0]))
            .OrderBy(id => id)
            .ToArray();

    // ---- full pass: multi-tenant, multi-table ------------------------------------

    [Fact]
    public async Task PurgeOnce_SweepsEveryRetentionTableAndTenant()
    {
        var (engine, _, reader, _) = Build();
        var model = await reader.GetModelAsync(EndpointPath);
        var connFactory = new SqliteDbConnFactory(ConnString);

        var outcome = await engine.PurgeOnceAsync(model, connFactory, EndpointPath, CancellationToken.None);

        outcome.TablesSwept.Should().Be(3, "sessions, audit_log, and metrics all declare retention");
        outcome.TablesSkipped.Should().Be(0);
        // sessions: tenant 1 + tenant 2 expired rows both soft-deleted (2); audit_log: 1 aged
        // purged; metrics: 5 expired hard-deleted. 2 + 1 + 5 = 8.
        outcome.RowsPurged.Should().Be(8);

        // Both tenants' expired sessions were expired by the full sweep (enumerated per tenant).
        (await ScalarAsync("SELECT deleted_at FROM sessions WHERE id = 1")).Should().NotBeNullOrEmpty();
        (await ScalarAsync("SELECT deleted_at FROM sessions WHERE id = 2")).Should().NotBeNullOrEmpty();
        (await ScalarAsync("SELECT deleted_at FROM sessions WHERE id = 3")).Should().BeNullOrEmpty("the fresh session is not expired");
        (await ScalarAsync("SELECT COUNT(*) FROM audit_log")).Should().Be("2", "only the aged soft-deleted row was purged");
        (await ScalarAsync("SELECT COUNT(*) FROM metrics")).Should().Be("0", "all expired metrics removed");
    }
}
