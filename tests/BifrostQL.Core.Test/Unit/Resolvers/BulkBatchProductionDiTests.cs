using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Resolvers.BulkBatch;
using BifrostQL.Server;
using BifrostQL.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BatchAction = BifrostQL.Core.Resolvers.BatchMutationPipeline.BatchAction;

namespace BifrostQL.Core.Test.Unit.Resolvers;

/// <summary>
/// The bulk set-based batch gate under the REAL <c>AddBifrostQL</c> container. The sibling
/// <see cref="BulkBatchPlanTests"/> composes the hook composites by hand, so it never sees the
/// four hooks production DI registers unconditionally (history, approval, deferred, CDC) — the
/// blind spot that let EVERY batch fall back to the per-row path in every host (finding H5).
///
/// The gate is per-TABLE applicability: a table no registered hook can act on takes the fast
/// path even though four hooks are registered, and a table any hook DOES act on still falls back.
/// </summary>
public sealed class BulkBatchProductionDiTests
{
    private sealed class StubConnFactory : IDbConnFactory
    {
        public DbConnection GetConnection() => throw new NotSupportedException();
        public ISqlDialect Dialect { get; } = new SqlServerDialect();
        public ISchemaReader SchemaReader => throw new NotSupportedException();
        public ITypeMapper TypeMapper => throw new NotSupportedException();
    }

    /// <summary>
    /// The production container. The endpoint's model loader is lazy, so no database is
    /// touched; what matters is that every built-in mutation hook is registered exactly as
    /// <c>AddBifrostQL</c> registers it in a host.
    /// </summary>
    private static ServiceProvider BuildProductionServices()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bifrost:DisableAuth"] = "true",
                ["Bifrost:Path"] = "/graphql",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBifrostQL(o => o
            .BindConfiguration(config.GetSection("Bifrost"))
            .BindConnectionString("Data Source=bifrost_bulk_prod_di;Mode=Memory;Cache=Shared")
            .BindProvider("sqlite"));
        return services.BuildServiceProvider();
    }

    private static IDbModel BuildModel(Action<DbModelTestFixture.TableBuilder>? extra = null)
        => DbModelTestFixture.Create()
            .WithTable("Orders", t =>
            {
                t.WithSchema("dbo").WithPrimaryKey("OrderId")
                    .WithColumn("Status", "nvarchar")
                    .WithColumn("Total", "decimal")
                    .WithMetadata(MetadataKeys.Batch.BulkThreshold, "1");
                extra?.Invoke(t);
            })
            .Build();

    private static MutationPipelineContext BuildContext(IDbModel model, IServiceProvider services)
        => new()
        {
            Model = model,
            ConnFactory = new StubConnFactory(),
            Transformers = new MutationTransformersWrap { Transformers = Array.Empty<IMutationTransformer>() },
            UserContext = new Dictionary<string, object?>(),
            Services = services,
        };

    private static async Task<BulkBatchPlanBuilder.BuiltBulkBatch?> BuildAsync(MutationPipelineContext ctx)
    {
        var table = ctx.Model.GetTableFromDbName("Orders");
        var actions = new[]
        {
            new BatchAction(MutationAction.Insert, new Dictionary<string, object?> { ["Status"] = "new", ["Total"] = 10m }),
        };
        return await BulkBatchPlanBuilder.TryBuildAsync(
            table, actions, ctx,
            new MutationTransformContext { Model = ctx.Model, UserContext = ctx.UserContext, Services = ctx.Services });
    }

    [Fact]
    public async Task FastPath_UnderProductionDi_IsTakenForATableNoHookActsOn()
    {
        await using var provider = BuildProductionServices();
        var ctx = BuildContext(BuildModel(), provider);

        // The registration itself must not decide: four hooks ARE registered here.
        provider.GetRequiredService<BeforeCommitMutationHooks>().Should().NotBeNull();
        provider.GetRequiredService<InTransactionMutationHooks>().Should().NotBeNull();

        var built = await BuildAsync(ctx);

        built.Should().NotBeNull("a table no registered hook acts on may take the set-based path");
        built!.Plan.Rows.Should().HaveCount(1);
        built.Plan.TableDbName.Should().Be("Orders");
    }

    [Theory]
    [InlineData("history")]
    [InlineData("approval")]
    [InlineData("deferred")]
    [InlineData("cdc")]
    public async Task FastPath_UnderProductionDi_FallsBackForATableAHookActsOn(string module)
    {
        await using var provider = BuildProductionServices();
        var model = BuildModel(t =>
        {
            switch (module)
            {
                case "history":
                    t.WithMetadata(MetadataKeys.History.Enabled, MetadataKeys.History.AllOperations)
                        .WithMetadata(MetadataKeys.History.Table, "orders_history");
                    break;
                case "approval":
                    t.WithMetadata(MetadataKeys.Approval.Marker, MetadataKeys.Approval.Enabled)
                        .WithMetadata(MetadataKeys.Approval.ApproverRole, "approver");
                    break;
                case "deferred":
                    t.WithMetadata(MetadataKeys.Deferred.Deferrable, MetadataKeys.Deferred.Enabled)
                        .WithMetadata(MetadataKeys.Deferred.UndoWindow, "12h");
                    break;
                case "cdc":
                    t.WithMetadata(MetadataKeys.Cdc.EmitEvents, "insert,update,delete");
                    break;
            }
        });
        var ctx = BuildContext(model, provider);

        BulkBatchPlanBuilder.IsEligible(
            model.GetTableFromDbName("Orders"),
            new[] { new BatchAction(MutationAction.Insert, new Dictionary<string, object?> { ["Status"] = "new" }) },
            ctx, out var reason).Should().BeFalse();
        reason.Should().Contain("hooks");
        (await BuildAsync(ctx)).Should().BeNull();
    }
}
