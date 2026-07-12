using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Sqlite;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Defense-in-depth for the trail: a history target takes no client writes on ANY
/// seam. The generated schema already emits no batch/mutation field for a target
/// (so these paths are unreachable through GraphQL today), but the batch resolver
/// and the TreeSync executor are invocable directly — a hand-wired resolver, an
/// adapter, or a future entry point must hit the same typed access-denied wall the
/// single-row pipeline raises, not a working write path. And the multi-db schema
/// must not emit orphaned insert/update/upsert/delete/batch input types for a
/// target whose mutation field was never generated.
/// </summary>
public sealed class HistoryTargetSeamGuardTests
{
    private static IDbModel HistoryModel() => DbModelTestFixture.Create()
        .WithModelMetadata(MetadataKeys.History.Table, "audit_trail")
        .WithTable("orders", t => t
            .WithPrimaryKey("id")
            .WithColumn("status", "nvarchar")
            .WithMetadata(MetadataKeys.History.Enabled, MetadataKeys.History.AllOperations))
        .WithTable("audit_trail", t => t
            .WithPrimaryKey("id")
            .WithColumn("entity", "nvarchar"))
        .Build();

    [Fact]
    public async Task BatchResolver_TargetingAHistoryTable_IsRejectedAtExecutionTime()
    {
        var model = HistoryModel();
        var target = model.GetTableFromDbName("audit_trail");

        var context = Substitute.For<IBifrostFieldContext>();
        context.InputExtensions.Returns(new Dictionary<string, object?>
        {
            ["connFactory"] = new SqliteDbConnFactory("Data Source=:memory:"),
            ["model"] = model,
            ["tableReaderFactory"] = Substitute.For<ISqlExecutionManager>(),
        });
        context.UserContext.Returns(new Dictionary<string, object?>());
        context.GetArgument<List<Dictionary<string, object?>>>("actions").Returns(
            new List<Dictionary<string, object?>>
            {
                new() { ["insert"] = new Dictionary<string, object?> { ["entity"] = "forged" } },
            });

        var act = async () => await new DbTableBatchResolver(target).ResolveAsync(context);

        (await act.Should().ThrowAsync<BifrostExecutionError>())
            .Which.Should().Match<BifrostExecutionError>(e =>
                e.ErrorCode == BifrostExecutionError.AccessDeniedCode
                && e.Message.Contains("change-history table")
                && e.Message.Contains("not writable"));
    }

    [Fact]
    public async Task TreeSyncExecutor_TargetingAHistoryTable_IsRejectedBeforeAnyWrite()
    {
        var model = HistoryModel();
        var target = model.GetTableFromDbName("audit_trail");

        var executor = new TreeSyncExecutor(SqliteDialect.Instance);
        var operations = new[]
        {
            new TreeSyncOperation
            {
                Table = target,
                OperationType = TreeSyncOperationType.Insert,
                Depth = 0,
                Data = new Dictionary<string, object?> { ["entity"] = "forged" },
            },
        };

        var act = async () => await executor.ExecuteAsync(
            operations, new SqliteDbConnFactory("Data Source=:memory:"), model: model);

        (await act.Should().ThrowAsync<BifrostExecutionError>())
            .Which.Should().Match<BifrostExecutionError>(e =>
                e.ErrorCode == BifrostExecutionError.AccessDeniedCode
                && e.Message.Contains("change-history table")
                && e.Message.Contains("not writable"));
    }

    [Fact]
    public void MultiDbSchema_EmitsNoMutationInputTypes_ForAHistoryTarget()
    {
        var schema = MultiDbSchemaGenerator.GenerateSchema(new Dictionary<string, IDbModel>
        {
            ["mainDb"] = HistoryModel(),
        });

        // The target has no mutation/batch field, so these input types would be orphans.
        foreach (var orphan in new[]
                 {
                     "input Insert_audit_trail", "input Update_audit_trail",
                     "input Upsert_audit_trail", "input Delete_audit_trail",
                     "input batch_audit_trail",
                 })
            schema.Should().NotContain(orphan, "a history target's mutation field is never emitted");

        // The tracked table's own mutation inputs — and the target's filter/sort types,
        // which its trail read field references — stay.
        schema.Should().Contain("input Insert_orders");
        schema.Should().Contain("input batch_orders");
        schema.Should().Contain("input TableFilteraudit_trailInput");
    }
}
