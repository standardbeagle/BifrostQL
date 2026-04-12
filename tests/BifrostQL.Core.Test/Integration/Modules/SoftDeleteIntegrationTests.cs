using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Integration tests verifying the complete soft-delete system:
/// - Filter transformers add WHERE deleted_at IS NULL to queries
/// - Mutation transformers convert DELETE to UPDATE with timestamp
/// - _includeDeleted bypasses soft-delete filtering
/// - deleted_by column populated from user context
/// - Generated SQL is syntactically valid
/// </summary>
public class SoftDeleteIntegrationTests
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;
    private readonly TSql160Parser _parser = new(initialQuotedIdentifiers: true);

    #region Filter + SQL Generation Integration

    [Fact]
    public void SoftDeleteFilter_GeneratesSqlWithIsNullWhereClause()
    {
        var model = CreateSoftDeleteModel();
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name", "Email")
            .Build();

        service.ApplyTransformers(query, model, EmptyUserContext());

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);

        var sql = sqls["Users"].Sql;
        sql.Should().Contain("WHERE");
        sql.Should().Contain("[deleted_at]");
        sql.Should().Contain("IS NULL");
        AssertValidSql(sql, parameters);
    }

    [Fact]
    public void SoftDeleteFilter_CombinesWithUserFilter()
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

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);

        var sql = sqls["Users"].Sql;
        sql.Should().Contain("WHERE");
        sql.Should().Contain("[deleted_at]");
        sql.Should().Contain("IS NULL");
        AssertValidSql(sql, parameters);
    }

    [Fact]
    public void SoftDeleteFilter_WithTenantFilter_GeneratesBothFilters()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("Total", "decimal")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
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

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);

        var sql = sqls["Orders"].Sql;
        sql.Should().Contain("WHERE");
        sql.Should().Contain("[tenant_id]");
        sql.Should().Contain("[deleted_at]");
        sql.Should().Contain("IS NULL");
        AssertValidSql(sql, parameters);
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

        var userContext = new Dictionary<string, object?> { ["include_deleted:dbo.Users"] = true };
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

        var userContext = new Dictionary<string, object?> { ["include_deleted:dbo.Orders"] = true };
        service.ApplyTransformers(query, model, userContext);

        query.Filter.Should().NotBeNull("soft-delete filter should still apply for Users when include_deleted is set for Orders");
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

    #endregion

    #region Combined Filter + Mutation Flow

    [Fact]
    public void FullSoftDeleteFlow_QueryExcludesDeleted_DeleteConvertsToUpdate()
    {
        var model = CreateSoftDeleteWithDeletedByModel();
        var table = model.GetTableFromDbName("Users");

        // Part 1: Query filtering
        var queryService = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name", "Email")
            .Build();

        queryService.ApplyTransformers(query, model, EmptyUserContext());

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);

        var selectSql = sqls["Users"].Sql;
        selectSql.Should().Contain("[deleted_at]");
        selectSql.Should().Contain("IS NULL");

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

    #region Schema Generation

    [Fact]
    public void SchemaGeneration_SoftDeleteTable_IncludesIncludeDeletedArgument()
    {
        var model = CreateSoftDeleteModel();
        var schema = BifrostQL.Core.Schema.DbSchema.FromModel(model);

        var queryType = schema.Query;
        var usersField = queryType.Fields.FirstOrDefault(f => f.Name == "Users");
        usersField.Should().NotBeNull("Users field should exist in query type");

        var includeDeletedArg = usersField!.Arguments?.FirstOrDefault(a => a.Name == "_includeDeleted");
        includeDeletedArg.Should().NotBeNull(
            "tables with soft-delete should expose _includeDeleted argument");
    }

    [Fact]
    public void SchemaGeneration_NonSoftDeleteTable_ExcludesIncludeDeletedArgument()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var schema = BifrostQL.Core.Schema.DbSchema.FromModel(model);

        var queryType = schema.Query;
        var usersField = queryType.Fields.FirstOrDefault(f => f.Name == "Users");
        usersField.Should().NotBeNull("Users field should exist in query type");

        var includeDeletedArg = usersField!.Arguments?.FirstOrDefault(a => a.Name == "_includeDeleted");
        includeDeletedArg.Should().BeNull(
            "tables without soft-delete should not expose _includeDeleted argument");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void SoftDeleteFilter_MissingColumn_ThrowsExecutionError()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
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
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
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
    public void SoftDeleteFilter_NoSoftDeleteMetadata_DoesNotApply()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var table = model.GetTableFromDbName("Users");

        var transformer = new SoftDeleteFilterTransformer();
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

    private static IDbModel CreateSoftDeleteWithDeletedByModel()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithColumn("deleted_by", "int", isNullable: true)
                .WithMetadata("soft-delete", "deleted_at")
                .WithMetadata("soft-delete-by", "deleted_by"))
            .Build();
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

    private void AssertValidSql(string sql, SqlParameterCollection parameters)
    {
        // Replace parameter placeholders with dummy values for parsing
        var parsableSql = sql;
        foreach (var param in parameters.Parameters)
        {
            parsableSql = parsableSql.Replace($"@{param.Name}", param.Value == null ? "NULL" : $"'{param.Value}'");
        }

        using var reader = new StringReader(parsableSql);
        _parser.Parse(reader, out var errors);
        errors.Should().BeEmpty(
            $"SQL should be valid but got errors: {string.Join("; ", errors.Select(e => e.Message))}.\nSQL: {parsableSql}");
    }

    #endregion
}
