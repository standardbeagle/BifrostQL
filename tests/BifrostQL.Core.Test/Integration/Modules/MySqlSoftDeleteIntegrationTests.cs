using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.MySql;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// MySQL-specific soft delete integration tests.
/// Verifies SoftDeleteFilterTransformer and SoftDeleteMutationTransformer
/// produce correct MySQL SQL syntax (backtick identifiers, LIMIT/OFFSET pagination).
/// Mirrors SoftDeleteIntegrationTests but uses MySqlDialect instead of SqlServerDialect.
/// </summary>
public class MySqlSoftDeleteIntegrationTests
{
    private static readonly ISqlDialect Dialect = MySqlDialect.Instance;

    #region Filter + SQL Generation Integration

    [Fact]
    public void SoftDeleteFilter_GeneratesMySqlIsNullWhereClause()
    {
        var model = CreateSoftDeleteModel();
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name", "Email")
            .Build();

        service.ApplyTransformers(query, model, EmptyUserContext());

        var (sql, _) = GenerateSql(query, model);

        sql.Should().Contain("WHERE");
        sql.Should().Contain("`deleted_at`");
        sql.Should().Contain("IS NULL");
        sql.Should().NotContain("[deleted_at]", "MySQL uses backtick identifiers, not brackets");
        sql.Should().NotContain("\"deleted_at\"", "MySQL uses backtick identifiers, not double quotes");
    }

    [Fact]
    public void SoftDeleteFilter_CombinesWithUserFilter_MySqlSyntax()
    {
        var model = CreateSoftDeleteModel();
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var userFilter = TableFilterFactory.Equals("Users", "Name", "Alice");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .WithFilter(userFilter)
            .Build();

        service.ApplyTransformers(query, model, EmptyUserContext());

        var (sql, parameters) = GenerateSql(query, model);

        sql.Should().Contain("WHERE");
        sql.Should().Contain("`deleted_at`");
        sql.Should().Contain("IS NULL");
        sql.Should().Contain("`Name`");
        parameters.Parameters.Should().NotBeEmpty("user filter value should be parameterized");
    }

    [Fact]
    public void SoftDeleteFilter_WithTenantFilter_GeneratesBothFiltersInMySql()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("mydb")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("Total", "decimal")
                .WithColumn("deleted_at", "datetime", isNullable: true)
                .WithMetadata("tenant-filter", "tenant_id")
                .WithMetadata("soft-delete", "deleted_at"))
            .Build();

        var table = model.GetTableFromDbName("Orders");
        var service = CreateTransformerService(
            new TenantFilterTransformer(),
            new SoftDeleteFilterTransformer());

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Total")
            .Build();

        var userContext = new Dictionary<string, object?> { ["tenant_id"] = 42 };
        service.ApplyTransformers(query, model, userContext);

        var (sql, parameters) = GenerateSql(query, model);

        sql.Should().Contain("WHERE");
        sql.Should().Contain("`tenant_id`");
        sql.Should().Contain("`deleted_at`");
        sql.Should().Contain("IS NULL");
        sql.Should().NotContain("[", "MySQL must not use bracket identifiers");
        sql.Should().NotContain("\"tenant_id\"", "MySQL must not use double-quote identifiers");
        parameters.Parameters.Should().Contain(p => p.Value != null && p.Value.Equals(42),
            "tenant_id parameter should be bound");
    }

    [Fact]
    public void SoftDeleteFilter_IncludeDeleted_OmitsDeletedAtFilter()
    {
        var model = CreateSoftDeleteModel();
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .Build();

        var userContext = new Dictionary<string, object?> { ["include_deleted"] = true };
        service.ApplyTransformers(query, model, userContext);

        query.Filter.Should().BeNull("soft-delete filter should not apply when include_deleted is true");
    }

    [Fact]
    public void SoftDeleteFilter_IncludeDeletedPerTable_OmitsFilterForSpecificTable()
    {
        var model = CreateSoftDeleteModel();
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .Build();

        var userContext = new Dictionary<string, object?> { ["include_deleted:mydb.Users"] = true };
        service.ApplyTransformers(query, model, userContext);

        query.Filter.Should().BeNull("soft-delete filter should not apply when table-specific include_deleted is true");
    }

    [Fact]
    public void SoftDeleteFilter_IncludeDeletedForOtherTable_StillFilters()
    {
        var model = CreateSoftDeleteModel();
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .Build();

        var userContext = new Dictionary<string, object?> { ["include_deleted:mydb.Orders"] = true };
        service.ApplyTransformers(query, model, userContext);

        query.Filter.Should().NotBeNull("soft-delete filter should still apply for Users when include_deleted is set for Orders");
    }

    #endregion

    #region MySQL-Specific SQL Verification

    [Fact]
    public void MySqlSql_SoftDeleteFilter_UsesBacktickIdentifiers()
    {
        var model = CreateSoftDeleteModel();
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name", "Email", "deleted_at")
            .Build();

        service.ApplyTransformers(query, model, EmptyUserContext());

        var (sql, _) = GenerateSql(query, model);

        sql.Should().Contain("`Users`", "table name should use backticks");
        sql.Should().Contain("`Id`", "column identifiers should use backticks");
        sql.Should().Contain("`deleted_at`", "soft-delete column should use backticks");
        sql.Should().NotContain("[Users]", "should not use SQL Server bracket identifiers");
        sql.Should().NotContain("[Id]");
        sql.Should().NotContain("[deleted_at]");
        sql.Should().NotContain("\"Users\"", "should not use PostgreSQL double-quote identifiers");
        sql.Should().NotContain("\"Id\"");
        sql.Should().NotContain("\"deleted_at\"");
    }

    [Fact]
    public void MySqlSql_SoftDeleteFilter_UsesAtPrefixParameters()
    {
        var model = CreateSoftDeleteModel();
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var userFilter = TableFilterFactory.Equals("Users", "Name", "Test");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .WithFilter(userFilter)
            .Build();

        service.ApplyTransformers(query, model, EmptyUserContext());

        var (sql, parameters) = GenerateSql(query, model);

        parameters.Parameters.Should().NotBeEmpty();
        foreach (var param in parameters.Parameters)
        {
            param.Name.Should().StartWith("@", "MySQL parameters should use @ prefix");
            sql.Should().Contain(param.Name, "SQL should reference the parameter");
        }
    }

    [Fact]
    public void MySqlSql_SoftDeleteFilter_SchemaQualifiedTableReference()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("mydb")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithColumn("deleted_at", "datetime", isNullable: true)
                .WithMetadata("soft-delete", "deleted_at"))
            .Build();

        var table = model.GetTableFromDbName("Users");
        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .Build();

        service.ApplyTransformers(query, model, EmptyUserContext());

        var (sql, _) = GenerateSql(query, model);

        sql.Should().Contain("`mydb`.`Users`", "should use schema-qualified table reference with backticks");
        sql.Should().Contain("`deleted_at`");
        sql.Should().Contain("IS NULL");
    }

    [Fact]
    public void MySqlSql_SoftDeleteFilter_LimitOffsetPagination()
    {
        var model = CreateSoftDeleteModel();
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .WithSort("Id_asc")
            .WithPagination(5, 10)
            .Build();

        service.ApplyTransformers(query, model, EmptyUserContext());

        var (sql, _) = GenerateSql(query, model);

        sql.Should().Contain("`deleted_at`");
        sql.Should().Contain("IS NULL");
        sql.Should().Contain("LIMIT 10");
        sql.Should().Contain("OFFSET 5");
        sql.Should().NotContain("FETCH NEXT", "MySQL uses LIMIT/OFFSET, not FETCH NEXT");
    }

    #endregion

    #region Mutation Transformer Integration

    [Fact]
    public void SoftDeleteMutation_DeleteConvertsToUpdate_WithTimestamp()
    {
        var model = CreateSoftDeleteModel();
        var table = model.GetTableFromDbName("Users");

        var transformers = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new SoftDeleteMutationTransformer() }
        };

        var context = new MutationTransformContext
        {
            Model = model,
            UserContext = EmptyUserContext()
        };

        var data = new Dictionary<string, object?> { ["Id"] = 1 };
        var result = transformers.Transform(table, MutationType.Delete, data, context);

        result.MutationType.Should().Be(MutationType.Update);
        result.Data.Should().ContainKey("deleted_at");
        result.Data["deleted_at"].Should().BeOfType<DateTimeOffset>();
        result.Errors.Should().BeEmpty();
        result.AdditionalFilter.Should().NotBeNull("soft-delete mutations should add IS NULL filter");
    }

    [Fact]
    public void SoftDeleteMutation_DeleteTimestamp_IsRecentUtc()
    {
        var model = CreateSoftDeleteModel();
        var table = model.GetTableFromDbName("Users");

        var transformer = new SoftDeleteMutationTransformer();
        var context = new MutationTransformContext
        {
            Model = model,
            UserContext = EmptyUserContext()
        };

        var before = DateTimeOffset.UtcNow;
        var data = new Dictionary<string, object?> { ["Id"] = 1 };
        var result = transformer.Transform(table, MutationType.Delete, data, context);
        var after = DateTimeOffset.UtcNow;

        var timestamp = (DateTimeOffset)result.Data["deleted_at"]!;
        timestamp.Should().BeOnOrAfter(before);
        timestamp.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void SoftDeleteMutation_DeleteWithDeletedBy_PopulatesUserColumn()
    {
        var model = CreateSoftDeleteWithDeletedByModel();
        var table = model.GetTableFromDbName("Users");

        var transformers = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new SoftDeleteMutationTransformer() }
        };

        var context = new MutationTransformContext
        {
            Model = model,
            UserContext = new Dictionary<string, object?> { ["user_id"] = 99 }
        };

        var data = new Dictionary<string, object?> { ["Id"] = 1 };
        var result = transformers.Transform(table, MutationType.Delete, data, context);

        result.MutationType.Should().Be(MutationType.Update);
        result.Data.Should().ContainKey("deleted_at");
        result.Data.Should().ContainKey("deleted_by");
        result.Data["deleted_by"].Should().Be(99);
    }

    [Fact]
    public void SoftDeleteMutation_DeleteWithoutUserContext_SkipsDeletedBy()
    {
        var model = CreateSoftDeleteWithDeletedByModel();
        var table = model.GetTableFromDbName("Users");

        var transformers = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new SoftDeleteMutationTransformer() }
        };

        var context = new MutationTransformContext
        {
            Model = model,
            UserContext = EmptyUserContext()
        };

        var data = new Dictionary<string, object?> { ["Id"] = 1 };
        var result = transformers.Transform(table, MutationType.Delete, data, context);

        result.MutationType.Should().Be(MutationType.Update);
        result.Data.Should().ContainKey("deleted_at");
        result.Data.Should().NotContainKey("deleted_by",
            "deleted_by should not be set when user_id is missing from context");
    }

    [Fact]
    public void SoftDeleteMutation_Update_AddsIsNullFilter()
    {
        var model = CreateSoftDeleteModel();
        var table = model.GetTableFromDbName("Users");

        var transformers = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new SoftDeleteMutationTransformer() }
        };

        var context = new MutationTransformContext
        {
            Model = model,
            UserContext = EmptyUserContext()
        };

        var data = new Dictionary<string, object?> { ["Name"] = "Updated" };
        var result = transformers.Transform(table, MutationType.Update, data, context);

        result.MutationType.Should().Be(MutationType.Update);
        result.Data.Should().NotContainKey("deleted_at",
            "UPDATE should not set deleted_at");
        result.AdditionalFilter.Should().NotBeNull("UPDATE should filter to non-deleted records only");
        result.AdditionalFilter!.ColumnName.Should().Be("deleted_at");
    }

    [Fact]
    public void SoftDeleteMutation_Insert_NotAffected()
    {
        var model = CreateSoftDeleteModel();
        var table = model.GetTableFromDbName("Users");

        var transformer = new SoftDeleteMutationTransformer();
        var context = new MutationTransformContext
        {
            Model = model,
            UserContext = EmptyUserContext()
        };

        transformer.AppliesTo(table, MutationType.Insert, context).Should().BeFalse(
            "soft-delete should not apply to INSERT operations");
    }

    [Fact]
    public void SoftDeleteMutation_DeletePreservesOriginalData()
    {
        var model = CreateSoftDeleteModel();
        var table = model.GetTableFromDbName("Users");

        var transformer = new SoftDeleteMutationTransformer();
        var context = new MutationTransformContext
        {
            Model = model,
            UserContext = EmptyUserContext()
        };

        var data = new Dictionary<string, object?> { ["Id"] = 1 };
        var result = transformer.Transform(table, MutationType.Delete, data, context);

        result.Data.Should().ContainKey("Id");
        result.Data["Id"].Should().Be(1);
        result.Data.Should().ContainKey("deleted_at");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void SoftDeleteFilter_MissingColumn_ThrowsExecutionError()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("mydb")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithMetadata("soft-delete", "nonexistent_column"))
            .Build();

        var transformer = new SoftDeleteFilterTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = EmptyUserContext(),
            QueryType = QueryType.Standard,
            Path = "",
            IsNestedQuery = false
        };

        var act = () => transformer.GetAdditionalFilter(table, context);
        act.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>()
            .WithMessage("*not found in table*");
    }

    [Fact]
    public void SoftDeleteFilter_EmptyColumnMetadata_ReturnsNull()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("mydb")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithMetadata("soft-delete", ""))
            .Build();

        var transformer = new SoftDeleteFilterTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = EmptyUserContext(),
            QueryType = QueryType.Standard,
            Path = "",
            IsNestedQuery = false
        };

        transformer.AppliesTo(table, context).Should().BeTrue();
        var filter = transformer.GetAdditionalFilter(table, context);
        filter.Should().BeNull("empty column name should result in no filter");
    }

    [Fact]
    public void SoftDeleteFilter_NoSoftDeleteMetadata_DoesNotApply()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("mydb")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar"))
            .Build();

        var transformer = new SoftDeleteFilterTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = EmptyUserContext(),
            QueryType = QueryType.Standard,
            Path = "",
            IsNestedQuery = false
        };

        transformer.AppliesTo(table, context).Should().BeFalse(
            "transformer should not apply to tables without soft-delete metadata");
    }

    [Fact]
    public void SoftDeleteMutation_MissingColumn_ReturnsError()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("mydb")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithMetadata("soft-delete", "nonexistent_column"))
            .Build();

        var transformer = new SoftDeleteMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var context = new MutationTransformContext
        {
            Model = model,
            UserContext = EmptyUserContext()
        };

        var data = new Dictionary<string, object?> { ["Id"] = 1 };
        var result = transformer.Transform(table, MutationType.Delete, data, context);

        result.Errors.Should().NotBeEmpty();
        result.Errors[0].Should().Contain("not found in table");
    }

    #endregion

    #region Combined Filter + Mutation Flow

    [Fact]
    public void FullSoftDeleteFlow_QueryExcludesDeleted_DeleteConvertsToUpdate_MySqlSql()
    {
        var model = CreateSoftDeleteWithDeletedByModel();
        var table = model.GetTableFromDbName("Users");

        // Part 1: Query filtering with MySQL SQL generation
        var queryService = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name", "Email")
            .Build();

        queryService.ApplyTransformers(query, model, EmptyUserContext());

        var (selectSql, _) = GenerateSql(query, model);
        selectSql.Should().Contain("`deleted_at`");
        selectSql.Should().Contain("IS NULL");
        selectSql.Should().NotContain("[", "MySQL must not use bracket identifiers");
        selectSql.Should().NotContain("\"deleted_at\"", "MySQL must not use double-quote identifiers");

        // Part 2: Mutation transformation
        var mutationTransformers = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new SoftDeleteMutationTransformer() }
        };

        var mutationContext = new MutationTransformContext
        {
            Model = model,
            UserContext = new Dictionary<string, object?> { ["user_id"] = 42 }
        };

        var deleteData = new Dictionary<string, object?> { ["Id"] = 1 };
        var deleteResult = mutationTransformers.Transform(table, MutationType.Delete, deleteData, mutationContext);

        deleteResult.MutationType.Should().Be(MutationType.Update);
        deleteResult.Data["deleted_by"].Should().Be(42);
        deleteResult.Data.Should().ContainKey("deleted_at");
    }

    #endregion

    #region Helper Methods

    private static IDbModel CreateSoftDeleteModel()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("mydb")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithColumn("Email", "varchar")
                .WithColumn("deleted_at", "datetime", isNullable: true)
                .WithMetadata("soft-delete", "deleted_at"))
            .Build();
    }

    private static IDbModel CreateSoftDeleteWithDeletedByModel()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("mydb")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithColumn("Email", "varchar")
                .WithColumn("deleted_at", "datetime", isNullable: true)
                .WithColumn("deleted_by", "int", isNullable: true)
                .WithMetadata("soft-delete", "deleted_at")
                .WithMetadata("soft-delete-by", "deleted_by"))
            .Build();
    }

    private static (string sql, SqlParameterCollection parameters) GenerateSql(
        GqlObjectQuery query, IDbModel model)
    {
        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);
        var sql = sqls.Values.First().Sql;
        return (sql, parameters);
    }

    private static IDictionary<string, object?> EmptyUserContext()
    {
        return new Dictionary<string, object?>();
    }

    private static QueryTransformerService CreateTransformerService(params IFilterTransformer[] transformers)
    {
        var wrap = new FilterTransformersWrap { Transformers = transformers };
        return new QueryTransformerService(wrap);
    }

    #endregion
}
