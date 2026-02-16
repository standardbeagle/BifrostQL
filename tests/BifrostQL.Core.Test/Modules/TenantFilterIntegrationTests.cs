using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Integration tests verifying the complete tenant filtering system for SQL Server:
/// - TenantFilterTransformer injects WHERE tenant_id = @pN into queries
/// - Bracket identifier escaping [tenant_id] in generated SQL
/// - Error when tenant context is missing
/// - Combination with user-supplied filters
/// - Custom tenant context key via model metadata
/// - Generated SQL is syntactically valid via TSql160Parser
/// </summary>
public class TenantFilterIntegrationTests
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;
    private readonly TSql160Parser _parser = new(initialQuotedIdentifiers: true);

    #region SQL Generation with Tenant Filter

    [Fact]
    public void TenantFilter_GeneratesSqlWithWhereClause()
    {
        var model = CreateTenantModel();
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Total", "Status")
            .Build();

        var userContext = TenantContext(42);
        service.ApplyTransformers(query, model, userContext);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);

        var sql = sqls["Orders"].Sql;
        sql.Should().Contain("WHERE");
        sql.Should().Contain("[tenant_id]", "SQL Server must bracket-escape the tenant column");
        AssertValidSql(sql, parameters);
    }

    [Fact]
    public void TenantFilter_ParameterizesValue()
    {
        var model = CreateTenantModel();
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Total")
            .Build();

        var userContext = TenantContext(99);
        service.ApplyTransformers(query, model, userContext);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);

        parameters.Parameters.Should().Contain(p => Equals(p.Value, 99),
            "tenant ID value must be passed as a SQL parameter, not inlined");
        AssertValidSql(sqls["Orders"].Sql, parameters);
    }

    [Fact]
    public void TenantFilter_CombinesWithUserFilter()
    {
        var model = CreateTenantModel();
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var userFilter = TableFilterFactory.Equals("Orders", "Status", "Active");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Total", "Status")
            .WithFilter(userFilter)
            .Build();

        var userContext = TenantContext(42);
        service.ApplyTransformers(query, model, userContext);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);

        var sql = sqls["Orders"].Sql;
        sql.Should().Contain("WHERE");
        sql.Should().Contain("[tenant_id]");
        sql.Should().Contain("[Status]");
        AssertValidSql(sql, parameters);
    }

    [Fact]
    public void TenantFilter_BracketEscapesTableAndColumn()
    {
        var model = CreateTenantModel();
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id")
            .Build();

        service.ApplyTransformers(query, model, TenantContext(1));

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);

        var sql = sqls["Orders"].Sql;
        sql.Should().Contain("[Orders]", "table name must be bracket-escaped");
        sql.Should().Contain("[tenant_id]", "tenant column must be bracket-escaped");
        sql.Should().Contain("[Id]", "selected columns must be bracket-escaped");
        AssertValidSql(sql, parameters);
    }

    #endregion

    #region Error Handling

    [Fact]
    public void TenantFilter_ThrowsWhenTenantContextMissing()
    {
        var model = CreateTenantModel();
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Total")
            .Build();

        var emptyContext = new Dictionary<string, object?>();

        var act = () => service.ApplyTransformers(query, model, emptyContext);
        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*Tenant context required*")
            .WithMessage("*tenant_id*");
    }

    [Fact]
    public void TenantFilter_ThrowsWhenTenantIdIsNull()
    {
        var model = CreateTenantModel();
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id")
            .Build();

        var nullContext = new Dictionary<string, object?> { ["tenant_id"] = null };

        var act = () => service.ApplyTransformers(query, model, nullContext);
        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*cannot be null*");
    }

    [Fact]
    public void TenantFilter_ThrowsWhenColumnNotInTable()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata("tenant-filter", "missing_column"))
            .Build();

        var transformer = new TenantFilterTransformer();
        var table = model.GetTableFromDbName("Orders");
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = TenantContext(1),
            QueryType = QueryType.Standard,
            Path = "",
            IsNestedQuery = false
        };

        var act = () => transformer.GetAdditionalFilter(table, context);
        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*not found in table*");
    }

    #endregion

    #region Custom Tenant Context Key

    [Fact]
    public void TenantFilter_CustomContextKey_UsesConfiguredKey()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("tenant-context-key", "org_id")
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("Total", "decimal")
                .WithMetadata("tenant-filter", "tenant_id"))
            .Build();

        var service = CreateTransformerService(new TenantFilterTransformer());
        var table = model.GetTableFromDbName("Orders");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Total")
            .Build();

        var userContext = new Dictionary<string, object?> { ["org_id"] = 77 };
        service.ApplyTransformers(query, model, userContext);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);

        var sql = sqls["Orders"].Sql;
        sql.Should().Contain("[tenant_id]");
        parameters.Parameters.Should().Contain(p => Equals(p.Value, 77));
        AssertValidSql(sql, parameters);
    }

    [Fact]
    public void TenantFilter_CustomContextKey_ThrowsWhenCustomKeyMissing()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("tenant-context-key", "org_id")
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithMetadata("tenant-filter", "tenant_id"))
            .Build();

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(model.GetTableFromDbName("Orders"))
            .WithColumns("Id")
            .Build();

        // Provide default key instead of custom key
        var wrongContext = new Dictionary<string, object?> { ["tenant_id"] = 42 };

        var act = () => service.ApplyTransformers(query, model, wrongContext);
        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*org_id*");
    }

    #endregion

    #region Combined with Other Transformers

    [Fact]
    public void TenantFilter_WithSoftDelete_GeneratesBothFilters()
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

        service.ApplyTransformers(query, model, TenantContext(42));

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);

        var sql = sqls["Orders"].Sql;
        sql.Should().Contain("[tenant_id]", "tenant filter must be present");
        sql.Should().Contain("[deleted_at]", "soft-delete filter must be present");
        sql.Should().Contain("IS NULL", "soft-delete checks for NULL");
        AssertValidSql(sql, parameters);
    }

    #endregion

    #region Table Without Tenant Metadata

    [Fact]
    public void TenantFilter_DoesNotApplyToUnflaggedTable()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .Build();

        service.ApplyTransformers(query, model, new Dictionary<string, object?>());

        query.Filter.Should().BeNull(
            "tenant filter must not inject into tables without tenant-filter metadata");
    }

    #endregion

    #region String Tenant ID

    [Fact]
    public void TenantFilter_StringTenantId_GeneratesValidSql()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "nvarchar")
                .WithColumn("Total", "decimal")
                .WithMetadata("tenant-filter", "tenant_id"))
            .Build();

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(model.GetTableFromDbName("Orders"))
            .WithColumns("Id", "Total")
            .Build();

        var userContext = new Dictionary<string, object?> { ["tenant_id"] = "acme-corp" };
        service.ApplyTransformers(query, model, userContext);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);

        var sql = sqls["Orders"].Sql;
        sql.Should().Contain("[tenant_id]");
        parameters.Parameters.Should().Contain(p => Equals(p.Value, "acme-corp"));
        AssertValidSql(sql, parameters);
    }

    #endregion

    #region Multi-Table Tenant Isolation

    [Fact]
    public void TenantFilter_MultipleTablesWithMetadata_AllGetFiltered()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("Total", "decimal")
                .WithMetadata("tenant-filter", "tenant_id"))
            .WithTable("Invoices", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("Amount", "decimal")
                .WithMetadata("tenant-filter", "tenant_id"))
            .Build();

        var transformer = new TenantFilterTransformer();

        foreach (var table in model.Tables)
        {
            var context = new QueryTransformContext
            {
                Model = model,
                UserContext = TenantContext(10),
                QueryType = QueryType.Standard,
                Path = "",
                IsNestedQuery = false
            };

            transformer.AppliesTo(table, context).Should().BeTrue(
                $"tenant filter should apply to {table.DbName}");

            var filter = transformer.GetAdditionalFilter(table, context);
            filter.Should().NotBeNull();
            filter!.ColumnName.Should().Be("tenant_id");
            filter.Next!.Value.Should().Be(10);
        }
    }

    #endregion

    #region Helper Methods

    private static IDbModel CreateTenantModel()
    {
        return DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("Total", "decimal")
                .WithColumn("Status", "nvarchar")
                .WithMetadata("tenant-filter", "tenant_id"))
            .Build();
    }

    private static IDictionary<string, object?> TenantContext(object tenantId)
    {
        return new Dictionary<string, object?> { ["tenant_id"] = tenantId };
    }

    private static QueryTransformerService CreateTransformerService(params IFilterTransformer[] transformers)
    {
        var wrap = new FilterTransformersWrap { Transformers = transformers };
        return new QueryTransformerService(wrap);
    }

    private void AssertValidSql(string sql, SqlParameterCollection parameters)
    {
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
