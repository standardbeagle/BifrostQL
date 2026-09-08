using System.Text.RegularExpressions;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// CHARACTERIZATION — the predicate/SET split and the ZERO-ROW POLICY of
/// <see cref="TreeSyncExecutor"/>, the sixth keyed-write seam. Sibling of
/// <c>KeyedWriteSeamCharacterizationTests</c> (CHAR-1, per-row/batch/filtered) and of
/// <c>BulkBatchPlanCharacterizationTests</c> (the set-based fast path). These facts
/// assert nothing about what the SQL OUGHT to be; they pin what it IS at this commit.
/// No production file changes with them.
///
/// <para>One fact is named <c>Current_*</c>: it pins a divergence CHAR-5 will
/// re-baseline, and asserts it POSITIVELY (the scoped-away soft delete DOES return
/// silently, the transaction DOES commit, the row DOES survive un-deleted), so the fix
/// cannot land without turning it red.</para>
///
/// <para>The artefact is the captured <c>CommandText</c> (through
/// <see cref="SqlLoggingConnFactory"/>) AND the transaction outcome — the row state
/// after the run. A zero-row fact that reads only a return value cannot tell "returned
/// silently" from "threw and rolled back", which is exactly the distinction this slice
/// exists to pin.</para>
///
/// <para><b>Why the fixture is shaped this way</b>
/// (<c>.claude/rules/regression-test-non-vacuous.md</c>): every child table carries a
/// COMPOSITE primary key <c>(order_id, line_no)</c> / <c>(order_id, att_no)</c>, and a
/// key value <c>0</c> is present, so a fact can observe whether the WHOLE key reaches
/// the WHERE and presence is never decided by truthiness; <c>updated_at { populate:
/// updated-on }</c> makes <see cref="AuditMutationTransformer"/> stamp a column on
/// Update AND Delete, so a predicate built from POST-chain data would carry a
/// never-matching term (protocol-adapter-security invariant 8(c)); a SECOND TENANT owns
/// rows under the same parent, so a scoped-away write is reachable and the tenant
/// <c>AdditionalFilter</c> has something to exclude; and <c>attachments</c> is a
/// soft-delete table so the rewritten-delete branch is exercised in its own right.</para>
///
/// <para>Every delete here is INFERRED: the tree is diffed by
/// <see cref="TreeSyncEngine"/> and the omitted child becomes an orphan delete the
/// client never asked for. That provenance is what makes the zero-row policy
/// load-bearing — the row was just read, so zero rows means the statement silently did
/// nothing.</para>
/// </summary>
public sealed class TreeSyncKeyedWriteCharacterizationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _raw;

    public TreeSyncKeyedWriteCharacterizationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-treesync-char-{Guid.NewGuid():N}.db");
        _raw = new SqliteDbConnFactory($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static IDbModel BuildModel()
        => DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithPrimaryKey("order_id")
                .WithColumn("title", "nvarchar")
                .WithColumn("tenant_id", "int")
                .WithColumn("updated_at", "nvarchar", isNullable: true)
                .WithColumnMetadata("updated_at", MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.UpdatedOn)
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id"))
            .WithTable("lines", t => t
                .WithPrimaryKey("order_id").WithPrimaryKey("line_no")
                .WithColumn("note", "nvarchar")
                .WithColumn("tenant_id", "int")
                .WithColumn("updated_at", "nvarchar", isNullable: true)
                .WithColumnMetadata("updated_at", MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.UpdatedOn)
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id"))
            .WithTable("attachments", t => t
                .WithPrimaryKey("order_id").WithPrimaryKey("att_no")
                .WithColumn("label", "nvarchar")
                .WithColumn("deleted_at", "nvarchar", isNullable: true)
                .WithColumn("tenant_id", "int")
                .WithColumn("updated_at", "nvarchar", isNullable: true)
                .WithColumnMetadata("updated_at", MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.UpdatedOn)
                .WithMetadata(MetadataKeys.SoftDelete.Column, "deleted_at")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id"))
            // No metadata at all: the degenerate no-key / no-set fact must observe the
            // executor's own return, not a transformer's stamp filling the SET list.
            .WithTable("plain", t => t
                .WithPrimaryKey("id")
                .WithColumn("name", "nvarchar"))
            .WithMultiLink("orders", "order_id", "lines", "order_id", "lines")
            .WithMultiLink("orders", "order_id", "attachments", "order_id", "attachments")
            .Build();

    private async Task SeedAsync()
    {
        await Exec("CREATE TABLE orders (order_id INTEGER PRIMARY KEY, title TEXT, tenant_id INTEGER, updated_at TEXT)");
        await Exec("CREATE TABLE lines (order_id INTEGER, line_no INTEGER, note TEXT, tenant_id INTEGER, updated_at TEXT, PRIMARY KEY (order_id, line_no))");
        await Exec("CREATE TABLE attachments (order_id INTEGER, att_no INTEGER, label TEXT, deleted_at TEXT, tenant_id INTEGER, updated_at TEXT, PRIMARY KEY (order_id, att_no))");
        await Exec("CREATE TABLE plain (id INTEGER PRIMARY KEY, name TEXT)");

        await Exec("INSERT INTO orders (order_id, title, tenant_id, updated_at) VALUES (1,'Acme',1,'2020-01-01')");
        // Key value 0 is present; row (1,2) belongs to the OTHER tenant, so a
        // scoped-away write is reachable under the same parent.
        await Exec("INSERT INTO lines (order_id, line_no, note, tenant_id, updated_at) VALUES " +
                   "(1,0,'zero',1,'2020-01-01'),(1,1,'one',1,'2020-01-01'),(1,2,'other-tenant',2,'2020-01-01')");
        await Exec("INSERT INTO attachments (order_id, att_no, label, deleted_at, tenant_id, updated_at) VALUES " +
                   "(1,0,'mine',NULL,1,'2020-01-01'),(1,5,'other-tenant',NULL,2,'2020-01-01')");
        await Exec("INSERT INTO plain (id, name) VALUES (1,'unchanged')");
    }

    // ---- update -----------------------------------------------------------

    /// <summary>
    /// A reconciled child UPDATE: the WHERE is the WHOLE composite primary key and
    /// nothing else, the transformer's row scope is ANDed on as a parenthesised suffix,
    /// and everything else — the tenant column the chain pinned, the audit stamp it
    /// added — lands in SET. This is the split every keyed-write seam already agrees on
    /// (CHAR-1 pins it for per-row and batch).
    /// </summary>
    [Fact]
    public async Task TreeSync_Update_Where_IsPkOnly_Set_IsRemainder_TenantSuffixAnded()
    {
        await SeedAsync();
        var captured = new List<CapturedSql>();

        await SyncAsync(captured, new Dictionary<string, object?>
        {
            ["order_id"] = 1L,
            ["title"] = "Acme",
            ["lines"] = new List<Dictionary<string, object?>>
            {
                // The key value 0 row, edited. Both key columns supplied, so the diff
                // matches it against the loaded row rather than treating it as new.
                new() { ["order_id"] = 1L, ["line_no"] = 0L, ["note"] = "edited" },
                new() { ["order_id"] = 1L, ["line_no"] = 1L, ["note"] = "one" },
                new() { ["order_id"] = 1L, ["line_no"] = 2L, ["note"] = "other-tenant" },
            },
        });

        var update = Single(captured, "UPDATE", "lines");
        update.KeyColumns.Should().Equal("order_id", "line_no");
        update.SetColumns.Should().BeEquivalentTo(new[] { "note", "tenant_id", "updated_at" });
        update.SetColumns.Should().Contain("updated_at", "the audit stamp belongs in SET");
        update.KeyColumns.Should().NotContain("updated_at",
            "a chain-stamped value in the WHERE would match no row (invariant 8(c))");
        update.KeyColumns.Should().NotContain("tenant_id",
            "tenant scope arrives as the ANDed suffix, never as a key column");
        update.Suffix.Should().Be(" AND (\"tenant_id\" = @p0)");
        update.ParameterNames.Should().Contain("@p0").And.OnlyHaveUniqueItems();

        (await ScalarAsync("SELECT note FROM lines WHERE order_id = 1 AND line_no = 0")).Should().Be("edited");

        // The loader's own read is pinned here, present or absent: with no filter
        // transformers registered in services (the shape every TreeSync test builds)
        // the loaded subtree is UNSCOPED, which is what makes the other tenant's row
        // visible to the diff at all. Scoping arrives only through registered filter
        // transformers, so an unasserted absence would hide a change either way.
        var reads = captured.Where(c => c.Sql.StartsWith("SELECT ", StringComparison.Ordinal)).ToList();
        reads.Should().NotBeEmpty();
        reads.Should().OnlyContain(r => !r.Sql.Contains("tenant_id ="),
            "the state loader applies no tenant predicate when no filter transformers are registered");
    }

    // ---- inferred hard delete ---------------------------------------------

    /// <summary>
    /// An omitted child is an INFERRED orphan delete. Its WHERE is the columns the
    /// operation carried (the whole composite key) — taken from a PRE-chain snapshot
    /// through <c>TableMutationPipeline.SelectPredicateColumns</c> — plus the tenant
    /// suffix. <c>updated_at</c> IS stamped by the chain on a Delete, so a predicate
    /// built from POST-chain data would AND a never-matching term into the WHERE and
    /// silently remove nothing; the negative half is what makes this fact non-vacuous.
    /// That the chain really stamps <c>updated_at</c> on a Delete is proven by the paired
    /// control <c>KeyedWriteCharacterizationTests.AuditChain_StampsUpdatedAt_OnDelete_*</c>
    /// (CHAR-1) and, on the same chain, by
    /// <c>BulkBatchPlanCharacterizationTests.Current_Bulk_HardDelete_AuditStamp_LandsInKeyColumns</c>,
    /// which asserts the stamped value positively.
    /// </summary>
    [Fact]
    public async Task TreeSync_InferredHardDelete_Where_IsClientColumnsUnionPk()
    {
        await SeedAsync();
        var captured = new List<CapturedSql>();

        await SyncAsync(captured, new Dictionary<string, object?>
        {
            ["order_id"] = 1L,
            ["title"] = "Acme",
            ["lines"] = new List<Dictionary<string, object?>>
            {
                new() { ["order_id"] = 1L, ["line_no"] = 0L, ["note"] = "zero" },
                new() { ["order_id"] = 1L, ["line_no"] = 2L, ["note"] = "other-tenant" },
            },
        });

        var delete = Single(captured, "DELETE", "lines");
        delete.SetColumns.Should().BeEmpty("a hard delete has no SET list");
        delete.KeyColumns.Should().Equal("order_id", "line_no");
        delete.KeyColumns.Should().NotContain("updated_at",
            "the audit stamp the chain adds must never contaminate the delete predicate");
        delete.ParameterNames.Should().NotContain("@updated_at",
            "and it must not even be bound onto the delete command");
        delete.Suffix.Should().Be(" AND (\"tenant_id\" = @p0)");

        // Transaction outcome: exactly the orphan is gone.
        (await ScalarAsync("SELECT COUNT(*) FROM lines WHERE order_id = 1 AND line_no = 1")).Should().Be("0");
        (await ScalarAsync("SELECT COUNT(*) FROM lines")).Should().Be("2");
    }

    /// <summary>
    /// CURRENT, and CORRECT — the asymmetry CHAR-5 must preserve. A scoped-away
    /// INFERRED hard delete affects zero rows, and zero rows on a row the sync just read
    /// means the statement silently did nothing: the executor throws and the WHOLE tree
    /// rolls back, including the root update that had already succeeded.
    /// </summary>
    [Fact]
    public async Task TreeSync_InferredHardDelete_ScopedAway_Throws_AndRollsBack()
    {
        await SeedAsync();
        var captured = new List<CapturedSql>();

        // The root is renamed (so a successful write precedes the delete) and every
        // child is omitted; the other tenant's line (1,2) is among the orphans.
        var act = async () => await SyncAsync(captured, new Dictionary<string, object?>
        {
            ["order_id"] = 1L,
            ["title"] = "Acme Renamed",
            ["lines"] = new List<Dictionary<string, object?>>(),
        });

        (await act.Should().ThrowAsync<BifrostExecutionError>()).Which
            .Message.Should().Contain("affected no rows");

        var delete = captured
            .Where(c => c.Sql.StartsWith("DELETE ", StringComparison.Ordinal) && c.Sql.Contains("\"lines\""))
            .Select(Parse)
            .Should().NotBeEmpty().And.Subject.First();
        delete.Suffix.Should().Be(" AND (\"tenant_id\" = @p0)",
            "the tenant scope is what excludes the row the delete was inferred for");

        // Transaction outcome — the whole tree rolled back, root update included.
        (await ScalarAsync("SELECT title FROM orders WHERE order_id = 1")).Should().Be("Acme");
        (await ScalarAsync("SELECT COUNT(*) FROM lines")).Should().Be("3",
            "no orphan was removed: the rollback is the whole transaction, not the failing statement");
    }

    // ---- inferred soft delete: the divergence -----------------------------

    /// <summary>
    /// CURRENT, DIVERGENT — re-baselined by CHAR-5.
    ///
    /// <para>The same scoped-away INFERRED delete on a SOFT-DELETE table reaches the
    /// executor as an Update, because the chain rewrites Delete → Update. The policy
    /// does not read that rewritten verb: <c>inferredTarget</c> is taken from
    /// <c>TreeSyncOperation.Inferred</c>, stamped by the engine's orphan producer BEFORE
    /// the chain runs, so both arms reach the same
    /// <c>MutationCommandExecutor.EnsureAffectedRows</c> and both abort. The row was
    /// just read during the diff, so zero affected rows means the statement silently did
    /// nothing (protocol-adapter-security.md invariant 8(c)).</para>
    ///
    /// <para>Asserted as a throw AND a rollback — the sibling root update is undone and
    /// the target is still present with <c>deleted_at</c> NULL. A fact that only checked
    /// the throw could not tell an abort from a partial commit, which is the outcome
    /// this policy exists to prevent. The tolerant counterpart, an EXPLICIT save-tree
    /// delete of the same row, is pinned by
    /// <see cref="TreeSync_ExplicitSoftDelete_ScopedAway_ReturnsTolerantly"/>: together
    /// they prove the policy turns on provenance, not on the verb.</para>
    /// </summary>
    [Fact]
    public async Task TreeSync_InferredSoftDelete_ScopedAway_Throws_AndRollsBack()
    {
        await SeedAsync();
        var captured = new List<CapturedSql>();

        // Attachment (1,5) belongs to tenant 2 and is omitted, so it is inferred as an
        // orphan; (1,0) is kept, so the only write to attachments is the orphan's.
        var thrown = await Record.ExceptionAsync(() => SyncAsync(captured, new Dictionary<string, object?>
        {
            ["order_id"] = 1L,
            ["title"] = "Acme Renamed",
            ["attachments"] = new List<Dictionary<string, object?>>
            {
                new() { ["order_id"] = 1L, ["att_no"] = 0L, ["label"] = "mine" },
            },
        }));

        thrown.Should().NotBeNull("an inferred soft delete that affects no rows must abort the sync");
        thrown!.Message.Should().Contain("affected no rows");

        var softDelete = Single(captured, "UPDATE", "attachments");
        softDelete.KeyColumns.Should().Equal("order_id", "att_no");
        softDelete.SetColumns.Should().BeEquivalentTo(new[] { "updated_at", "deleted_at" });
        softDelete.Suffix.Should().Be(" AND ((\"tenant_id\" = @p0) AND (\"deleted_at\" IS NULL))",
            "the tenant scope and the soft-delete guard compose as one ANDed suffix, and the "
            + "tenant half is what excludes the row");

        // Transaction outcome — the whole tree rolls back, including the sibling root update.
        (await ScalarAsync("SELECT title FROM orders WHERE order_id = 1")).Should().Be("Acme");
        (await ScalarAsync("SELECT COUNT(*) FROM attachments WHERE order_id = 1 AND att_no = 5"))
            .Should().Be("1", "the inferred target remains present");
        (await ScalarAsync("SELECT COUNT(*) FROM attachments WHERE deleted_at IS NOT NULL"))
            .Should().Be("0", "the target remains undeleted after rollback");
    }

    // ---- client-addressed operations stay tolerant -------------------------

    /// <summary>
    /// The complement of the inferred facts above, and the half that keeps this slice
    /// from being a narrowing that breaks working callers
    /// (<c>.claude/rules/regression-test-non-vacuous.md</c>): an EXPLICIT save-tree
    /// <c>_op: delete</c> is client-ADDRESSED, so a scoped-away target is an ordinary
    /// no-op — the same tolerance the per-row delete seam gives
    /// (<c>PerRow_Update_ScopedAway_ReturnsAffectedRowsZero_NoThrow</c>).
    ///
    /// <para>The row (1,5) belongs to tenant 2, so the tenant suffix excludes it and the
    /// soft-delete UPDATE affects zero rows — byte-identical circumstances to the
    /// inferred fact above. The ONLY difference is provenance:
    /// <c>TreeSyncOperation.Inferred</c> is false, because only
    /// <c>TreeSyncEngine</c>'s orphan producer stamps it. So this fact is what makes
    /// deriving <c>inferredTarget</c> from the OP KIND (<c>OperationType == Delete</c>)
    /// red: that derivation cannot tell these two apart and would abort a legitimate
    /// client save.</para>
    /// </summary>
    [Fact]
    public async Task TreeSync_ExplicitSoftDelete_ScopedAway_ReturnsTolerantly()
    {
        await SeedAsync();
        var captured = new List<CapturedSql>();
        var model = BuildModel();

        var thrown = await Record.ExceptionAsync(() => ExecuteOpsAsync(captured, model, new[]
        {
            RootTitleUpdate(model),
            // Client-addressed delete of the other tenant's attachment: Inferred stays
            // false, so this is a save the client asked for, not an orphan the diff found.
            new TreeSyncOperation
            {
                Table = model.GetTableFromDbName("attachments"),
                OperationType = TreeSyncOperationType.Delete,
                Data = new Dictionary<string, object?> { ["order_id"] = 1L, ["att_no"] = 5L },
                Depth = 1,
            },
        }));

        thrown.Should().BeNull("a client-addressed delete of a scoped-away row is a no-op, not a failure");

        // The soft-delete rewrite still ran and still matched nothing...
        var softDelete = Single(captured, "UPDATE", "attachments");
        softDelete.KeyColumns.Should().Equal("order_id", "att_no");
        softDelete.Suffix.Should().Be(" AND ((\"tenant_id\" = @p0) AND (\"deleted_at\" IS NULL))");

        // ...and because nothing threw, the transaction COMMITTED: the sibling root
        // update is durable and the out-of-tenant row is untouched.
        (await ScalarAsync("SELECT title FROM orders WHERE order_id = 1")).Should().Be("Acme Renamed");
        (await ScalarAsync("SELECT COUNT(*) FROM attachments WHERE deleted_at IS NOT NULL"))
            .Should().Be("0", "the other tenant's row was never in scope to soft-delete");
    }

    /// <summary>
    /// Same provenance question on the HARD-delete table, pinning the decision: an
    /// EXPLICIT save-tree delete of a scoped-away row is TOLERANT, exactly like its
    /// soft-delete sibling above and unlike
    /// <see cref="TreeSync_InferredHardDelete_ScopedAway_Throws_AndRollsBack"/>.
    ///
    /// <para>Both delete tables are covered on purpose. The soft-delete arm reaches the
    /// executor as an Update and the hard-delete arm as a Delete, so a policy that read
    /// the (post-chain) verb rather than the provenance flag would treat them
    /// differently; one fact could not show that.</para>
    /// </summary>
    [Fact]
    public async Task TreeSync_ExplicitHardDelete_ScopedAway_ReturnsTolerantly()
    {
        await SeedAsync();
        var captured = new List<CapturedSql>();
        var model = BuildModel();

        var thrown = await Record.ExceptionAsync(() => ExecuteOpsAsync(captured, model, new[]
        {
            RootTitleUpdate(model),
            new TreeSyncOperation
            {
                Table = model.GetTableFromDbName("lines"),
                OperationType = TreeSyncOperationType.Delete,
                Data = new Dictionary<string, object?> { ["order_id"] = 1L, ["line_no"] = 2L },
                Depth = 1,
            },
        }));

        thrown.Should().BeNull("a client-addressed hard delete of a scoped-away row is a no-op");
        Single(captured, "DELETE", "lines").KeyColumns.Should().Equal("order_id", "line_no");

        (await ScalarAsync("SELECT title FROM orders WHERE order_id = 1")).Should().Be("Acme Renamed");
        (await ScalarAsync("SELECT COUNT(*) FROM lines WHERE order_id = 1 AND line_no = 2"))
            .Should().Be("1", "the other tenant's row survives; it was never in scope");
    }

    /// <summary>
    /// The third client-addressed shape, and the one the task's own Risk section names:
    /// a tree UPDATE of an out-of-tenant row carrying NO concurrency token stays
    /// tolerant. Nothing about this slice may turn a scoped-away client update into a
    /// hard failure — <c>conflictOnNoRows</c> is false here, and with
    /// <c>inferredTarget</c> false too, zero rows must fall through both arms of the
    /// shared policy and simply return.
    /// </summary>
    [Fact]
    public async Task TreeSync_ClientAddressedUpdate_ScopedAway_ReturnsTolerantly()
    {
        await SeedAsync();
        var captured = new List<CapturedSql>();
        var model = BuildModel();

        var thrown = await Record.ExceptionAsync(() => ExecuteOpsAsync(captured, model, new[]
        {
            new TreeSyncOperation
            {
                Table = model.GetTableFromDbName("lines"),
                OperationType = TreeSyncOperationType.Update,
                Data = new Dictionary<string, object?>
                {
                    ["order_id"] = 1L, ["line_no"] = 2L, ["note"] = "hijacked",
                },
                Depth = 1,
            },
        }));

        thrown.Should().BeNull("a scoped-away client update returns zero rows silently");
        Single(captured, "UPDATE", "lines").Suffix.Should().Be(" AND (\"tenant_id\" = @p0)");
        (await ScalarAsync("SELECT note FROM lines WHERE order_id = 1 AND line_no = 2"))
            .Should().Be("other-tenant", "the write matched no row and committed nothing");
    }

    // ---- degenerate update -------------------------------------------------

    /// <summary>
    /// The executor's own no-op arm: an update whose data carries no key column, or no
    /// non-key column, returns 0 WITHOUT emitting a statement and without throwing.
    /// Both halves in one fact, on a table with no transformer metadata — an audit stamp
    /// would supply the missing SET column and the no-set arm could never be reached.
    /// </summary>
    [Fact]
    public async Task TreeSync_Update_NoKeyOrNoSet_ReturnsZero()
    {
        await SeedAsync();
        var captured = new List<CapturedSql>();
        var model = BuildModel();
        var plain = model.GetTableFromDbName("plain");

        var ops = new[]
        {
            // Key, but nothing to SET.
            new TreeSyncOperation
            {
                Table = plain,
                OperationType = TreeSyncOperationType.Update,
                Data = new Dictionary<string, object?> { ["id"] = 1L },
                Depth = 0,
            },
            // Something to SET, but no key to scope it — an unscoped UPDATE would
            // rewrite every row of the table.
            new TreeSyncOperation
            {
                Table = plain,
                OperationType = TreeSyncOperationType.Update,
                Data = new Dictionary<string, object?> { ["name"] = "rewritten" },
                Depth = 0,
            },
        };

        var result = await new TreeSyncExecutor(_raw.Dialect).ExecuteAsync(
            ops, Logging(captured), Transformers(), model, UserContext());

        result.Should().BeNull("no insert ran, so there is no root key to return");
        captured.Should().NotContain(c => c.Sql.StartsWith("UPDATE ", StringComparison.Ordinal),
            "neither degenerate case reaches the database at all");
        (await ScalarAsync("SELECT name FROM plain WHERE id = 1")).Should().Be("unchanged");
    }

    // ---- fixture plumbing --------------------------------------------------

    /// <summary>
    /// Loader + engine + executor, exactly as <c>DbTableMutateResolver.SyncObject</c>
    /// wires them, with the transformer chain active and every statement captured.
    /// </summary>
    private async Task<object?> SyncAsync(List<CapturedSql> captured, Dictionary<string, object?> tree)
    {
        var model = BuildModel();
        var orders = model.GetTableFromDbName("orders");
        var factory = Logging(captured);
        var loader = new TreeSyncStateLoader(_raw.Dialect, model, UserContext());
        var existing = await loader.LoadAsync(orders, tree, factory);
        var ops = new TreeSyncEngine(model).ComputeOperations(orders, tree, existing);
        return await new TreeSyncExecutor(_raw.Dialect).ExecuteAsync(
            ops, factory, Transformers(), model, UserContext());
    }

    /// <summary>
    /// Drives the executor over ops the test supplies directly, bypassing
    /// <see cref="TreeSyncEngine"/>. That is the point: the engine is the only thing
    /// that stamps <c>Inferred</c>, so ops built here carry the false default and stand
    /// for the EXPLICIT save-tree shape <c>SaveTreeBuilder</c> produces from
    /// <c>_op: delete</c>. Everything downstream — transformer chain, connection
    /// factory, user context — is identical to <see cref="SyncAsync"/>, so a fact pair
    /// differing only in provenance differs only in <c>Inferred</c>.
    /// </summary>
    private Task<object?> ExecuteOpsAsync(
        List<CapturedSql> captured, IDbModel model, IReadOnlyList<TreeSyncOperation> ops)
        => new TreeSyncExecutor(_raw.Dialect).ExecuteAsync(
            ops, Logging(captured), Transformers(), model, UserContext());

    /// <summary>
    /// The in-scope sibling write every tolerance fact needs: if the run commits, this
    /// root update is durable, and if it aborts, the rollback is what erases it. Without
    /// a second write in the tree, "committed" and "rolled back" look the same.
    /// </summary>
    private static TreeSyncOperation RootTitleUpdate(IDbModel model) => new()
    {
        Table = model.GetTableFromDbName("orders"),
        OperationType = TreeSyncOperationType.Update,
        Data = new Dictionary<string, object?> { ["order_id"] = 1L, ["title"] = "Acme Renamed" },
        Depth = 0,
    };

    private SqlLoggingConnFactory Logging(List<CapturedSql> captured)
        => new(_raw, new List<string>(), captured);

    private static IMutationTransformers Transformers() => new MutationTransformersWrap
    {
        Transformers = new IMutationTransformer[]
        {
            new SoftDeleteMutationTransformer(),
            new TenantMutationTransformer(),
            new AuditMutationTransformer(),
        },
    };

    private static IDictionary<string, object?> UserContext()
        => new Dictionary<string, object?> { ["tenant_id"] = 1 };

    /// <summary>One emitted statement decomposed into the parts a keyed-write fact reasons about.</summary>
    private sealed record WriteSql(
        string Sql,
        IReadOnlyList<string> SetColumns,
        IReadOnlyList<string> KeyColumns,
        string Suffix,
        IReadOnlyList<string> ParameterNames);

    // Column LISTS rather than substrings: exact list equality is word-boundary safe by
    // construction, where Contains("order_id") would also match a longer identifier.
    private static readonly Regex AssignmentPattern =
        new(@"""(?<col>[^""]+)""=@(?<param>\w+)", RegexOptions.NonBacktracking);

    private static WriteSql Parse(CapturedSql captured)
    {
        var sql = captured.Sql.TrimEnd(';');
        var setPart = "";
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
        var keyPart = whereText;
        var suffix = "";
        var suffixIndex = whereText.IndexOf(" AND (", StringComparison.Ordinal);
        if (suffixIndex >= 0)
        {
            keyPart = whereText[..suffixIndex];
            suffix = whereText[suffixIndex..];
        }

        return new WriteSql(sql, Columns(setPart), Columns(keyPart), suffix, captured.ParameterNames);
    }

    private static IReadOnlyList<string> Columns(string clause) =>
        AssignmentPattern.Matches(clause).Select(m => m.Groups["col"].Value).ToList();

    /// <summary>
    /// The one statement of the given verb against the given table. Asserting there is
    /// exactly one keeps a fact from silently reading a sibling operation's statement.
    /// </summary>
    private static WriteSql Single(List<CapturedSql> captured, string verb, string table)
    {
        var matches = captured
            .Where(c => c.Sql.StartsWith(verb + " ", StringComparison.Ordinal)
                        && c.Sql.Contains($"\"{table}\"", StringComparison.Ordinal))
            .ToList();
        matches.Should().ContainSingle($"the seam must emit exactly one {verb} against {table}");
        return Parse(matches[0]);
    }

    private async Task Exec(string sql)
        => await RawSqlExecutor.ExecuteAsync(_raw, sql, null, 30, 1000);

    private async Task<string?> ScalarAsync(string sql)
    {
        var result = await RawSqlExecutor.ExecuteAsync(_raw, sql, null, 30, 1000);
        return result.Rows.Count == 0 ? null : result.Rows[0][0]?.ToString();
    }
}
