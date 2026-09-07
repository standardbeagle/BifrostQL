using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Resolvers.BulkBatch;
using BifrostQL.SqlServer;
using FluentAssertions;
using Xunit;
using BatchAction = BifrostQL.Core.Resolvers.BatchMutationPipeline.BatchAction;

namespace BifrostQL.Core.Test.Unit.Resolvers;

/// <summary>
/// CHARACTERIZATION — the predicate/SET column split and the zero-row policy of the
/// set-based fast path (<see cref="BulkBatchPlanBuilder"/>), the sibling of
/// <c>KeyedWriteSeamCharacterizationTests</c> (CHAR-1, per-row/batch/filtered). These
/// facts assert nothing about what the plan OUGHT to contain; they pin what it DOES
/// contain at this commit. No production file changes with them.
///
/// <para>Three of them are named <c>Current_*</c>: they pin behaviour that DIVERGES
/// from the per-row contract and that CHAR-4 will re-baseline. They assert the
/// divergence POSITIVELY — the client predicate column IS in SetColumns, the audit
/// stamp IS in KeyColumns, the conflict flag IS dropped — so a slice that unifies the
/// seams cannot land without turning them red.</para>
///
/// <para>SQLite exposes no bulk executor and the SQL Server one needs a server, so the
/// observable artefact here is the PLAN — <see cref="BulkOpGroup.KeyColumns"/> /
/// <see cref="BulkOpGroup.SetColumns"/> / <see cref="BulkOpGroup.FilterSql"/> and
/// <see cref="BulkStagedAction.ConflictOnNoRows"/> — exactly as
/// <c>BulkBatchPlanTests</c> asserts it. The group's key columns are the columns the
/// staged UPDATE/DELETE joins the target table on, so they ARE the WHERE.</para>
///
/// <para><b>Why the fixture is shaped this way</b>
/// (<c>.claude/rules/regression-test-non-vacuous.md</c>): a composite primary key
/// <c>(OrderId, LineNo)</c> — a single-column key cannot show whether the whole key
/// reaches the join; the key value <c>0</c>, so presence is never decided by
/// truthiness; an <c>updated_at { populate: updated-on }</c> stamp, which
/// <see cref="AuditMutationTransformer"/> adds on Update AND on Delete — without a
/// stamping transformer the hard-delete fact below is green either way; a
/// <c>tenant-filter</c> so the chain contributes a rendered <see cref="BulkOpGroup.FilterSql"/>;
/// and a second, soft-delete table so the rewritten-delete branch has its own facts.</para>
///
/// <para>Where a fact claims the bulk plan AGREES with (or DIVERGES from) the per-row
/// contract, it runs the per-row rule itself — <see cref="MutationArgumentBinder.SplitProperties"/>
/// for the update split, <c>TableMutationPipeline.SelectPredicateColumns</c> for the
/// delete predicate — over the SAME transformed row inside the same fact, and compares
/// the two outputs. Two separately written expectations can drift together; a direct
/// comparison cannot.</para>
/// </summary>
public sealed class BulkBatchPlanCharacterizationTests
{
    private sealed class StubConnFactory : IDbConnFactory
    {
        public DbConnection GetConnection() => throw new NotSupportedException();
        public ISqlDialect Dialect { get; } = new SqlServerDialect();
        public ISchemaReader SchemaReader => throw new NotSupportedException();
        public ITypeMapper TypeMapper => throw new NotSupportedException();
    }

    /// <summary>
    /// <c>Orders</c> is the hard-delete table: composite key, tenant scope, audit stamp.
    /// <c>Notes</c> adds the soft-delete rewrite on the same shape.
    /// </summary>
    private static IDbModel BuildModel()
        => DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("OrderId").WithPrimaryKey("LineNo")
                .WithColumn("Status", "nvarchar")
                .WithColumn("Total", "decimal")
                .WithColumn("tenant_id", "int")
                .WithColumn("updated_at", "datetime2", isNullable: true)
                .WithColumnMetadata("updated_at", MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.UpdatedOn)
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id")
                .WithMetadata(MetadataKeys.Batch.BulkThreshold, "1"))
            .WithTable("Notes", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("NoteId").WithPrimaryKey("Region", "nvarchar")
                .WithColumn("Status", "nvarchar")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithColumn("tenant_id", "int")
                .WithColumn("updated_at", "datetime2", isNullable: true)
                .WithColumnMetadata("updated_at", MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.UpdatedOn)
                .WithMetadata(MetadataKeys.SoftDelete.Column, "deleted_at")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id")
                .WithMetadata(MetadataKeys.Batch.BulkThreshold, "1"))
            .Build();

    /// <summary>The chain as a host wires it, minus the transformers this fixture cannot reach.</summary>
    private static IMutationTransformer[] Chain() => new IMutationTransformer[]
    {
        new SoftDeleteMutationTransformer(),
        new TenantMutationTransformer(),
        new AuditMutationTransformer(),
    };

    private static MutationPipelineContext BuildContext(
        IDbModel model, IMutationTransformer[]? transformers = null)
        => new()
        {
            Model = model,
            ConnFactory = new StubConnFactory(),
            Transformers = new MutationTransformersWrap { Transformers = transformers ?? Chain() },
            UserContext = new Dictionary<string, object?> { ["tenant_id"] = 1 },
            Services = null,
        };

    private static MutationTransformContext TransformContext(MutationPipelineContext ctx)
        => new() { Model = ctx.Model, UserContext = ctx.UserContext, Services = ctx.Services };

    private static Task<BulkBatchPlanBuilder.BuiltBulkBatch?> BuildAsync(
        MutationPipelineContext ctx, string table, params BatchAction[] actions)
        => BulkBatchPlanBuilder.TryBuildAsync(
            ctx.Model.GetTableFromDbName(table), actions, ctx, TransformContext(ctx));

    private static BatchAction Update(params (string Col, object? Value)[] values)
        => new(MutationAction.Update, values.ToDictionary(v => v.Col, v => v.Value));

    private static BatchAction Delete(params (string Col, object? Value)[] values)
        => new(MutationAction.Delete, values.ToDictionary(v => v.Col, v => v.Value));

    /// <summary>The single group of a single-action plan, with its staged row.</summary>
    private static (BulkOpGroup Group, BulkStagedAction Row) Single(BulkBatchPlanBuilder.BuiltBulkBatch? built)
    {
        built.Should().NotBeNull("the fixture is at the bulk threshold, so the fast path must be taken");
        var row = built!.Plan.Rows.Should().ContainSingle().Subject;
        return (built.Plan.Groups.Single(g => g.Id == row.Group), row);
    }

    // ---- update: the one split every seam agrees on ----------------------

    /// <summary>
    /// The staged UPDATE joins on the WHOLE primary key and writes everything else —
    /// the client's column, the tenant column the chain pinned, and the audit stamp the
    /// chain added. The transformer's row scope rides along as the rendered
    /// <see cref="BulkOpGroup.FilterSql"/> suffix, never as a key column.
    ///
    /// The agreement with the per-row seam is not asserted twice; the per-row rule
    /// (<see cref="MutationArgumentBinder.SplitProperties"/>) is run over the SAME staged
    /// row and its output compared to the plan's.
    /// </summary>
    [Fact]
    public async Task Bulk_Update_KeyColumns_IsPk_SetColumns_IsRemainderIncludingAuditStamp_FilterSqlIsTenant()
    {
        var ctx = BuildContext(BuildModel());

        // Key value 0 is a legitimate key value.
        var built = await BuildAsync(ctx, "Orders", Update(("OrderId", 0), ("LineNo", 2), ("Status", "paid")));
        var (group, row) = Single(built);

        group.Op.Should().Be(BulkOpCode.Update);
        group.KeyColumns.Should().BeEquivalentTo(new[] { "OrderId", "LineNo" });
        group.SetColumns.Should().BeEquivalentTo(new[] { "Status", "tenant_id", "updated_at" });
        // The two halves that carry weight, as explicit negatives.
        group.SetColumns.Should().Contain("updated_at", "the audit stamp is written, not matched");
        group.KeyColumns.Should().NotContain("updated_at",
            "a chain-stamped value in the join predicate would match no row (invariant 8(c))");
        group.KeyColumns.Should().NotContain("tenant_id",
            "tenant scope arrives as the rendered filter suffix, never as a key column");
        group.FilterSql.Should().StartWith(" AND (").And.Contain("tenant_id");
        group.FilterParameters.Should().ContainSingle().Which.Value.Should().Be(1);

        // ---- the per-row rule, run over the same staged row -------------
        var table = ctx.Model.GetTableFromDbName("Orders");
        var (_, perRowKeys, perRowSet) = MutationArgumentBinder.SplitProperties(table, row.Values, null);
        perRowKeys.Keys.Should().BeEquivalentTo(group.KeyColumns,
            "the bulk fast path and the per-row pipelines derive the SAME update key split");
        perRowSet.Keys.Should().BeEquivalentTo(group.SetColumns);
        perRowKeys["OrderId"].Should().Be(0, "and the falsy key value survives into staging");
    }

    // ---- delete: the three divergences ----------------------------------

    /// <summary>
    /// CURRENT, DIVERGENT — re-baselined by CHAR-4.
    ///
    /// <para>A client that scopes a delete with an extra predicate column
    /// (<c>Status: "archived"</c> alongside the key) gets that column WRITTEN into every
    /// matched row on the bulk path: <see cref="BulkBatchPlanBuilder"/>'s soft-delete
    /// branch splits by <c>IsPrimaryKeyColumn</c> alone, so every non-key column — the
    /// client's predicate included — falls into the SET list. The per-row seam routes the
    /// same payload through <c>SelectPredicateColumns</c> and puts <c>Status</c> in the
    /// WHERE; both rules are run here and the divergence is asserted directly. This is the
    /// hazard the comment at <c>TableMutationPipeline.SelectPredicateColumns</c> names.</para>
    /// </summary>
    [Fact]
    public async Task Bulk_SoftDelete_ClientPredicateColumn_InWhere_NotInSet()
    {
        var ctx = BuildContext(BuildModel());
        var table = ctx.Model.GetTableFromDbName("Notes");

        var built = await BuildAsync(ctx, "Notes",
            Delete(("NoteId", 0), ("Region", "west"), ("Status", "archived")));
        var (group, row) = Single(built);

        group.Op.Should().Be(BulkOpCode.Update, "a soft delete is staged as an UPDATE");
        // POSITIVE assertion of the divergent behaviour: the predicate column is WRITTEN.
        group.SetColumns.Should().NotContain("Status");
        group.KeyColumns.Should().Contain("Status");
        group.KeyColumns.Should().BeEquivalentTo(new[] { "NoteId", "Region", "Status" });
        group.SetColumns.Should().BeEquivalentTo(new[] { "updated_at", "deleted_at" });

        // ---- the per-row rule, over the same staged row ------------------
        var clientColumns = new HashSet<string>(
            new[] { "NoteId", "Region", "Status" }, StringComparer.OrdinalIgnoreCase);
        var perRowPredicate = TableMutationPipeline.SelectPredicateColumns(
            row.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            clientColumns, table);
        perRowPredicate.Keys.Should().Contain("Status",
            "the per-row seam matches on the client's predicate column");
        group.KeyColumns.Should().BeEquivalentTo(perRowPredicate.Keys);
    }

    /// <summary>
    /// CURRENT, DIVERGENT — re-baselined by CHAR-4.
    ///
    /// <para>The bulk HARD delete predicates on EVERY transformed column, so the
    /// <c>updated_at</c> that <see cref="AuditMutationTransformer"/> stamps on a Delete
    /// becomes a join column: <c>updated_at = &lt;now&gt;</c> matches no stored row
    /// (protocol-adapter-security invariant 8(c)). The per-row seam excludes it, as the
    /// comparison below shows.</para>
    /// </summary>
    [Fact]
    public async Task Bulk_HardDelete_AuditStamp_NotInKeyColumns()
    {
        var ctx = BuildContext(BuildModel());
        var table = ctx.Model.GetTableFromDbName("Orders");

        var built = await BuildAsync(ctx, "Orders",
            Delete(("OrderId", 3), ("LineNo", 1), ("Status", "open")));
        var (group, row) = Single(built);

        group.Op.Should().Be(BulkOpCode.Delete);
        group.SetColumns.Should().BeEmpty("a hard delete has no SET list");
        // POSITIVE assertion of the divergent behaviour.
        group.KeyColumns.Should().NotContain("updated_at");
        group.KeyColumns.Should().BeEquivalentTo(new[] { "OrderId", "LineNo", "Status" });
        row.Values.Should().ContainKey("updated_at").WhoseValue.Should().NotBeNull(
            "and the stamped value is a fresh timestamp, so it cannot match a stored row");
        group.FilterSql.Should().StartWith(" AND (").And.Contain("tenant_id");

        // ---- the per-row rule, over the same staged row ------------------
        var clientColumns = new HashSet<string>(
            new[] { "OrderId", "LineNo", "Status" }, StringComparer.OrdinalIgnoreCase);
        var perRowPredicate = TableMutationPipeline.SelectPredicateColumns(
            row.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            clientColumns, table);
        perRowPredicate.Keys.Should().NotContain("updated_at",
            "the per-row seam builds its predicate from the PRE-chain client columns");
        group.KeyColumns.Should().BeEquivalentTo(perRowPredicate.Keys);
    }

    /// <summary>A transformer that asks for zero rows to be a conflict, and nothing else.</summary>
    private sealed class ConflictOnNoRowsTransformer : IMutationTransformer
    {
        public int Priority => 200;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => mutationType != MutationType.Insert;

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data,
            MutationTransformContext context)
            => ValueTask.FromResult(new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data,
                ConflictOnNoRows = true,
            });
    }

    /// <summary>
    /// CURRENT, DIVERGENT — re-baselined by CHAR-4.
    ///
    /// <para><see cref="BulkBatchPlanBuilder"/> hardcodes <c>ConflictOnNoRows: false</c> on
    /// BOTH delete branches, so a transformer that raises the flag (a concurrency token, a
    /// guard that must not silently match nothing) is obeyed on an update and dropped on a
    /// delete. The update arm is the control: it proves the flag reaches staging at all, so
    /// the delete arms cannot be green because the plumbing is absent.</para>
    /// </summary>
    [Fact]
    public async Task Bulk_Delete_ConflictOnNoRows_FollowsChainResult()
    {
        var transformers = Chain().Concat(new IMutationTransformer[] { new ConflictOnNoRowsTransformer() }).ToArray();

        // Control: the same flag from the same transformer DOES reach an update's staged row.
        var updateCtx = BuildContext(BuildModel(), transformers);
        var (_, updateRow) = Single(await BuildAsync(updateCtx, "Orders",
            Update(("OrderId", 1), ("LineNo", 1), ("Status", "paid"))));
        updateRow.ConflictOnNoRows.Should().BeTrue(
            "the update branch carries the transformer's flag into staging");

        // Hard delete: the flag is dropped.
        var hardCtx = BuildContext(BuildModel(), transformers);
        var (_, hardRow) = Single(await BuildAsync(hardCtx, "Orders",
            Delete(("OrderId", 2), ("LineNo", 1))));
        hardRow.Op.Should().Be(BulkOpCode.Delete);
        hardRow.ConflictOnNoRows.Should().BeTrue();

        // Soft delete (the rewritten UPDATE): the flag is dropped there too.
        var softCtx = BuildContext(BuildModel(), transformers);
        var (softGroup, softRow) = Single(await BuildAsync(softCtx, "Notes",
            Delete(("NoteId", 1), ("Region", "west"))));
        softGroup.Op.Should().Be(BulkOpCode.Update);
        softRow.ConflictOnNoRows.Should().BeTrue();
    }

    // ---- the guard that must survive the convergence ---------------------

    /// <summary>
    /// M3's completeness guard, restated here so it stays green through CHAR-4/CHAR-5:
    /// a partial composite key reaches NO set-based statement on any of the three
    /// branches. One statement joined on part of a two-column key rewrites or removes
    /// every row sharing the supplied column, so each branch needs the guard in its own
    /// right — the soft-delete rewrite derives its key columns separately from the hard
    /// delete, and the update from both.
    /// </summary>
    [Fact]
    public async Task Bulk_RequireCompleteKey_PartialCompositeKey_Throws()
    {
        var ctx = BuildContext(BuildModel());

        BulkBatchPlanBuilder.BuiltBulkBatch? update = null;
        var updateThrew = await Record.ExceptionAsync(async () =>
            update = await BuildAsync(ctx, "Orders", Update(("LineNo", 1), ("Status", "paid"))));
        update.Should().BeNull();
        updateThrew.Should().BeOfType<BifrostExecutionError>()
            .Which.ErrorCode.Should().Be("PARTIAL_PRIMARY_KEY");

        BulkBatchPlanBuilder.BuiltBulkBatch? hardDelete = null;
        var hardThrew = await Record.ExceptionAsync(async () =>
            hardDelete = await BuildAsync(ctx, "Orders", Delete(("LineNo", 1))));
        hardDelete.Should().BeNull();
        hardThrew.Should().BeOfType<BifrostExecutionError>()
            .Which.ErrorCode.Should().Be("PARTIAL_PRIMARY_KEY");

        BulkBatchPlanBuilder.BuiltBulkBatch? softDelete = null;
        var softThrew = await Record.ExceptionAsync(async () =>
            softDelete = await BuildAsync(ctx, "Notes", Delete(("Region", "west"))));
        softDelete.Should().BeNull();
        softThrew.Should().BeOfType<BifrostExecutionError>()
            .Which.ErrorCode.Should().Be("PARTIAL_PRIMARY_KEY");
    }
}
