using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using GraphQL;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

public class FilterTransformerTests
{
    #region TenantFilterTransformer Tests

    [Fact]
    public void TenantFilterTransformer_AppliesTo_ReturnsTrueWhenMetadataPresent()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("Total", "decimal")
                .WithMetadata("tenant-filter", "tenant_id"))
            .Build();

        var transformer = new TenantFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateTransformContext(model);

        Assert.True(transformer.AppliesTo(table, context));
    }

    [Fact]
    public void TenantFilterTransformer_AppliesTo_ReturnsFalseWhenNoMetadata()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var transformer = new TenantFilterTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateTransformContext(model);

        Assert.False(transformer.AppliesTo(table, context));
    }

    [Fact]
    public void TenantFilterTransformer_GetAdditionalFilter_CreatesCorrectFilter()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("Total", "decimal")
                .WithMetadata("tenant-filter", "tenant_id"))
            .Build();

        var transformer = new TenantFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateTransformContext(model, new Dictionary<string, object?>
        {
            ["tenant_id"] = 42
        });

        var filter = transformer.GetAdditionalFilter(table, context);

        Assert.NotNull(filter);
        Assert.Equal("Orders", filter!.TableName);
        Assert.Equal("tenant_id", filter.ColumnName);
        Assert.NotNull(filter.Next);
        Assert.Equal("_eq", filter.Next!.RelationName);
        Assert.Equal(42, filter.Next.Value);
    }

    [Fact]
    public void TenantFilterTransformer_GetAdditionalFilter_ThrowsWhenTenantIdMissing()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithMetadata("tenant-filter", "tenant_id"))
            .Build();

        var transformer = new TenantFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateTransformContext(model); // No tenant_id in context

        var ex = Assert.Throws<ExecutionError>(() => transformer.GetAdditionalFilter(table, context));
        Assert.Contains("Tenant context required", ex.Message);
    }

    [Fact]
    public void TenantFilterTransformer_GetAdditionalFilter_ThrowsWhenColumnNotFound()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata("tenant-filter", "nonexistent_column"))
            .Build();

        var transformer = new TenantFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateTransformContext(model, new Dictionary<string, object?>
        {
            ["tenant_id"] = 42
        });

        var ex = Assert.Throws<ExecutionError>(() => transformer.GetAdditionalFilter(table, context));
        Assert.Contains("not found in table", ex.Message);
    }

    #endregion

    #region SoftDeleteFilterTransformer Tests

    [Fact]
    public void SoftDeleteFilterTransformer_AppliesTo_ReturnsTrueWhenMetadataPresent()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithMetadata("soft-delete", "deleted_at"))
            .Build();

        var transformer = new SoftDeleteFilterTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateTransformContext(model);

        Assert.True(transformer.AppliesTo(table, context));
    }

    [Fact]
    public void SoftDeleteFilterTransformer_AppliesTo_ReturnsFalseWhenIncludeDeletedSet()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithMetadata("soft-delete", "deleted_at"))
            .Build();

        var transformer = new SoftDeleteFilterTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateTransformContext(model, new Dictionary<string, object?>
        {
            ["include_deleted"] = true
        });

        Assert.False(transformer.AppliesTo(table, context));
    }

    [Fact]
    public void SoftDeleteFilterTransformer_AppliesTo_ReturnsFalseWhenTableSpecificIncludeDeletedSet()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithMetadata("soft-delete", "deleted_at"))
            .Build();

        var transformer = new SoftDeleteFilterTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateTransformContext(model, new Dictionary<string, object?>
        {
            ["include_deleted:dbo.Users"] = true
        });

        Assert.False(transformer.AppliesTo(table, context));
    }

    [Fact]
    public void SoftDeleteFilterTransformer_GetAdditionalFilter_CreatesIsNullFilter()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithMetadata("soft-delete", "deleted_at"))
            .Build();

        var transformer = new SoftDeleteFilterTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateTransformContext(model);

        var filter = transformer.GetAdditionalFilter(table, context);

        Assert.NotNull(filter);
        Assert.Equal("Users", filter!.TableName);
        Assert.Equal("deleted_at", filter.ColumnName);
        Assert.NotNull(filter.Next);
        Assert.Equal("_eq", filter.Next!.RelationName);
        Assert.Null(filter.Next.Value); // IS NULL check
    }

    #endregion

    #region FilterTransformersWrap Tests

    [Fact]
    public void FilterTransformersWrap_GetCombinedFilter_CombinesMultipleFilters()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithMetadata("tenant-filter", "tenant_id")
                .WithMetadata("soft-delete", "deleted_at"))
            .Build();

        var transformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new TenantFilterTransformer(),
                new SoftDeleteFilterTransformer()
            }
        };

        var table = model.GetTableFromDbName("Orders");
        var context = CreateTransformContext(model, new Dictionary<string, object?>
        {
            ["tenant_id"] = 42
        });

        var filter = transformers.GetCombinedFilter(table, context);

        Assert.NotNull(filter);
        Assert.Equal(FilterType.And, filter!.FilterType);
        Assert.Equal(2, filter.And.Count);
    }

    [Fact]
    public void FilterTransformersWrap_GetCombinedFilter_ReturnsNullWhenNoTransformersApply()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var transformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new TenantFilterTransformer(),
                new SoftDeleteFilterTransformer()
            }
        };

        var table = model.GetTableFromDbName("Users");
        var context = CreateTransformContext(model);

        var filter = transformers.GetCombinedFilter(table, context);

        Assert.Null(filter);
    }

    [Fact]
    public void FilterTransformersWrap_GetCombinedFilter_AppliesInPriorityOrder()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithMetadata("tenant-filter", "tenant_id")
                .WithMetadata("soft-delete", "deleted_at"))
            .Build();

        // Add in reverse order - should still apply in priority order
        var transformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new SoftDeleteFilterTransformer(), // Priority 100
                new TenantFilterTransformer()      // Priority 0
            }
        };

        var table = model.GetTableFromDbName("Orders");
        var context = CreateTransformContext(model, new Dictionary<string, object?>
        {
            ["tenant_id"] = 42
        });

        var filter = transformers.GetCombinedFilter(table, context);

        // The first filter in the AND list should be tenant (lower priority = first)
        Assert.NotNull(filter);
        var firstFilter = filter!.And[0];
        Assert.Equal("tenant_id", firstFilter.ColumnName);
    }

    #endregion

    #region QueryTransformerService Tests

    [Fact]
    public void QueryTransformerService_ApplyTransformers_ModifiesQueryFilter()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("Total", "decimal")
                .WithMetadata("tenant-filter", "tenant_id"))
            .Build();

        var transformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new TenantFilterTransformer() }
        };
        var service = new QueryTransformerService(transformers);

        var query = new GqlObjectQuery
        {
            DbTable = model.GetTableFromDbName("Orders"),
            TableName = "Orders",
            SchemaName = "dbo",
            GraphQlName = "Orders",
            Filter = null // No existing filter
        };

        var userContext = new Dictionary<string, object?> { ["tenant_id"] = 42 };
        service.ApplyTransformers(query, model, userContext);

        Assert.NotNull(query.Filter);
        Assert.Equal("tenant_id", query.Filter!.ColumnName);
    }

    [Fact]
    public void QueryTransformerService_ApplyTransformers_CombinesWithExistingFilter()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("Status", "nvarchar")
                .WithMetadata("tenant-filter", "tenant_id"))
            .Build();

        var transformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new TenantFilterTransformer() }
        };
        var service = new QueryTransformerService(transformers);

        // Existing filter: Status = 'Active'
        var existingFilter = TableFilterFactory.Equals("Orders", "Status", "Active");

        var query = new GqlObjectQuery
        {
            DbTable = model.GetTableFromDbName("Orders"),
            TableName = "Orders",
            SchemaName = "dbo",
            GraphQlName = "Orders",
            Filter = existingFilter
        };

        var userContext = new Dictionary<string, object?> { ["tenant_id"] = 42 };
        service.ApplyTransformers(query, model, userContext);

        // Should combine existing + tenant filter
        Assert.NotNull(query.Filter);
        Assert.Equal(FilterType.And, query.Filter!.FilterType);
        Assert.Equal(2, query.Filter.And.Count);
    }

    #endregion

    #region Helper Methods

    private static QueryTransformContext CreateTransformContext(
        IDbModel model,
        IDictionary<string, object?>? userContext = null)
    {
        return new QueryTransformContext
        {
            Model = model,
            UserContext = userContext ?? new Dictionary<string, object?>(),
            QueryType = QueryType.Standard,
            Path = "",
            IsNestedQuery = false
        };
    }

    #endregion
}
