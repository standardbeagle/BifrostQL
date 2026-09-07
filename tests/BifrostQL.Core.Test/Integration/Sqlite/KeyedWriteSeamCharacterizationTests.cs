using System.Text.RegularExpressions;
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
/// CHARACTERIZATION — the current emitted SQL of every keyed-write seam reachable
/// through <see cref="IMutationIntentExecutor"/> and the filtered set-update.
/// These facts assert nothing about what the SQL OUGHT to be; they pin what it IS
/// at this commit, so the seam-convergence slices that follow (CHAR-3, CHAR-4,
/// CHAR-5) cannot change a predicate, a SET list, an <c>AdditionalFilter</c>
/// composition or a parameter namespace without a fact going red. No production
/// file changes with them: a fact that will not go green here is a divergence to
/// RECORD (named <c>Current_*</c>), never a bug to fix.
///
/// <para><b>Why the fixture is shaped this way</b>
/// (<c>.claude/rules/regression-test-non-vacuous.md</c>). A keyed-write fact can
/// only observe WHICH column a seam read if the columns are distinguishable, so
/// the fixture spans six dimensions at once:
/// <list type="bullet">
/// <item><b>Composite primary key</b> — <c>ledger(id, region)</c>. A single-column
/// key cannot show whether the WHERE carries the whole key.</item>
/// <item><b>Key value <c>0</c></b> — row <c>(0,'west')</c>. A falsy key value
/// distinguishes "supplied" from "truthy".</item>
/// <item><b>Pre-existing content at every target</b> — every row already carries a
/// non-null <c>note</c>, <c>status</c> and <c>updated_at</c>, so an overwrite is
/// observable rather than indistinguishable from an insert.</item>
/// <item><b>A second tenant</b> — row <c>(9,'west')</c> belongs to tenant 2, so
/// scoped-away is reachable and the tenant <c>AdditionalFilter</c> has something to
/// exclude.</item>
/// <item><b>An audit stamp the CHAIN adds</b> — <c>updated_at { populate:
/// updated-on }</c>. <see cref="AuditMutationTransformer"/> stamps it on Update
/// AND on Delete, so a seam that built its predicate from POST-chain data would
/// silently AND a never-matching <c>updated_at = &lt;now&gt;</c> term into the
/// WHERE (protocol-adapter-security invariant 8(c)). Without a stamping
/// transformer that fact is green either way.</item>
/// <item><b>DB names that sanitize to DIFFERENT GraphQL names</b> —
/// <c>sale-price</c> → <c>sale_price</c>. A fixture whose two name spaces coincide
/// cannot tell which one reached the SQL. The column <c>p0</c> adds the other
/// name-space hazard: it collides with the RESERVED generated shape
/// <see cref="BifrostQL.Core.QueryModel.SqlParameterNames.Generated"/> the
/// transformer-injected predicate uses.</item>
/// </list></para>
///
/// <para>Every fact asserts the captured <c>CommandText</c> decomposed into ordered
/// COLUMN LISTS rather than substrings of it: exact list equality is word-boundary
/// safe by construction, where <c>Contains("id")</c> would also match
/// <c>tenant_id</c> and <c>Contains("LIMIT 100")</c> would match
/// <c>LIMIT 10000</c>. The weight-bearing facts additionally assert the NEGATIVE
/// half — the column that must be ABSENT is named explicitly — so perturbing the
/// expectation makes them fail.</para>
/// </summary>
public sealed class KeyedWriteSeamCharacterizationTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_keyed_write_characterization;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;

    /// <summary>
    /// <c>ledger</c> carries the tenant scope, the audit stamp and the composite key;
    /// <c>note</c> adds the soft-delete rewrite; <c>vault</c> adds the
    /// concurrency token (which raises the ConflictOnNoRows flag and is refused
    /// outright by the filtered set-update).
    /// </summary>
    private static readonly string[] Rules =
    {
        "*.ledger { tenant-filter: tenant_id; filtered-update: enabled }",
        "*.ledger.updated_at { populate: updated-on }",
        "*.note { tenant-filter: tenant_id; soft-delete: deleted_at }",
        "*.note.updated_at { populate: updated-on }",
        "*.vault { tenant-filter: tenant_id; concurrency-token: version; filtered-update: enabled }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS ledger");
        await Exec(
            """
            CREATE TABLE ledger (
                id INTEGER NOT NULL,
                region TEXT NOT NULL,
                note TEXT NOT NULL,
                status TEXT NOT NULL,
                "sale-price" REAL NOT NULL,
                p0 TEXT NULL,
                tenant_id INTEGER NOT NULL,
                updated_at TEXT NULL,
                PRIMARY KEY (id, region)
            )
            """);
        // Key value 0 is present; every row already carries content at the target
        // columns; id 9 belongs to the OTHER tenant so scoped-away is observable.
        await Exec(
            """
            INSERT INTO ledger(id, region, note, status, "sale-price", p0, tenant_id, updated_at) VALUES
                (0,'west','zero-west','open',1.5,'zero',1,'2020-01-01'),
                (1,'west','one-west','open',2.5,'one',1,'2020-01-01'),
                (2,'east','two-east','archived',3.5,'two',1,'2020-01-01'),
                (9,'west','other-tenant','open',4.5,'nine',2,'2020-01-01')
            """);

        await Exec("DROP TABLE IF EXISTS note");
        await Exec(
            """
            CREATE TABLE note (
                id INTEGER PRIMARY KEY,
                body TEXT NOT NULL,
                status TEXT NOT NULL,
                deleted_at TEXT NULL,
                tenant_id INTEGER NOT NULL,
                updated_at TEXT NULL
            )
            """);
        await Exec(
            """
            INSERT INTO note(id, body, status, deleted_at, tenant_id, updated_at) VALUES
                (0,'zero','archived',NULL,1,'2020-01-01'),
                (1,'one','archived',NULL,1,'2020-01-01'),
                (9,'other','archived',NULL,2,'2020-01-01')
            """);

        await Exec("DROP TABLE IF EXISTS vault");
        await Exec(
            """
            CREATE TABLE vault (
                id INTEGER PRIMARY KEY,
                body TEXT NOT NULL,
                version INTEGER NOT NULL,
                tenant_id INTEGER NOT NULL
            )
            """);
        await Exec("INSERT INTO vault(id, body, version, tenant_id) VALUES (1,'mine',5,1),(9,'theirs',7,2)");
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    // ---- per-row seam: TableMutationPipeline ----------------------------

    /// <summary>
    /// Single-row UPDATE. WHERE is the primary key ALONE (both composite columns,
    /// with the falsy key value 0 supplied), the transformer's row scope is ANDed on
    /// as a parenthesised suffix, and everything else — including the audit stamp the
    /// chain added and the tenant column it pinned — lands in SET.
    /// </summary>
    [Fact]
    public async Task PerRow_Update_Where_IsPkOnly_Set_IsRemainder_AuditStampInSet_TenantSuffixAnded()
    {
        var captured = new List<CapturedSql>();
        var result = await BuildExecutor(captured).ExecuteAsync(new MutationIntent
        {
            Table = "ledger",
            Action = MutationIntentAction.Update,
            // sale_price is the GRAPHQL name of DB column "sale-price"; p0 collides
            // with the reserved generated parameter shape.
            Data = new Dictionary<string, object?>
            {
                ["id"] = 0, ["region"] = "west", ["note"] = "updated", ["sale_price"] = 9.5, ["p0"] = "collide",
            },
            UserContext = Tenant(1),
            Endpoint = EndpointPath,
        });

        result.AffectedRows.Should().Be(1, "the key value 0 addresses exactly its own row");
        var write = ParseSingleWrite(captured);

        write.KeyColumns.Should().Equal(new[] { "id", "region" },
            "the single-row update predicate is the WHOLE primary key and nothing else");
        write.SetColumns.Should().Equal("note", "sale-price", "p0", "tenant_id", "updated_at");
        // The two halves that carry weight, stated as explicit negatives.
        write.SetColumns.Should().Contain("updated_at", "the audit stamp belongs in SET");
        write.KeyColumns.Should().NotContain("updated_at",
            "a chain-stamped value in the WHERE would match no row (invariant 8(c))");
        write.KeyColumns.Should().NotContain("tenant_id",
            "tenant scope arrives as the ANDed AdditionalFilter suffix, never as a key column");
        write.AdditionalFilterSuffix.Should().Be(" AND (\"tenant_id\" = @p0)");
        // The DB name reaches the SQL; the GraphQL name never does.
        write.Sql.Should().Contain("\"sale-price\"=@sale_price_5b43732e");
        write.Sql.Should().NotContain("\"sale_price\"");
    }

    /// <summary>
    /// Single-row hard DELETE — the invariant 8(c) fact. The WHERE is the columns the
    /// CLIENT supplied (<c>id</c>, <c>region</c>, <c>status</c>) unioned with the
    /// primary key, taken from a PRE-chain snapshot. <c>updated_at</c> IS stamped by
    /// <see cref="AuditMutationTransformer"/> on Delete
    /// (<c>KeyedWriteCharacterizationTests.AuditChain_StampsUpdatedAt_OnDelete_*</c>
    /// is the control that proves this fixture can manifest the defect), so a
    /// predicate built from POST-chain data would carry a never-matching
    /// <c>updated_at = &lt;now&gt;</c> term and silently delete nothing.
    /// </summary>
    [Fact]
    public async Task PerRow_HardDelete_Where_IsClientColumnsUnionPk_AuditStampNotInWhere()
    {
        var captured = new List<CapturedSql>();
        var result = await BuildExecutor(captured).ExecuteAsync(new MutationIntent
        {
            Table = "ledger",
            Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?> { ["id"] = 1, ["region"] = "west", ["status"] = "open" },
            UserContext = Tenant(1),
            Endpoint = EndpointPath,
        });

        result.AffectedRows.Should().Be(1, "the delete must actually reach its row, not silently match zero");
        var write = ParseSingleWrite(captured);

        write.Sql.Should().StartWith("DELETE FROM ");
        write.SetColumns.Should().BeEmpty("a hard delete has no SET list");
        write.KeyColumns.Should().Equal("id", "region", "status");
        // NEGATIVE half — the whole point of the fact.
        write.KeyColumns.Should().NotContain("updated_at",
            "the audit stamp the chain adds must never contaminate the delete predicate");
        write.ParameterNames.Should().NotContain("@updated_at",
            "and it must not even be bound onto the delete command");
        write.AdditionalFilterSuffix.Should().Be(" AND (\"tenant_id\" = @p0)");
    }

    /// <summary>
    /// Single-row SOFT delete: the chain rewrites Delete → Update. The split is the
    /// mirror of the hard delete — the client's predicate columns
    /// (<c>id</c>, <c>status</c>) scope the WHERE, and SET carries ONLY the columns a
    /// transformer stamped. A client predicate leaking into SET would WRITE
    /// <c>status</c> into every matched row instead of matching on it.
    /// </summary>
    [Fact]
    public async Task PerRow_SoftDelete_Where_IsClientColumnsUnionPk_Set_IsStampsOnly_ClientPredicateNotInSet()
    {
        var captured = new List<CapturedSql>();
        await BuildExecutor(captured).ExecuteAsync(new MutationIntent
        {
            Table = "note",
            Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?> { ["id"] = 1, ["status"] = "archived" },
            UserContext = Tenant(1),
            Endpoint = EndpointPath,
        });

        var write = ParseSingleWrite(captured);

        write.Sql.Should().StartWith("UPDATE ", "a soft delete is emitted as an UPDATE");
        write.KeyColumns.Should().Equal("id", "status");
        write.SetColumns.Should().Equal("updated_at", "deleted_at");
        // NEGATIVE halves, both directions of the split.
        write.SetColumns.Should().NotContain("status",
            "a client predicate column written into SET would stamp it onto the row instead of matching it");
        write.KeyColumns.Should().NotContain("deleted_at").And.NotContain("updated_at",
            "the stamped columns scope nothing; they are what the statement writes");
        write.AdditionalFilterSuffix.Should().Be(" AND ((\"tenant_id\" = @p0) AND (\"deleted_at\" IS NULL))",
            "the tenant scope and the soft-delete guard compose as one ANDed suffix");

        (await ScalarAsync("SELECT COUNT(*) FROM note WHERE deleted_at IS NOT NULL")).Should().Be("1");
        (await ScalarAsync("SELECT status FROM note WHERE id = 1")).Should().Be("archived");
    }

    /// <summary>
    /// A tenant-scoped-away single-row update is a SILENT no-op: the statement runs,
    /// the AdditionalFilter excludes the row, and the seam reports zero affected rows
    /// without throwing. The asymmetry with the concurrency-token case below is the
    /// behaviour CHAR-3..5 must preserve.
    /// </summary>
    [Fact]
    public async Task PerRow_Update_ScopedAway_ReturnsAffectedRowsZero_NoThrow()
    {
        var captured = new List<CapturedSql>();
        var result = await BuildExecutor(captured).ExecuteAsync(new MutationIntent
        {
            Table = "ledger",
            Action = MutationIntentAction.Update,
            // Tenant 1 addressing tenant 2's row.
            Data = new Dictionary<string, object?> { ["id"] = 9, ["region"] = "west", ["note"] = "hijacked" },
            UserContext = Tenant(1),
            Endpoint = EndpointPath,
        });

        result.AffectedRows.Should().Be(0);
        result.Value.Should().Be(0);
        var write = ParseSingleWrite(captured);
        write.KeyColumns.Should().Equal("id", "region");
        write.AdditionalFilterSuffix.Should().Be(" AND (\"tenant_id\" = @p0)");
        (await ScalarAsync("SELECT note FROM ledger WHERE id = 9")).Should().Be("other-tenant");
    }

    /// <summary>
    /// The same zero-row outcome under a concurrency token is a CONFLICT, not a
    /// silent no-op: the token contributes a second ANDed clause to the SAME suffix,
    /// and <c>ConflictOnNoRows</c> turns zero rows into a throw that rolls back.
    /// </summary>
    [Fact]
    public async Task PerRow_Update_ScopedAway_WithConcurrencyToken_ThrowsConflict()
    {
        var captured = new List<CapturedSql>();
        var thrown = await Record.ExceptionAsync(() => BuildExecutor(captured).ExecuteAsync(new MutationIntent
        {
            Table = "vault",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["id"] = 9, ["body"] = "hijacked", ["version"] = 7 },
            UserContext = Tenant(1),
            Endpoint = EndpointPath,
        }));

        thrown.Should().BeOfType<BifrostExecutionError>()
            .Which.ErrorCode.Should().Be("CONFLICT");
        var write = ParseSingleWrite(captured);
        write.KeyColumns.Should().Equal("id");
        write.SetColumns.Should().Equal("body", "version", "tenant_id");
        write.AdditionalFilterSuffix.Should().Be(" AND ((\"tenant_id\" = @p0) AND (\"version\" = @p1))");
        write.KeyColumns.Should().NotContain("version",
            "the token narrows through the AdditionalFilter, it is not part of the key split");
        (await ScalarAsync("SELECT body FROM vault WHERE id = 9")).Should().Be("theirs");
    }

    // ---- batch seam: BatchMutationPipeline ------------------------------
    //
    // SQLite exposes no bulk executor, so a batch below the bulk threshold runs the
    // per-row statements of BatchMutationPipeline. Each fact drives the SAME payload
    // through both seams inside one test and compares the emitted text, so parity is
    // asserted rather than inferred from two separately-written expectations
    // (regression-test-non-vacuous.md — a contract shared by N seams needs one fact
    // per seam, or a direct comparison of them).

    /// <summary>Batch UPDATE emits the byte-identical statement the single-row update does.</summary>
    [Fact]
    public async Task Batch_Update_Where_IsPkOnly_MatchesPerRow()
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = 0, ["region"] = "west", ["note"] = "updated", ["sale_price"] = 9.5,
        };

        var perRowLog = new List<CapturedSql>();
        await BuildExecutor(perRowLog).ExecuteAsync(new MutationIntent
        {
            Table = "ledger", Action = MutationIntentAction.Update,
            Data = payload, UserContext = Tenant(1), Endpoint = EndpointPath,
        });

        var batchLog = new List<CapturedSql>();
        var batch = await BuildExecutor(batchLog).ExecuteBatchAsync(new MutationBatchIntent
        {
            Table = "ledger",
            Actions = new[] { new MutationBatchAction(MutationIntentAction.Update, payload) },
            UserContext = Tenant(1), Endpoint = EndpointPath,
        });

        batch.TotalAffected.Should().Be(1);
        var perRow = ParseSingleWrite(perRowLog);
        var batched = ParseSingleWrite(batchLog);

        batched.Sql.Should().Be(perRow.Sql, "the batch seam re-derives the same key split and suffix");
        batched.KeyColumns.Should().Equal("id", "region");
        batched.SetColumns.Should().Equal("note", "sale-price", "tenant_id", "updated_at");
        batched.KeyColumns.Should().NotContain("updated_at");
        // Bound parameter NAMES agree; their ORDER does not, and need not — ADO binds
        // by name. Asserted unordered so a later refactor is not held to an
        // insignificant detail.
        batched.ParameterNames.Should().BeEquivalentTo(perRow.ParameterNames);
    }

    /// <summary>Batch hard DELETE emits the byte-identical predicate the single-row delete does.</summary>
    [Fact]
    public async Task Batch_HardDelete_MatchesPerRowPredicate()
    {
        var perRowLog = new List<CapturedSql>();
        await BuildExecutor(perRowLog).ExecuteAsync(new MutationIntent
        {
            Table = "ledger", Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?> { ["id"] = 1, ["region"] = "west", ["status"] = "open" },
            UserContext = Tenant(1), Endpoint = EndpointPath,
        });

        var batchLog = new List<CapturedSql>();
        await BuildExecutor(batchLog).ExecuteBatchAsync(new MutationBatchIntent
        {
            Table = "ledger",
            Actions = new[]
            {
                new MutationBatchAction(MutationIntentAction.Delete,
                    new Dictionary<string, object?> { ["id"] = 2, ["region"] = "east", ["status"] = "archived" }),
            },
            UserContext = Tenant(1), Endpoint = EndpointPath,
        });

        var perRow = ParseSingleWrite(perRowLog);
        var batched = ParseSingleWrite(batchLog);

        batched.Sql.Should().Be(perRow.Sql,
            "both delete seams route through TableMutationPipeline.SelectPredicateColumns");
        batched.KeyColumns.Should().Equal("id", "region", "status");
        batched.KeyColumns.Should().NotContain("updated_at",
            "the batch seam takes its own PRE-chain client-column snapshot for the same reason");
        batched.ParameterNames.Should().NotContain("@updated_at");
        batched.AdditionalFilterSuffix.Should().Be(" AND (\"tenant_id\" = @p0)");
    }

    /// <summary>Batch SOFT delete reproduces the single-row predicate/SET split exactly.</summary>
    [Fact]
    public async Task Batch_SoftDelete_MatchesPerRowSplit()
    {
        var perRowLog = new List<CapturedSql>();
        await BuildExecutor(perRowLog).ExecuteAsync(new MutationIntent
        {
            Table = "note", Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?> { ["id"] = 1, ["status"] = "archived" },
            UserContext = Tenant(1), Endpoint = EndpointPath,
        });

        var batchLog = new List<CapturedSql>();
        await BuildExecutor(batchLog).ExecuteBatchAsync(new MutationBatchIntent
        {
            Table = "note",
            Actions = new[]
            {
                new MutationBatchAction(MutationIntentAction.Delete,
                    new Dictionary<string, object?> { ["id"] = 0, ["status"] = "archived" }),
            },
            UserContext = Tenant(1), Endpoint = EndpointPath,
        });

        var perRow = ParseSingleWrite(perRowLog);
        var batched = ParseSingleWrite(batchLog);

        batched.Sql.Should().Be(perRow.Sql);
        batched.KeyColumns.Should().Equal("id", "status");
        batched.SetColumns.Should().Equal("updated_at", "deleted_at");
        batched.SetColumns.Should().NotContain("status");
        batched.AdditionalFilterSuffix.Should().Be(" AND ((\"tenant_id\" = @p0) AND (\"deleted_at\" IS NULL))");
    }

    /// <summary>
    /// The upsert's insert-or-update decision runs a <c>SELECT 1</c> existence probe
    /// keyed by the WHOLE primary key. The probe carries NO AdditionalFilter suffix —
    /// it is unscoped — and that is deliberate: the write it dispatches to is scoped,
    /// so a cross-tenant key falls to the tenant-filtered UPDATE and affects nothing
    /// (BatchUpsertGuardTests pins that outcome). Recorded here because a seam
    /// convergence that added or removed the suffix would change the insert/update
    /// decision for a cross-tenant key.
    /// </summary>
    [Fact]
    public async Task Batch_UpsertProbe_SelectOne_WhereIsPk()
    {
        var captured = new List<CapturedSql>();
        var result = await ExecuteGraphQlAsync(captured,
            "mutation { ledger_batch(actions: [{ upsert: { id: 0, region: \"west\", note: \"upserted\", status: \"open\", sale_price: 1.5 } }]) }",
            Tenant(1));
        result.Errors.Should().BeNullOrEmpty();

        var probe = captured.Single(c => c.Sql.StartsWith("SELECT 1 ", StringComparison.Ordinal));
        var parsed = Parse(probe);
        parsed.KeyColumns.Should().Equal(new[] { "id", "region" },
            "the existence probe is keyed by the whole primary key");
        parsed.AdditionalFilterSuffix.Should().BeEmpty(
            "the probe is unscoped today — the scoping lives on the write it dispatches to");
        parsed.ParameterNames.Should().Equal("@id", "@region");

        // And the write it dispatched to IS the standard scoped update.
        var update = Parse(captured.Single(c => c.Sql.StartsWith("UPDATE ", StringComparison.Ordinal)));
        update.KeyColumns.Should().Equal("id", "region");
        update.AdditionalFilterSuffix.Should().Be(" AND (\"tenant_id\" = @p0)");
    }

    /// <summary>
    /// A batch whose rows are all scoped away is TOLERANT — zero affected, no throw —
    /// unless a transformer raises <c>ConflictOnNoRows</c>, which turns the same zero
    /// into a CONFLICT that rolls the whole batch back. Both arms in one fact because
    /// a fixture exercising only one cannot show the flag is what decides.
    /// </summary>
    [Fact]
    public async Task Batch_ScopedAway_ZeroRows_TolerantUnlessConflictFlag()
    {
        var tolerantLog = new List<CapturedSql>();
        var tolerant = await BuildExecutor(tolerantLog).ExecuteBatchAsync(new MutationBatchIntent
        {
            Table = "ledger",
            Actions = new[]
            {
                new MutationBatchAction(MutationIntentAction.Update,
                    new Dictionary<string, object?> { ["id"] = 9, ["region"] = "west", ["note"] = "hijacked" }),
            },
            UserContext = Tenant(1), Endpoint = EndpointPath,
        });

        tolerant.TotalAffected.Should().Be(0, "a tenant-scoped-away batch row is a silent no-op");
        Parse(tolerantLog.Single(c => c.Sql.StartsWith("UPDATE ", StringComparison.Ordinal)))
            .AdditionalFilterSuffix.Should().Be(" AND (\"tenant_id\" = @p0)");
        (await ScalarAsync("SELECT note FROM ledger WHERE id = 9")).Should().Be("other-tenant");

        var conflictLog = new List<CapturedSql>();
        var thrown = await Record.ExceptionAsync(() => BuildExecutor(conflictLog).ExecuteBatchAsync(new MutationBatchIntent
        {
            Table = "vault",
            Actions = new[]
            {
                new MutationBatchAction(MutationIntentAction.Update,
                    new Dictionary<string, object?> { ["id"] = 9, ["body"] = "hijacked", ["version"] = 7 }),
            },
            UserContext = Tenant(1), Endpoint = EndpointPath,
        }));

        thrown.Should().BeOfType<BifrostExecutionError>()
            .Which.ErrorCode.Should().Be("CONFLICT",
                "the same zero-row outcome under a concurrency token aborts the batch");
        (await ScalarAsync("SELECT body FROM vault WHERE id = 9")).Should().Be("theirs");
    }

    // ---- filtered set-update seam: FilteredUpdatePipeline ----------------

    /// <summary>
    /// The filtered set-update has NO key split at all: its WHERE is the caller's own
    /// filter combined with the transformer chain's row scope through
    /// <see cref="BifrostQL.Core.QueryModel.TableFilter.CombineAnd"/> and rendered by
    /// <c>RenderParts</c>, so the predicate is ALIAS-QUALIFIED and every bound value
    /// uses the generated <c>@pN</c> namespace — structurally unlike the
    /// <c>"col"=@col</c> key predicate every other seam emits. SET still carries the
    /// chain's stamps.
    /// </summary>
    [Fact]
    public async Task FilteredUpdate_Where_IsCallerFilterAndTenantSuffix_ViaCombineAnd_NoKeySplit()
    {
        var captured = new List<CapturedSql>();
        var result = await ExecuteGraphQlAsync(captured,
            "mutation { ledger(updateWhere: { set: { note: \"filtered\" }, where: { status: { _eq: \"open\" } } }) }",
            Tenant(1));
        result.Errors.Should().BeNullOrEmpty();

        var update = Parse(captured.Single(c => c.Sql.StartsWith("UPDATE ", StringComparison.Ordinal)));
        update.SetColumns.Should().Equal(new[] { "note", "tenant_id", "updated_at" },
            "the chain's tenant pin and audit stamp reach SET exactly as on the keyed seams");
        update.WhereText.Should().Be("((\"ledger\".\"status\" = @p0) AND (\"ledger\".\"tenant_id\" = @p1))");
        // NEGATIVE half: there is no key predicate to be found.
        update.KeyColumns.Should().BeEmpty("the filtered update addresses rows by filter, never by key");
        update.WhereText.Should().NotContain("\"id\"=@id");
        update.AdditionalFilterSuffix.Should().BeEmpty(
            "the row scope is COMBINED into the filter, not appended as a WhereSuffix");

        // The caller's filter narrowed and never widened: the other tenant's open row
        // is untouched, and only tenant 1's open rows changed.
        (await ScalarAsync("SELECT note FROM ledger WHERE id = 9")).Should().Be("other-tenant");
        (await ScalarAsync("SELECT COUNT(*) FROM ledger WHERE note = 'filtered'")).Should().Be("2");
    }

    /// <summary>
    /// The max-affected COUNT precheck runs inside the update's own transaction and
    /// MUST use the identical WHERE, or the bound it enforces is not the bound the
    /// update obeys.
    /// </summary>
    [Fact]
    public async Task FilteredUpdate_CountSql_UsesSameWhere()
    {
        var captured = new List<CapturedSql>();
        await ExecuteGraphQlAsync(captured,
            "mutation { ledger(updateWhere: { set: { note: \"filtered\" }, where: { status: { _eq: \"open\" } } }) }",
            Tenant(1));

        var count = Parse(captured.Single(c => c.Sql.StartsWith("SELECT COUNT(*) ", StringComparison.Ordinal)));
        var update = Parse(captured.Single(c => c.Sql.StartsWith("UPDATE ", StringComparison.Ordinal)));

        count.WhereText.Should().Be(update.WhereText);
        count.ParameterNames.Should().Equal(new[] { "@p0", "@p1" },
            "the precheck binds the filter's parameters and nothing from the SET list");
    }

    /// <summary>
    /// A concurrency-token table is refused OUTRIGHT — one client version cannot guard
    /// N rows. The refusal is fail-closed by construction: no statement of any kind
    /// reaches the database.
    /// </summary>
    [Fact]
    public async Task FilteredUpdate_RefusesConcurrencyTokenTable()
    {
        var captured = new List<CapturedSql>();
        var result = await ExecuteGraphQlAsync(captured,
            "mutation { vault(updateWhere: { set: { body: \"filtered\" }, where: { id: { _eq: 1 } } }) }",
            Tenant(1));

        result.Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("is not available on a concurrency-token table");
        captured.Should().BeEmpty("the gate refuses before any SQL is built or executed");
        (await ScalarAsync("SELECT body FROM vault WHERE id = 1")).Should().Be("mine");
    }

    // ---- the two parameter namespaces ------------------------------------

    /// <summary>
    /// The command carries TWO disjoint parameter namespaces: sanitized column names
    /// for the written/matched columns, and the generated <c>@p0..</c> that
    /// <see cref="BifrostQL.Core.QueryModel.SqlParameterCollection"/> mints for the
    /// transformer-injected predicate. The fixture forces the collision: the table has
    /// a column literally named <c>p0</c>, which is a legal parameter identifier, so
    /// only the RESERVATION of the generated shape keeps the client's value from
    /// becoming the tenant predicate's value.
    /// </summary>
    [Fact]
    public async Task AdditionalFilter_ParameterNames_DoNotCollideWithColumnParameters()
    {
        var captured = new List<CapturedSql>();
        await BuildExecutor(captured).ExecuteAsync(new MutationIntent
        {
            Table = "ledger",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?>
            {
                ["id"] = 0, ["region"] = "west", ["note"] = "updated", ["sale_price"] = 9.5, ["p0"] = "collide",
            },
            UserContext = Tenant(1),
            Endpoint = EndpointPath,
        });

        var write = ParseSingleWrite(captured);

        write.ParameterNames.Should().OnlyHaveUniqueItems("two namespaces on one command must not overlap");
        write.ParameterNames.Should().Contain("@p0", "the tenant predicate owns the generated name");
        write.ParameterNames.Should().Contain("@p0_a14ed80d",
            "the column literally named p0 is pushed out of the reserved shape by a hash suffix");
        write.SetColumns.Should().Contain("p0");
        // The generated name appears ONLY in the transformer's suffix, never as a
        // column assignment — asserted at a token boundary, since "@p0" is a prefix
        // of "@p0_a14ed80d".
        Regex.Matches(write.Sql, @"@p0\b").Should().HaveCount(1);
        write.AdditionalFilterSuffix.Should().Be(" AND (\"tenant_id\" = @p0)");
        write.Sql.Should().Contain("\"p0\"=@p0_a14ed80d");
        write.Sql.Should().NotContain("\"p0\"=@p0,").And.NotContain("\"p0\"=@p0 ");

        (await ScalarAsync("SELECT p0 FROM ledger WHERE id = 0")).Should().Be("collide");
        (await ScalarAsync("SELECT tenant_id FROM ledger WHERE id = 0")).Should().Be("1",
            "and the tenant predicate must not have been fed the client's p0 value");
    }

    // ---- fixture plumbing -------------------------------------------------

    /// <summary>
    /// One emitted statement decomposed into the parts a keyed-write fact reasons
    /// about. Column LISTS rather than substrings, so every assertion is
    /// word-boundary safe by construction.
    /// </summary>
    private sealed record WriteSql(
        string Sql,
        IReadOnlyList<string> SetColumns,
        IReadOnlyList<string> KeyColumns,
        string AdditionalFilterSuffix,
        string WhereText,
        IReadOnlyList<string> ParameterNames);

    private static readonly Regex AssignmentPattern = new(@"""(?<col>[^""]+)""=@(?<param>\w+)", RegexOptions.NonBacktracking);

    private static WriteSql Parse(CapturedSql captured)
    {
        var sql = captured.Sql.TrimEnd(';');
        string setPart = "";
        int whereIndex;
        if (sql.StartsWith("UPDATE ", StringComparison.Ordinal))
        {
            var setIndex = sql.IndexOf(" SET ", StringComparison.Ordinal);
            whereIndex = sql.IndexOf(" WHERE ", setIndex, StringComparison.Ordinal);
            setPart = sql[(setIndex + " SET ".Length)..whereIndex];
        }
        else
        {
            whereIndex = sql.IndexOf(" WHERE ", StringComparison.Ordinal);
        }

        var whereText = sql[(whereIndex + " WHERE ".Length)..];
        // BuildUpdateSql/BuildDeleteSql append the transformer suffix as
        // " AND (<rendered>)" AFTER the "col"=@col key predicate; the filtered
        // set-update emits no key predicate at all, so there is nothing to split.
        var keyPart = whereText;
        var suffix = "";
        var suffixIndex = whereText.IndexOf(" AND (", StringComparison.Ordinal);
        if (suffixIndex >= 0 && whereText.StartsWith('"'))
        {
            keyPart = whereText[..suffixIndex];
            suffix = whereText[suffixIndex..];
        }
        else if (whereText.StartsWith('('))
        {
            keyPart = "";
        }

        return new WriteSql(sql, Columns(setPart), Columns(keyPart), suffix, whereText, captured.ParameterNames);
    }

    private static IReadOnlyList<string> Columns(string clause) =>
        AssignmentPattern.Matches(clause).Select(m => m.Groups["col"].Value).ToList();

    /// <summary>
    /// The one data-modifying statement the seam emitted. Asserting there is exactly
    /// one keeps a fact from silently reading the wrong statement if a seam later
    /// splits its write in two.
    /// </summary>
    private static WriteSql ParseSingleWrite(List<CapturedSql> captured)
    {
        var writes = captured
            .Where(c => c.Sql.StartsWith("UPDATE ", StringComparison.Ordinal)
                     || c.Sql.StartsWith("DELETE ", StringComparison.Ordinal))
            .ToList();
        writes.Should().ContainSingle("the seam under test emits exactly one data-modifying statement");
        return Parse(writes[0]);
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

    private static IDictionary<string, object?> Tenant(int tenantId) =>
        new Dictionary<string, object?> { ["tenant_id"] = tenantId };

    private static IMutationTransformer[] BuiltInTransformers() => new IMutationTransformer[]
    {
        new PolicyMutationTransformer(),
        new StateMachineMutationTransformer(),
        new EnumValueMutationTransformer(),
        new SoftDeleteMutationTransformer(),
        new TenantMutationTransformer(),
        new AuditMutationTransformer(),
        new ConcurrencyMutationTransformer(),
    };

    /// <summary>
    /// The protocol-adapter write seam, wired to the shared capture factory. The
    /// model is loaded from the RAW factory and the pipelines execute through the
    /// logging one, so only statements the seam under test emits are captured.
    /// </summary>
    private static MutationIntentExecutor BuildExecutor(List<CapturedSql> captured)
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var raw = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(raw, new MetadataLoader(Rules)).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["connFactory"] = new SqlLoggingConnFactory(raw, new List<string>(), captured),
            });
        });

        return new MutationIntentExecutor(
            pathCache, new MutationTransformersWrap { Transformers = BuiltInTransformers() });
    }

    /// <summary>
    /// The GraphQL front door, for the two seams the intent executor cannot reach:
    /// the batch upsert's existence probe and the filtered set-update.
    /// </summary>
    private static async Task<ExecutionResult> ExecuteGraphQlAsync(
        List<CapturedSql> captured, string mutation, IDictionary<string, object?> userContext)
    {
        var raw = new SqliteDbConnFactory(ConnString);
        var model = await new DbModelLoader(raw, new MetadataLoader(Rules)).LoadAsync();
        var schema = DbSchema.FromModel(model);
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(
            new MutationTransformersWrap { Transformers = BuiltInTransformers() });
        services.AddSingleton<IFilterTransformers>(
            new FilterTransformersWrap { Transformers = Array.Empty<IFilterTransformer>() });
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>(userContext);
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqlLoggingConnFactory(raw, new List<string>(), captured),
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema, NullQueryTransformerService.Instance),
            });
        });
    }
}
