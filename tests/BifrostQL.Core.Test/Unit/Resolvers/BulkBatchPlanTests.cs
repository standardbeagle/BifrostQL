using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Resolvers.BulkBatch;
using BifrostQL.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BatchAction = BifrostQL.Core.Resolvers.BatchMutationPipeline.BatchAction;

namespace BifrostQL.Core.Test.Unit.Resolvers;

public sealed class BulkBatchPlanTests
{
    private sealed class StubConnFactory : IDbConnFactory
    {
        public DbConnection GetConnection() => throw new NotSupportedException();
        public ISqlDialect Dialect { get; } = new SqlServerDialect();
        public ISchemaReader SchemaReader => throw new NotSupportedException();
        public ITypeMapper TypeMapper => throw new NotSupportedException();
    }

    private static IDbModel BuildModel(Action<DbModelTestFixture.TableBuilder>? extra = null)
        => DbModelTestFixture.Create()
            .WithTable("Orders", t =>
            {
                t.WithSchema("dbo").WithPrimaryKey("OrderId").WithPrimaryKey("LineNo")
                    .WithColumn("Status", "nvarchar")
                    .WithColumn("Total", "decimal")
                    .WithMetadata("bulk-batch-threshold", "1");
                extra?.Invoke(t);
            })
            .Build();

    private static MutationPipelineContext BuildContext(
        IDbModel model, IMutationTransformer[]? transformers = null, IServiceProvider? services = null)
        => new()
        {
            Model = model,
            ConnFactory = new StubConnFactory(),
            Transformers = new MutationTransformersWrap { Transformers = transformers ?? Array.Empty<IMutationTransformer>() },
            UserContext = new Dictionary<string, object?>(),
            Services = services,
        };

    private static MutationTransformContext TransformContext(MutationPipelineContext ctx)
        => new() { Model = ctx.Model, UserContext = ctx.UserContext, Services = ctx.Services };

    private static Task<BulkBatchPlanBuilder.BuiltBulkBatch?> BuildAsync(
        MutationPipelineContext ctx, params BatchAction[] actions)
    {
        var table = ctx.Model.GetTableFromDbName("Orders");
        return BulkBatchPlanBuilder.TryBuildAsync(table, actions, ctx, TransformContext(ctx));
    }

    private static BatchAction Insert(params (string Col, object? Value)[] values)
        => new(MutationAction.Insert, values.ToDictionary(v => v.Col, v => v.Value));

    private static BatchAction Update(params (string Col, object? Value)[] values)
        => new(MutationAction.Update, values.ToDictionary(v => v.Col, v => v.Value));

    private static BatchAction Delete(params (string Col, object? Value)[] values)
        => new(MutationAction.Delete, values.ToDictionary(v => v.Col, v => v.Value));

    // ---- gating ----

    [Fact]
    public async Task BelowDefaultThreshold_FallsBack()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t.WithPrimaryKey("OrderId").WithColumn("Status", "nvarchar"))
            .Build();
        var ctx = BuildContext(model);
        var table = model.GetTableFromDbName("Orders");

        BulkBatchPlanBuilder.GetBulkThreshold(table).Should().Be(BulkBatchPlanBuilder.DefaultBulkThreshold);
        var built = await BulkBatchPlanBuilder.TryBuildAsync(
            table, new[] { Insert(("Status", "new")) }, ctx, TransformContext(ctx));
        built.Should().BeNull();
    }

    [Fact]
    public async Task ThresholdZero_DisablesFastPath()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t.WithPrimaryKey("OrderId").WithColumn("Status", "nvarchar")
                .WithMetadata("bulk-batch-threshold", "0"))
            .Build();
        var ctx = BuildContext(model);
        var table = model.GetTableFromDbName("Orders");

        BulkBatchPlanBuilder.IsEligible(table, new[] { Insert(("Status", "new")) }, ctx, out var reason)
            .Should().BeFalse();
        reason.Should().Contain("disables");
    }

    [Fact]
    public async Task UpsertAction_FallsBack()
    {
        var ctx = BuildContext(BuildModel());
        var built = await BuildAsync(ctx,
            new BatchAction(MutationAction.Upsert, new Dictionary<string, object?> { ["OrderId"] = 1, ["LineNo"] = 1, ["Status"] = "x" }));
        built.Should().BeNull();
    }

    [Fact]
    public async Task StateMachineTable_FallsBack()
    {
        var ctx = BuildContext(BuildModel(t => t
            .WithMetadata(MetadataKeys.StateMachine.StateColumn, "Status")
            .WithMetadata(MetadataKeys.StateMachine.InitialState, "draft")
            .WithMetadata(MetadataKeys.StateMachine.States, "draft,done")
            .WithMetadata(MetadataKeys.StateMachine.Transitions, "draft->done")));
        var built = await BuildAsync(ctx, Insert(("Status", "draft")));
        built.Should().BeNull();
    }

    private sealed class NoopBeforeCommitHook : IBeforeCommitMutationHook
    {
        public ValueTask<IReadOnlyList<string>> BeforeCommitAsync(MutationObserverContext context)
            => ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    [Fact]
    public async Task RegisteredBeforeCommitHook_FallsBack()
    {
        var services = new ServiceCollection()
            .AddSingleton(new BeforeCommitMutationHooks(new IBeforeCommitMutationHook[] { new NoopBeforeCommitHook() }))
            .BuildServiceProvider();
        var ctx = BuildContext(BuildModel(), services: services);

        var built = await BuildAsync(ctx, Insert(("Status", "new")));
        built.Should().BeNull();
    }

    [Fact]
    public async Task EmptyHookComposites_DoNotBlockFastPath()
    {
        var services = new ServiceCollection()
            .AddSingleton(new BeforeCommitMutationHooks(Array.Empty<IBeforeCommitMutationHook>()))
            .AddSingleton(new InTransactionMutationHooks(Array.Empty<IInTransactionMutationHook>()))
            .BuildServiceProvider();
        var ctx = BuildContext(BuildModel(), services: services);

        var built = await BuildAsync(ctx, Insert(("Status", "new")));
        built.Should().NotBeNull();
    }

    // ---- plan content ----

    [Fact]
    public async Task MixedBatch_CompositePk_BuildsCorrectPlan()
    {
        var ctx = BuildContext(BuildModel());

        // PK value 0 is a legitimate key value and must survive into staging.
        var built = await BuildAsync(ctx,
            Insert(("Status", "new"), ("Total", 10m)),
            Update(("OrderId", 0), ("LineNo", 2), ("Status", "paid")),
            Delete(("OrderId", 3), ("LineNo", 1)));

        built.Should().NotBeNull();
        var plan = built!.Plan;
        plan.TableSchema.Should().Be("dbo");
        plan.TableDbName.Should().Be("Orders");
        plan.Rows.Should().HaveCount(3);

        var insertRow = plan.Rows[0];
        insertRow.Op.Should().Be(BulkOpCode.Insert);
        insertRow.Seq.Should().Be(0);
        insertRow.Values.Should().Equal(new Dictionary<string, object?> { ["Status"] = "new", ["Total"] = 10m });

        var updateRow = plan.Rows[1];
        updateRow.Op.Should().Be(BulkOpCode.Update);
        updateRow.Values["OrderId"].Should().Be(0);
        var updateGroup = plan.Groups.Single(g => g.Id == updateRow.Group);
        // Composite key: BOTH key columns join the staging row to the target row.
        updateGroup.KeyColumns.Should().BeEquivalentTo(new[] { "OrderId", "LineNo" });
        updateGroup.SetColumns.Should().BeEquivalentTo(new[] { "Status" });

        var deleteRow = plan.Rows[2];
        deleteRow.Op.Should().Be(BulkOpCode.Delete);
        var deleteGroup = plan.Groups.Single(g => g.Id == deleteRow.Group);
        // Hard delete predicates on EVERY supplied column, matching BuildDeleteSql.
        deleteGroup.KeyColumns.Should().BeEquivalentTo(new[] { "OrderId", "LineNo" });
        deleteGroup.SetColumns.Should().BeEmpty();

        plan.Groups.Should().HaveCount(3);
        plan.StagingColumns.Should().BeEquivalentTo(new[] { "Status", "Total", "OrderId", "LineNo" });

        built.Outcomes.Should().HaveCount(3);
        built.Outcomes.Select(o => o.MutationType).Should().Equal(
            MutationType.Insert, MutationType.Update, MutationType.Delete);
    }

    [Fact]
    public async Task DifferentColumnSignatures_SplitIntoGroups()
    {
        var ctx = BuildContext(BuildModel());

        // Explicit NULL is a different signature from an absent column: the second insert
        // MUST NOT ride in the first group (its staged NULL would be indistinguishable).
        var built = await BuildAsync(ctx,
            Insert(("Status", "a"), ("Total", 1m)),
            Insert(("Status", null), ("Total", 2m)),
            Insert(("Total", 3m)));

        built.Should().NotBeNull();
        var plan = built!.Plan;
        plan.Groups.Should().HaveCount(2);
        plan.Rows[0].Group.Should().Be(plan.Rows[1].Group);
        plan.Rows[2].Group.Should().NotBe(plan.Rows[0].Group);
    }

    [Fact]
    public async Task TooManyGroups_FallsBack()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t =>
            {
                t.WithPrimaryKey("OrderId").WithMetadata("bulk-batch-threshold", "1");
                for (var i = 0; i < 10; i++)
                    t.WithColumn($"C{i}", "nvarchar");
            })
            .Build();
        var ctx = BuildContext(model);
        var table = model.GetTableFromDbName("Orders");

        var actions = Enumerable.Range(0, BulkBatchPlanBuilder.MaxOpGroups + 1)
            .Select(i => Insert(($"C{i}", "v")))
            .ToArray();
        var built = await BulkBatchPlanBuilder.TryBuildAsync(table, actions, ctx, TransformContext(ctx));
        built.Should().BeNull();
    }

    [Fact]
    public async Task DuplicateUpdateKeys_FallBack()
    {
        // The per-row path applies duplicate-key updates in order (last wins); a set-based
        // UPDATE..JOIN with two staged rows matching one target row is engine-nondeterministic.
        var ctx = BuildContext(BuildModel());

        var built = await BuildAsync(ctx,
            Update(("OrderId", 1), ("LineNo", 1), ("Status", "first")),
            Update(("OrderId", 1), ("LineNo", 1), ("Status", "second")));

        built.Should().BeNull();
    }

    [Fact]
    public async Task DuplicateDeleteKeys_FallBack()
    {
        // The per-row path reports 1 affected then 0; a set-based DELETE..JOIN would double-count
        // the row in the out-table. Fall back rather than diverge.
        var ctx = BuildContext(BuildModel());

        var built = await BuildAsync(ctx,
            Delete(("OrderId", 1), ("LineNo", 1)),
            Delete(("OrderId", 1), ("LineNo", 1)));

        built.Should().BeNull();
    }

    [Fact]
    public async Task DistinctKeys_AcrossOps_DoNotTriggerDuplicateGate()
    {
        // The same key deleted AND updated is two ops on one row — also order-dependent, so it
        // must fall back too; distinct keys within each op build fine.
        var ctx = BuildContext(BuildModel());

        var overlapping = await BuildAsync(ctx,
            Update(("OrderId", 1), ("LineNo", 1), ("Status", "x")),
            Delete(("OrderId", 1), ("LineNo", 1)));
        overlapping.Should().BeNull();

        var distinct = await BuildAsync(ctx,
            Update(("OrderId", 1), ("LineNo", 1), ("Status", "x")),
            Update(("OrderId", 1), ("LineNo", 2), ("Status", "y")),
            Delete(("OrderId", 2), ("LineNo", 1)));
        distinct.Should().NotBeNull();
    }

    // ---- transformer interaction ----

    [Fact]
    public async Task SoftDeleteTable_DeleteBecomesUpdateWithFilter()
    {
        var ctx = BuildContext(
            BuildModel(t => t
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithMetadata(MetadataKeys.SoftDelete.Column, "deleted_at")),
            transformers: new IMutationTransformer[] { new SoftDeleteMutationTransformer() });

        var built = await BuildAsync(ctx, Delete(("OrderId", 5), ("LineNo", 1)));

        built.Should().NotBeNull();
        var plan = built!.Plan;
        var row = plan.Rows.Single();
        // DELETE was rewritten to a soft-delete UPDATE stamping deleted_at.
        row.Op.Should().Be(BulkOpCode.Update);
        row.Values.Keys.Should().Contain("deleted_at");
        var group = plan.Groups.Single();
        group.KeyColumns.Should().BeEquivalentTo(new[] { "OrderId", "LineNo" });
        group.SetColumns.Should().Contain("deleted_at");
        // The soft-delete guard filter (deleted_at IS NULL) rode along.
        group.FilterSql.Should().Contain("deleted_at");
        built.Outcomes.Single().MutationType.Should().Be(MutationType.Update);
    }

    private sealed class PerRowFilterTransformer : IMutationTransformer
    {
        private readonly bool _vary;
        private int _n;
        public PerRowFilterTransformer(bool vary) => _vary = vary;
        public int Priority => 10;
        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => mutationType != MutationType.Insert;

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            var value = _vary ? _n++ : 42;
            var filter = TableFilter.FromObject(
                new Dictionary<string, object?> { ["Total"] = new Dictionary<string, object?> { ["_eq"] = value } },
                table.DbName);
            return ValueTask.FromResult(new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data,
                AdditionalFilter = filter,
            });
        }
    }

    [Fact]
    public async Task HomogeneousFilters_LandInPlanWithParameters()
    {
        var ctx = BuildContext(BuildModel(), transformers: new IMutationTransformer[] { new PerRowFilterTransformer(vary: false) });

        var built = await BuildAsync(ctx,
            Update(("OrderId", 1), ("LineNo", 1), ("Status", "a")),
            Update(("OrderId", 2), ("LineNo", 1), ("Status", "b")));

        built.Should().NotBeNull();
        var group = built!.Plan.Groups.Single();
        group.FilterSql.Should().StartWith(" AND (");
        group.FilterParameters.Should().ContainSingle(p => Equals(p.Value, 42));
    }

    [Fact]
    public async Task HeterogeneousFilters_FallBack()
    {
        var ctx = BuildContext(BuildModel(), transformers: new IMutationTransformer[] { new PerRowFilterTransformer(vary: true) });

        var built = await BuildAsync(ctx,
            Update(("OrderId", 1), ("LineNo", 1), ("Status", "a")),
            Update(("OrderId", 2), ("LineNo", 1), ("Status", "b")));

        built.Should().BeNull();
    }
}
