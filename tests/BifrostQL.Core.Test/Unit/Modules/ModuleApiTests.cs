using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Tests for the module API surface pattern: modules declare GraphQL arguments
/// (ModuleApiRegistry), schema generation emits them, and the soft-delete
/// transformers honor the captured values — _includeDeleted/_onlyDeleted on
/// queries and _hardDelete (optionally role-gated) on delete mutations.
/// </summary>
public class ModuleApiTests
{
    #region SDL emission

    [Fact]
    public void QueryArgumentsSdl_SoftDeleteTable_EmitsIncludeAndOnlyDeleted()
    {
        var table = SoftDeleteTable();

        var sdl = ModuleApiRegistry.QueryArgumentsSdl(table);

        sdl.Should().Be(" _includeDeleted: Boolean _onlyDeleted: Boolean");
    }

    [Fact]
    public void QueryArgumentsSdl_PlainTable_EmitsNothing()
    {
        var table = PlainTable();

        ModuleApiRegistry.QueryArgumentsSdl(table).Should().BeEmpty();
    }

    [Fact]
    public void MutationArgumentsSdl_SoftDeleteTable_EmitsHardDelete()
    {
        var table = SoftDeleteTable();

        ModuleApiRegistry.MutationArgumentsSdl(table).Should().Be(", _hardDelete: Boolean");
    }

    [Fact]
    public void MutationArgumentsSdl_PlainTable_EmitsNothing()
    {
        var table = PlainTable();

        ModuleApiRegistry.MutationArgumentsSdl(table).Should().BeEmpty();
    }

    #endregion

    #region Flag resolution

    [Fact]
    public void GetFlag_TableScopedKey_IsHonored()
    {
        var table = SoftDeleteTable();
        var userContext = new Dictionary<string, object?>
        {
            [ModuleApiRegistry.ScopedKey(SoftDeleteModuleApi.IncludeDeletedKey, table)] = true,
        };

        ModuleApiRegistry.GetFlag(userContext, SoftDeleteModuleApi.IncludeDeletedKey, table)
            .Should().BeTrue();
    }

    [Fact]
    public void GetFlag_GlobalKey_IsHonored()
    {
        var table = SoftDeleteTable();
        var userContext = new Dictionary<string, object?>
        {
            [SoftDeleteModuleApi.IncludeDeletedKey] = true,
        };

        ModuleApiRegistry.GetFlag(userContext, SoftDeleteModuleApi.IncludeDeletedKey, table)
            .Should().BeTrue();
    }

    [Fact]
    public void GetFlag_Absent_IsFalse()
    {
        var table = SoftDeleteTable();

        ModuleApiRegistry.GetFlag(new Dictionary<string, object?>(), SoftDeleteModuleApi.IncludeDeletedKey, table)
            .Should().BeFalse();
    }

    #endregion

    #region _onlyDeleted query filter

    [Fact]
    public void OnlyDeleted_ProducesIsNotNullFilter()
    {
        var table = SoftDeleteTable();
        var transformer = new SoftDeleteFilterTransformer();
        var context = QueryContext(table, new Dictionary<string, object?>
        {
            [ModuleApiRegistry.ScopedKey(SoftDeleteModuleApi.OnlyDeletedKey, table)] = true,
        });

        transformer.AppliesTo(table, context).Should().BeTrue("_onlyDeleted still requires a filter");
        var filter = transformer.GetAdditionalFilter(table, context);

        filter.Should().NotBeNull();
        filter!.ColumnName.Should().Be("deleted_at");
        filter.Next!.RelationName.Should().Be("_neq", "only-deleted means deleted_at IS NOT NULL");
        filter.Next.Value.Should().BeNull();
    }

    [Fact]
    public void OnlyDeleted_WinsOverIncludeDeleted()
    {
        var table = SoftDeleteTable();
        var transformer = new SoftDeleteFilterTransformer();
        var context = QueryContext(table, new Dictionary<string, object?>
        {
            [SoftDeleteModuleApi.IncludeDeletedKey] = true,
            [SoftDeleteModuleApi.OnlyDeletedKey] = true,
        });

        transformer.AppliesTo(table, context).Should().BeTrue();
        var filter = transformer.GetAdditionalFilter(table, context);
        filter!.Next!.RelationName.Should().Be("_neq");
    }

    [Fact]
    public void IncludeDeleted_ScopedKey_RemovesFilter()
    {
        var table = SoftDeleteTable();
        var transformer = new SoftDeleteFilterTransformer();
        var context = QueryContext(table, new Dictionary<string, object?>
        {
            [ModuleApiRegistry.ScopedKey(SoftDeleteModuleApi.IncludeDeletedKey, table)] = true,
        });

        transformer.AppliesTo(table, context).Should().BeFalse();
    }

    #endregion

    #region _hardDelete mutation

    [Fact]
    public void HardDelete_BypassesSoftDeleteRewrite()
    {
        var table = SoftDeleteTable();
        var transformer = new SoftDeleteMutationTransformer();
        var context = MutationContext(table, hardDelete: true);

        var data = new Dictionary<string, object?> { ["Id"] = 1 };
        var result = transformer.Transform(table, MutationType.Delete, data, context);

        result.Errors.Should().BeEmpty();
        result.MutationType.Should().Be(MutationType.Delete, "hard delete must stay a real DELETE");
        result.Data.Should().NotContainKey("deleted_at");
        result.AdditionalFilter.Should().BeNull(
            "hard delete must be able to purge rows that are already soft-deleted");
    }

    [Fact]
    public void HardDelete_WithoutFlag_StillSoftDeletes()
    {
        var table = SoftDeleteTable();
        var transformer = new SoftDeleteMutationTransformer();
        var context = MutationContext(table, hardDelete: false);

        var data = new Dictionary<string, object?> { ["Id"] = 1 };
        var result = transformer.Transform(table, MutationType.Delete, data, context);

        result.MutationType.Should().Be(MutationType.Update);
        result.Data.Should().ContainKey("deleted_at");
    }

    [Fact]
    public void HardDelete_RoleGate_DeniesWithoutRole()
    {
        var table = SoftDeleteTable(hardDeleteRole: "admin");
        var transformer = new SoftDeleteMutationTransformer();
        var context = MutationContext(table, hardDelete: true);

        var result = transformer.Transform(table, MutationType.Delete, new() { ["Id"] = 1 }, context);

        result.Errors.Should().ContainSingle(e => e.Contains("requires role 'admin'"));
    }

    [Fact]
    public void HardDelete_RoleGate_AllowsWithRole_CaseInsensitive()
    {
        var table = SoftDeleteTable(hardDeleteRole: "admin");
        var transformer = new SoftDeleteMutationTransformer();
        var context = MutationContext(table, hardDelete: true,
            userContext: new Dictionary<string, object?> { ["roles"] = new[] { "Admin" } });

        var result = transformer.Transform(table, MutationType.Delete, new() { ["Id"] = 1 }, context);

        result.Errors.Should().BeEmpty();
        result.MutationType.Should().Be(MutationType.Delete);
    }

    #endregion

    #region Helpers

    private static IDbTable SoftDeleteTable(string? hardDeleteRole = null)
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t =>
            {
                t.WithSchema("dbo")
                    .WithPrimaryKey("Id")
                    .WithColumn("Name", "varchar")
                    .WithColumn("deleted_at", "datetime", isNullable: true)
                    .WithMetadata("soft-delete", "deleted_at");
                if (hardDeleteRole != null)
                    t.WithMetadata("soft-delete-hard-role", hardDeleteRole);
            })
            .Build();
        return model.GetTableFromDbName("Users");
    }

    private static IDbTable PlainTable()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Plain", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar"))
            .Build();
        return model.GetTableFromDbName("Plain");
    }

    private static QueryTransformContext QueryContext(IDbTable table, IDictionary<string, object?> userContext)
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema(table.TableSchema)
                .WithPrimaryKey("Id")
                .WithColumn("deleted_at", "datetime", isNullable: true)
                .WithMetadata("soft-delete", "deleted_at"))
            .Build();
        return new QueryTransformContext
        {
            Model = model,
            UserContext = userContext,
            QueryType = QueryType.Standard,
            Path = "users",
        };
    }

    private static MutationTransformContext MutationContext(
        IDbTable table,
        bool hardDelete,
        IDictionary<string, object?>? userContext = null)
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema(table.TableSchema)
                .WithPrimaryKey("Id")
                .WithColumn("deleted_at", "datetime", isNullable: true)
                .WithMetadata("soft-delete", "deleted_at"))
            .Build();
        return new MutationTransformContext
        {
            Model = model,
            UserContext = userContext ?? new Dictionary<string, object?>(),
            ModuleArguments = hardDelete
                ? new Dictionary<string, object?> { [SoftDeleteModuleApi.HardDeleteKey] = true }
                : ModuleApiRegistry.EmptyArguments,
        };
    }

    #endregion
}
