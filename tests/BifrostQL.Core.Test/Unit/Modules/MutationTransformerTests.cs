using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

public class MutationTransformerTests
{
    #region SoftDeleteMutationTransformer Tests

    [Fact]
    public void SoftDeleteMutationTransformer_AppliesTo_ReturnsFalseForInsert()
    {
        var model = CreateSoftDeleteModel();
        var transformer = new SoftDeleteMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);

        Assert.False(transformer.AppliesTo(table, MutationType.Insert, context));
    }

    [Fact]
    public void SoftDeleteMutationTransformer_AppliesTo_ReturnsTrueForDelete()
    {
        var model = CreateSoftDeleteModel();
        var transformer = new SoftDeleteMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);

        Assert.True(transformer.AppliesTo(table, MutationType.Delete, context));
    }

    [Fact]
    public void SoftDeleteMutationTransformer_AppliesTo_ReturnsTrueForUpdate()
    {
        var model = CreateSoftDeleteModel();
        var transformer = new SoftDeleteMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);

        Assert.True(transformer.AppliesTo(table, MutationType.Update, context));
    }

    [Fact]
    public void SoftDeleteMutationTransformer_AppliesTo_ReturnsFalseWhenNoMetadata()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var transformer = new SoftDeleteMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);

        Assert.False(transformer.AppliesTo(table, MutationType.Delete, context));
    }

    [Fact]
    public async Task SoftDeleteMutationTransformer_Transform_ConvertsDeleteToUpdate()
    {
        var model = CreateSoftDeleteModel();
        var transformer = new SoftDeleteMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);

        var data = new Dictionary<string, object?>
        {
            ["Id"] = 1
        };

        var result = await transformer.TransformAsync(table, MutationType.Delete, data, context);

        Assert.Equal(MutationType.Update, result.MutationType);
        Assert.Contains("deleted_at", result.Data.Keys);
        Assert.IsType<DateTimeOffset>(result.Data["deleted_at"]);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task SoftDeleteMutationTransformer_Transform_AddsDeletedByWhenConfigured()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithColumn("deleted_by", "int", isNullable: true)
                .WithMetadata("soft-delete", "deleted_at")
                .WithMetadata("soft-delete-by", "deleted_by"))
            .Build();

        var transformer = new SoftDeleteMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model, new Dictionary<string, object?>
        {
            ["user_id"] = 99
        });

        var data = new Dictionary<string, object?> { ["Id"] = 1 };
        var result = await transformer.TransformAsync(table, MutationType.Delete, data, context);

        Assert.Equal(MutationType.Update, result.MutationType);
        Assert.Equal(99, result.Data["deleted_by"]);
    }

    [Fact]
    public async Task SoftDeleteMutationTransformer_Transform_AddsFilterForUpdate()
    {
        var model = CreateSoftDeleteModel();
        var transformer = new SoftDeleteMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);

        var data = new Dictionary<string, object?>
        {
            ["Name"] = "Updated Name"
        };

        var result = await transformer.TransformAsync(table, MutationType.Update, data, context);

        Assert.Equal(MutationType.Update, result.MutationType);
        Assert.NotNull(result.AdditionalFilter);
        Assert.Equal("deleted_at", result.AdditionalFilter!.ColumnName);
    }

    [Fact]
    public async Task SoftDeleteMutationTransformer_Transform_AddsFilterForDelete()
    {
        var model = CreateSoftDeleteModel();
        var transformer = new SoftDeleteMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);

        var data = new Dictionary<string, object?> { ["Id"] = 1 };
        var result = await transformer.TransformAsync(table, MutationType.Delete, data, context);

        // Should have filter to only soft-delete non-deleted records
        Assert.NotNull(result.AdditionalFilter);
        Assert.Equal("deleted_at", result.AdditionalFilter!.ColumnName);
    }

    [Fact]
    public async Task SoftDeleteMutationTransformer_Transform_ReturnsErrorWhenColumnNotFound()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithMetadata("soft-delete", "nonexistent_column"))
            .Build();

        var transformer = new SoftDeleteMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);

        var data = new Dictionary<string, object?> { ["Id"] = 1 };
        var result = await transformer.TransformAsync(table, MutationType.Delete, data, context);

        Assert.Single(result.Errors);
        Assert.Contains("not found in table", result.Errors[0]);
    }

    #endregion

    #region MutationTransformersWrap Tests

    [Fact]
    public async Task MutationTransformersWrap_Transform_ChainsTransformers()
    {
        var model = CreateSoftDeleteModel();
        var transformers = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new SoftDeleteMutationTransformer()
            }
        };

        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        var result = await transformers.TransformAsync(table, MutationType.Delete, data, context);

        Assert.Equal(MutationType.Update, result.MutationType);
        Assert.Contains("deleted_at", result.Data.Keys);
    }

    [Fact]
    public async Task MutationTransformersWrap_Transform_AccumulatesErrors()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithMetadata("soft-delete", "nonexistent_column"))
            .Build();

        var transformers = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new SoftDeleteMutationTransformer()
            }
        };

        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        var result = await transformers.TransformAsync(table, MutationType.Delete, data, context);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task MutationTransformersWrap_Transform_ReturnsUnchangedWhenNoTransformersApply()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var transformers = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new SoftDeleteMutationTransformer()
            }
        };

        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Test" };

        var result = await transformers.TransformAsync(table, MutationType.Delete, data, context);

        Assert.Equal(MutationType.Delete, result.MutationType);
        Assert.Equal(data, result.Data);
    }

    #endregion

    #region Helper Methods

    private static IDbModel CreateSoftDeleteModel()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithMetadata("soft-delete", "deleted_at"))
            .Build();
    }

    private static MutationTransformContext CreateMutationContext(
        IDbModel model,
        IDictionary<string, object?>? userContext = null)
    {
        return new MutationTransformContext
        {
            Model = model,
            UserContext = userContext ?? new Dictionary<string, object?>()
        };
    }

    #endregion

    #region TenantMutationTransformer Tests

    private static IDbModel CreateTenantModel()
    {
        return DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("tenant_id", "int")
                .WithMetadata("tenant-filter", "tenant_id"))
            .Build();
    }

    [Fact]
    public void TenantMutationTransformer_AppliesTo_FalseWhenNoMetadata()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var transformer = new TenantMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);

        Assert.False(transformer.AppliesTo(table, MutationType.Delete, context));
    }

    [Fact]
    public async Task TenantMutationTransformer_Insert_PinsTenantColumn_OverridingClientValue()
    {
        var model = CreateTenantModel();
        var transformer = new TenantMutationTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateMutationContext(model, new Dictionary<string, object?> { ["tenant_id"] = 7 });

        // Client tries to plant a row in another tenant.
        var data = new Dictionary<string, object?> { ["Name"] = "x", ["tenant_id"] = 999 };

        var result = await transformer.TransformAsync(table, MutationType.Insert, data, context);

        Assert.Equal(MutationType.Insert, result.MutationType);
        Assert.Equal(7, result.Data["tenant_id"]);
        Assert.Null(result.AdditionalFilter);
    }

    [Fact]
    public async Task TenantMutationTransformer_Delete_ScopesWhereToCallerTenant()
    {
        var model = CreateTenantModel();
        var transformer = new TenantMutationTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateMutationContext(model, new Dictionary<string, object?> { ["tenant_id"] = 7 });

        var result = await transformer.TransformAsync(table, MutationType.Delete,
            new Dictionary<string, object?> { ["Id"] = 1 }, context);

        Assert.NotNull(result.AdditionalFilter);
        Assert.Equal("tenant_id", result.AdditionalFilter!.ColumnName);
        Assert.Equal(7, result.AdditionalFilter.Next!.Value);
        Assert.Equal("_eq", result.AdditionalFilter.Next.RelationName);
    }

    [Fact]
    public async Task TenantMutationTransformer_Update_ScopesWhereAndPinsColumn()
    {
        var model = CreateTenantModel();
        var transformer = new TenantMutationTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateMutationContext(model, new Dictionary<string, object?> { ["tenant_id"] = 7 });

        // Client tries to reassign the row to another tenant.
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["tenant_id"] = 999 };

        var result = await transformer.TransformAsync(table, MutationType.Update, data, context);

        Assert.Equal(7, result.Data["tenant_id"]);
        Assert.NotNull(result.AdditionalFilter);
        Assert.Equal(7, result.AdditionalFilter!.Next!.Value);
    }

    [Fact]
    public async Task TenantMutationTransformer_MissingTenantContext_Throws()
    {
        var model = CreateTenantModel();
        var transformer = new TenantMutationTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateMutationContext(model); // no tenant_id in context

        await Assert.ThrowsAsync<BifrostExecutionError>(() =>
            transformer.TransformAsync(table, MutationType.Delete,
                new Dictionary<string, object?> { ["Id"] = 1 }, context).AsTask());
    }

    #endregion
}
