using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
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
    public void SoftDeleteMutationTransformer_Transform_ConvertsDeleteToUpdate()
    {
        var model = CreateSoftDeleteModel();
        var transformer = new SoftDeleteMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);

        var data = new Dictionary<string, object?>
        {
            ["Id"] = 1
        };

        var result = transformer.Transform(table, MutationType.Delete, data, context);

        Assert.Equal(MutationType.Update, result.MutationType);
        Assert.Contains("deleted_at", result.Data.Keys);
        Assert.IsType<DateTimeOffset>(result.Data["deleted_at"]);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SoftDeleteMutationTransformer_Transform_AddsDeletedByWhenConfigured()
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
        var result = transformer.Transform(table, MutationType.Delete, data, context);

        Assert.Equal(MutationType.Update, result.MutationType);
        Assert.Equal(99, result.Data["deleted_by"]);
    }

    [Fact]
    public void SoftDeleteMutationTransformer_Transform_AddsFilterForUpdate()
    {
        var model = CreateSoftDeleteModel();
        var transformer = new SoftDeleteMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);

        var data = new Dictionary<string, object?>
        {
            ["Name"] = "Updated Name"
        };

        var result = transformer.Transform(table, MutationType.Update, data, context);

        Assert.Equal(MutationType.Update, result.MutationType);
        Assert.NotNull(result.AdditionalFilter);
        Assert.Equal("deleted_at", result.AdditionalFilter!.ColumnName);
    }

    [Fact]
    public void SoftDeleteMutationTransformer_Transform_AddsFilterForDelete()
    {
        var model = CreateSoftDeleteModel();
        var transformer = new SoftDeleteMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateMutationContext(model);

        var data = new Dictionary<string, object?> { ["Id"] = 1 };
        var result = transformer.Transform(table, MutationType.Delete, data, context);

        // Should have filter to only soft-delete non-deleted records
        Assert.NotNull(result.AdditionalFilter);
        Assert.Equal("deleted_at", result.AdditionalFilter!.ColumnName);
    }

    [Fact]
    public void SoftDeleteMutationTransformer_Transform_ReturnsErrorWhenColumnNotFound()
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
        var result = transformer.Transform(table, MutationType.Delete, data, context);

        Assert.Single(result.Errors);
        Assert.Contains("not found in table", result.Errors[0]);
    }

    #endregion

    #region MutationTransformersWrap Tests

    [Fact]
    public void MutationTransformersWrap_Transform_ChainsTransformers()
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

        var result = transformers.Transform(table, MutationType.Delete, data, context);

        Assert.Equal(MutationType.Update, result.MutationType);
        Assert.Contains("deleted_at", result.Data.Keys);
    }

    [Fact]
    public void MutationTransformersWrap_Transform_AccumulatesErrors()
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

        var result = transformers.Transform(table, MutationType.Delete, data, context);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void MutationTransformersWrap_Transform_ReturnsUnchangedWhenNoTransformersApply()
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

        var result = transformers.Transform(table, MutationType.Delete, data, context);

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
}
