using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using GraphQL;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

public class AutoFilterTransformerTests
{
    #region AppliesTo Tests

    [Fact]
    public void AppliesTo_ReturnsTrueWhenAutoFilterMetadataPresent()
    {
        var model = CreateModelWithAutoFilter("org_id:organization_id");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model);

        Assert.True(transformer.AppliesTo(table, context));
    }

    [Fact]
    public void AppliesTo_ReturnsFalseWhenNoMetadata()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = CreateContext(model);

        Assert.False(transformer.AppliesTo(table, context));
    }

    [Fact]
    public void AppliesTo_ReturnsFalseWhenMetadataIsEmpty()
    {
        var model = CreateModelWithAutoFilter("");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model);

        Assert.False(transformer.AppliesTo(table, context));
    }

    [Fact]
    public void AppliesTo_ReturnsFalseWhenUserHasBypassRole()
    {
        var model = CreateModelWithAutoFilterAndBypass("org_id:organization_id", "admin");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["roles"] = new[] { "admin", "user" }
        });

        Assert.False(transformer.AppliesTo(table, context));
    }

    [Fact]
    public void AppliesTo_ReturnsTrueWhenUserLacksBypassRole()
    {
        var model = CreateModelWithAutoFilterAndBypass("org_id:organization_id", "admin");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["roles"] = new[] { "user", "viewer" }
        });

        Assert.True(transformer.AppliesTo(table, context));
    }

    [Fact]
    public void AppliesTo_ReturnsTrueWhenNoBypassRoleConfigured()
    {
        var model = CreateModelWithAutoFilter("org_id:organization_id");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["roles"] = new[] { "admin" }
        });

        Assert.True(transformer.AppliesTo(table, context));
    }

    [Fact]
    public void AppliesTo_BypassRole_WorksWithSingleStringRole()
    {
        var model = CreateModelWithAutoFilterAndBypass("org_id:organization_id", "admin");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["roles"] = "admin"
        });

        Assert.False(transformer.AppliesTo(table, context));
    }

    [Fact]
    public void AppliesTo_BypassRole_IsCaseInsensitive()
    {
        var model = CreateModelWithAutoFilterAndBypass("org_id:organization_id", "Admin");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["roles"] = "admin"
        });

        Assert.False(transformer.AppliesTo(table, context));
    }

    #endregion

    #region GetAdditionalFilter - Single Mapping Tests

    [Fact]
    public void GetAdditionalFilter_SingleMapping_CreatesEqualityFilter()
    {
        var model = CreateModelWithAutoFilter("org_id:organization_id");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["organization_id"] = 42
        });

        var filter = transformer.GetAdditionalFilter(table, context);

        Assert.NotNull(filter);
        Assert.Equal("Orders", filter!.TableName);
        Assert.Equal("org_id", filter.ColumnName);
        Assert.NotNull(filter.Next);
        Assert.Equal("_eq", filter.Next!.RelationName);
        Assert.Equal(42, filter.Next.Value);
    }

    [Fact]
    public void GetAdditionalFilter_ThrowsWhenClaimMissing()
    {
        var model = CreateModelWithAutoFilter("org_id:organization_id");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model);

        var ex = Assert.Throws<ExecutionError>(() => transformer.GetAdditionalFilter(table, context));
        Assert.Contains("organization_id", ex.Message);
        Assert.Contains("required but not found", ex.Message);
    }

    [Fact]
    public void GetAdditionalFilter_ThrowsWhenClaimIsNull()
    {
        var model = CreateModelWithAutoFilter("org_id:organization_id");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["organization_id"] = null
        });

        var ex = Assert.Throws<ExecutionError>(() => transformer.GetAdditionalFilter(table, context));
        Assert.Contains("cannot be null", ex.Message);
    }

    [Fact]
    public void GetAdditionalFilter_ThrowsWhenColumnNotFound()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata("auto-filter", "nonexistent_col:some_claim"))
            .Build();

        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["some_claim"] = 1
        });

        var ex = Assert.Throws<ExecutionError>(() => transformer.GetAdditionalFilter(table, context));
        Assert.Contains("not found in table", ex.Message);
    }

    #endregion

    #region GetAdditionalFilter - Multiple Mappings Tests

    [Fact]
    public void GetAdditionalFilter_MultipleMappings_CombinesWithAnd()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("org_id", "int")
                .WithColumn("region_id", "int")
                .WithColumn("Total", "decimal")
                .WithMetadata("auto-filter", "org_id:organization_id,region_id:user_region"))
            .Build();

        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["organization_id"] = 42,
            ["user_region"] = 7
        });

        var filter = transformer.GetAdditionalFilter(table, context);

        Assert.NotNull(filter);
        Assert.Equal(FilterType.And, filter!.FilterType);
        Assert.Equal(2, filter.And.Count);

        // First filter: org_id = 42
        var first = filter.And[0];
        Assert.Equal("org_id", first.ColumnName);
        Assert.Equal(42, first.Next!.Value);

        // Second filter: region_id = 7
        var second = filter.And[1];
        Assert.Equal("region_id", second.ColumnName);
        Assert.Equal(7, second.Next!.Value);
    }

    [Fact]
    public void GetAdditionalFilter_MultipleMappings_ThrowsIfAnyClaimMissing()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("org_id", "int")
                .WithColumn("region_id", "int")
                .WithMetadata("auto-filter", "org_id:organization_id,region_id:user_region"))
            .Build();

        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["organization_id"] = 42
            // Missing user_region
        });

        var ex = Assert.Throws<ExecutionError>(() => transformer.GetAdditionalFilter(table, context));
        Assert.Contains("user_region", ex.Message);
    }

    #endregion

    #region GetAdditionalFilter - Array Claim Tests

    [Fact]
    public void GetAdditionalFilter_ArrayClaim_CreatesInFilter()
    {
        var model = CreateModelWithAutoFilter("org_id:organization_ids");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["organization_ids"] = new object[] { 1, 2, 3 }
        });

        var filter = transformer.GetAdditionalFilter(table, context);

        Assert.NotNull(filter);
        Assert.Equal("Orders", filter!.TableName);
        Assert.Equal("org_id", filter.ColumnName);
        Assert.NotNull(filter.Next);
        Assert.Equal("_in", filter.Next!.RelationName);
    }

    [Fact]
    public void GetAdditionalFilter_ArrayClaim_ThrowsWhenEmpty()
    {
        var model = CreateModelWithAutoFilter("org_id:organization_ids");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["organization_ids"] = Array.Empty<object>()
        });

        var ex = Assert.Throws<ExecutionError>(() => transformer.GetAdditionalFilter(table, context));
        Assert.Contains("cannot be empty", ex.Message);
    }

    [Fact]
    public void GetAdditionalFilter_ListClaim_CreatesInFilter()
    {
        var model = CreateModelWithAutoFilter("org_id:organization_ids");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["organization_ids"] = new List<object> { 10, 20 }
        });

        var filter = transformer.GetAdditionalFilter(table, context);

        Assert.NotNull(filter);
        Assert.Equal("_in", filter!.Next!.RelationName);
    }

    [Fact]
    public void GetAdditionalFilter_StringClaim_CreatesEqualityNotIn()
    {
        var model = CreateModelWithAutoFilter("org_id:organization_id");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["organization_id"] = "acme-corp"
        });

        var filter = transformer.GetAdditionalFilter(table, context);

        Assert.NotNull(filter);
        Assert.Equal("_eq", filter!.Next!.RelationName);
        Assert.Equal("acme-corp", filter.Next.Value);
    }

    #endregion

    #region All Query Types Tests

    [Theory]
    [InlineData(QueryType.Standard)]
    [InlineData(QueryType.Join)]
    [InlineData(QueryType.Single)]
    [InlineData(QueryType.Aggregate)]
    public void GetAdditionalFilter_AppliesForAllQueryTypes(QueryType queryType)
    {
        var model = CreateModelWithAutoFilter("org_id:organization_id");
        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = new Dictionary<string, object?> { ["organization_id"] = 42 },
            QueryType = queryType,
            Path = "",
            IsNestedQuery = false
        };

        var filter = transformer.GetAdditionalFilter(table, context);

        Assert.NotNull(filter);
        Assert.Equal("org_id", filter!.ColumnName);
    }

    #endregion

    #region Parsing Validation Tests (via public API)

    [Fact]
    public void GetAdditionalFilter_WhitespaceInMapping_StillWorks()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("org_id", "int")
                .WithColumn("region_id", "int")
                .WithMetadata("auto-filter", " org_id : organization_id , region_id : user_region "))
            .Build();

        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["organization_id"] = 1,
            ["user_region"] = 2
        });

        var filter = transformer.GetAdditionalFilter(table, context);

        Assert.NotNull(filter);
        Assert.Equal(FilterType.And, filter!.FilterType);
        Assert.Equal(2, filter.And.Count);
    }

    [Fact]
    public void GetAdditionalFilter_InvalidFormat_NoColon_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("org_id", "int")
                .WithMetadata("auto-filter", "org_id_only"))
            .Build();

        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["org_id_only"] = 1
        });

        var ex = Assert.Throws<ExecutionError>(() => transformer.GetAdditionalFilter(table, context));
        Assert.Contains("Expected format", ex.Message);
    }

    [Fact]
    public void GetAdditionalFilter_InvalidFormat_EmptyColumn_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("org_id", "int")
                .WithMetadata("auto-filter", ":claim"))
            .Build();

        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?> { ["claim"] = 1 });

        var ex = Assert.Throws<ExecutionError>(() => transformer.GetAdditionalFilter(table, context));
        Assert.Contains("Expected format", ex.Message);
    }

    [Fact]
    public void GetAdditionalFilter_InvalidFormat_EmptyClaim_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("org_id", "int")
                .WithMetadata("auto-filter", "column:"))
            .Build();

        var transformer = new AutoFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model);

        var ex = Assert.Throws<ExecutionError>(() => transformer.GetAdditionalFilter(table, context));
        Assert.Contains("Expected format", ex.Message);
    }

    #endregion

    #region Integration with FilterTransformersWrap

    [Fact]
    public void Integration_WorksAlongsideTenantFilter()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("org_id", "int")
                .WithColumn("Total", "decimal")
                .WithMetadata("tenant-filter", "tenant_id")
                .WithMetadata("auto-filter", "org_id:organization_id"))
            .Build();

        var transformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new TenantFilterTransformer(),
                new AutoFilterTransformer()
            }
        };

        var table = model.GetTableFromDbName("Orders");
        var context = CreateContext(model, new Dictionary<string, object?>
        {
            ["tenant_id"] = 1,
            ["organization_id"] = 42
        });

        var filter = transformers.GetCombinedFilter(table, context);

        Assert.NotNull(filter);
        Assert.Equal(FilterType.And, filter!.FilterType);
        Assert.Equal(2, filter.And.Count);

        // Tenant filter (priority 0) first, auto-filter (priority 1) second
        Assert.Equal("tenant_id", filter.And[0].ColumnName);
        Assert.Equal("org_id", filter.And[1].ColumnName);
    }

    [Fact]
    public void Integration_QueryTransformerService_AppliesAutoFilter()
    {
        var model = CreateModelWithAutoFilter("org_id:organization_id");
        var transformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new AutoFilterTransformer() }
        };
        var service = new QueryTransformerService(transformers);

        var query = new GqlObjectQuery
        {
            DbTable = model.GetTableFromDbName("Orders"),
            TableName = "Orders",
            SchemaName = "dbo",
            GraphQlName = "Orders",
            Filter = null
        };

        var userContext = new Dictionary<string, object?> { ["organization_id"] = 42 };
        service.ApplyTransformers(query, model, userContext);

        Assert.NotNull(query.Filter);
        Assert.Equal("org_id", query.Filter!.ColumnName);
    }

    [Fact]
    public void Integration_QueryTransformerService_CombinesWithExistingFilter()
    {
        var model = CreateModelWithAutoFilter("org_id:organization_id");
        var transformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new AutoFilterTransformer() }
        };
        var service = new QueryTransformerService(transformers);

        var existingFilter = TableFilterFactory.Equals("Orders", "Total", 100);
        var query = new GqlObjectQuery
        {
            DbTable = model.GetTableFromDbName("Orders"),
            TableName = "Orders",
            SchemaName = "dbo",
            GraphQlName = "Orders",
            Filter = existingFilter
        };

        var userContext = new Dictionary<string, object?> { ["organization_id"] = 42 };
        service.ApplyTransformers(query, model, userContext);

        Assert.NotNull(query.Filter);
        Assert.Equal(FilterType.And, query.Filter!.FilterType);
        Assert.Equal(2, query.Filter.And.Count);
    }

    #endregion

    #region Priority Tests

    [Fact]
    public void Priority_IsInSecurityRange()
    {
        var transformer = new AutoFilterTransformer();
        Assert.InRange(transformer.Priority, 0, 99);
    }

    [Fact]
    public void Priority_IsAfterTenantFilter()
    {
        var autoFilter = new AutoFilterTransformer();
        var tenantFilter = new TenantFilterTransformer();
        Assert.True(autoFilter.Priority > tenantFilter.Priority);
    }

    #endregion

    #region Helper Methods

    private static IDbModel CreateModelWithAutoFilter(string mappingStr)
    {
        return DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("org_id", "int")
                .WithColumn("Total", "decimal")
                .WithMetadata("auto-filter", mappingStr))
            .Build();
    }

    private static IDbModel CreateModelWithAutoFilterAndBypass(string mappingStr, string bypassRole)
    {
        return DbModelTestFixture.Create()
            .WithModelMetadata("auto-filter-bypass-role", bypassRole)
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("org_id", "int")
                .WithColumn("Total", "decimal")
                .WithMetadata("auto-filter", mappingStr))
            .Build();
    }

    private static QueryTransformContext CreateContext(
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
