using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.MySql;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// MySQL-specific tenant filtering integration tests.
/// Verifies TenantFilterTransformer produces correct MySQL SQL syntax
/// (backtick identifiers, LIMIT/OFFSET pagination, @-prefixed parameters).
/// Mirrors PostgresTenantFilterIntegrationTests but uses MySqlDialect instead of PostgresDialect.
/// </summary>
public class MySqlTenantFilterIntegrationTests
{
    private static readonly ISqlDialect Dialect = MySqlDialect.Instance;

    #region SQL Generation with Tenant Filter

    [Fact]
    public void TenantFilter_GeneratesMySqlWhereClause()
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

        var (sql, _) = GenerateSql(query, model);

        sql.Should().Contain("WHERE");
        sql.Should().Contain("`tenant_id`", "MySQL must backtick-escape the tenant column");
        sql.Should().NotContain("[tenant_id]", "MySQL must not use bracket identifiers");
        sql.Should().NotContain("\"tenant_id\"", "MySQL must not use double-quote identifiers");
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

        var (sql, parameters) = GenerateSql(query, model);

        parameters.Parameters.Should().Contain(p => Equals(p.Value, 99),
            "tenant ID value must be passed as a SQL parameter, not inlined");
        foreach (var param in parameters.Parameters)
        {
            sql.Should().Contain(param.Name, "SQL should reference the parameter");
        }
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

        var (sql, parameters) = GenerateSql(query, model);

        sql.Should().Contain("WHERE");
        sql.Should().Contain("`tenant_id`");
        sql.Should().Contain("`Status`");
        parameters.Parameters.Should().NotBeEmpty("filter values should be parameterized");
    }

    [Fact]
    public void TenantFilter_BacktickEscapesTableAndColumn()
    {
        var model = CreateTenantModel();
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id")
            .Build();

        service.ApplyTransformers(query, model, TenantContext(1));

        var (sql, _) = GenerateSql(query, model);

        sql.Should().Contain("`Orders`", "table name must be backtick-escaped");
        sql.Should().Contain("`tenant_id`", "tenant column must be backtick-escaped");
        sql.Should().Contain("`Id`", "selected columns must be backtick-escaped");
        sql.Should().NotContain("[", "MySQL must not use bracket identifiers");
        sql.Should().NotContain("\"Orders\"", "MySQL must not use double-quote identifiers");
    }

    [Fact]
    public void TenantFilter_SchemaQualifiedTableReference()
    {
        var model = CreateTenantModel();
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Total")
            .Build();

        service.ApplyTransformers(query, model, TenantContext(5));

        var (sql, _) = GenerateSql(query, model);

        sql.Should().Contain("`mydb`.`Orders`", "should use schema-qualified table reference with backticks");
    }

    [Fact]
    public void TenantFilter_WithPagination_UsesLimitOffset()
    {
        var model = CreateTenantModel();
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Total")
            .WithSort("Id_asc")
            .WithPagination(5, 10)
            .Build();

        service.ApplyTransformers(query, model, TenantContext(42));

        var (sql, _) = GenerateSql(query, model);

        sql.Should().Contain("`tenant_id`");
        sql.Should().Contain("LIMIT 10");
        sql.Should().Contain("OFFSET 5");
        sql.Should().NotContain("FETCH NEXT", "MySQL uses LIMIT/OFFSET, not FETCH NEXT");
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
                .WithSchema("mydb")
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
                .WithSchema("mydb")
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

        var (sql, parameters) = GenerateSql(query, model);

        sql.Should().Contain("`tenant_id`");
        sql.Should().NotContain("[tenant_id]", "MySQL must not use bracket identifiers");
        sql.Should().NotContain("\"tenant_id\"", "MySQL must not use double-quote identifiers");
        parameters.Parameters.Should().Contain(p => Equals(p.Value, 77));
    }

    [Fact]
    public void TenantFilter_CustomContextKey_ThrowsWhenCustomKeyMissing()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("tenant-context-key", "org_id")
            .WithTable("Orders", t => t
                .WithSchema("mydb")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithMetadata("tenant-filter", "tenant_id"))
            .Build();

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(model.GetTableFromDbName("Orders"))
            .WithColumns("Id")
            .Build();

        var wrongContext = new Dictionary<string, object?> { ["tenant_id"] = 42 };

        var act = () => service.ApplyTransformers(query, model, wrongContext);
        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*org_id*");
    }

    #endregion

    #region Combined with Other Transformers

    [Fact]
    public void TenantFilter_WithSoftDelete_GeneratesBothFiltersInMySql()
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

        service.ApplyTransformers(query, model, TenantContext(42));

        var (sql, parameters) = GenerateSql(query, model);

        sql.Should().Contain("`tenant_id`", "tenant filter must be present");
        sql.Should().Contain("`deleted_at`", "soft-delete filter must be present");
        sql.Should().Contain("IS NULL", "soft-delete checks for NULL");
        sql.Should().NotContain("[", "MySQL must not use bracket identifiers");
        sql.Should().NotContain("\"tenant_id\"", "MySQL must not use double-quote identifiers");
        parameters.Parameters.Should().Contain(p => p.Value != null && p.Value.Equals(42),
            "tenant_id parameter should be bound");
    }

    #endregion

    #region Table Without Tenant Metadata

    [Fact]
    public void TenantFilter_DoesNotApplyToUnflaggedTable()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("mydb")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithColumn("Email", "varchar"))
            .Build();
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
    public void TenantFilter_StringTenantId_GeneratesValidMySqlSql()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("mydb")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "varchar")
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

        var (sql, parameters) = GenerateSql(query, model);

        sql.Should().Contain("`tenant_id`");
        sql.Should().NotContain("[tenant_id]", "MySQL must not use bracket identifiers");
        sql.Should().NotContain("\"tenant_id\"", "MySQL must not use double-quote identifiers");
        parameters.Parameters.Should().Contain(p => Equals(p.Value, "acme-corp"));
    }

    #endregion

    #region Multi-Table Tenant Isolation

    [Fact]
    public void TenantFilter_MultipleTablesWithMetadata_AllGetFiltered()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("mydb")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("Total", "decimal")
                .WithMetadata("tenant-filter", "tenant_id"))
            .WithTable("Invoices", t => t
                .WithSchema("mydb")
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

    #region MySQL-Specific Parameter Verification

    [Fact]
    public void TenantFilter_UsesAtPrefixParameters()
    {
        var model = CreateTenantModel();
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Total")
            .Build();

        service.ApplyTransformers(query, model, TenantContext(42));

        var (sql, parameters) = GenerateSql(query, model);

        parameters.Parameters.Should().NotBeEmpty();
        foreach (var param in parameters.Parameters)
        {
            param.Name.Should().StartWith("@", "MySQL parameters should use @ prefix");
            sql.Should().Contain(param.Name, "SQL should reference the parameter");
        }
    }

    #endregion

    #region Helper Methods

    private static IDbModel CreateTenantModel()
    {
        return DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("mydb")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("Total", "decimal")
                .WithColumn("Status", "varchar")
                .WithMetadata("tenant-filter", "tenant_id"))
            .Build();
    }

    private static IDictionary<string, object?> TenantContext(object tenantId)
    {
        return new Dictionary<string, object?> { ["tenant_id"] = tenantId };
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

    private static QueryTransformerService CreateTransformerService(params IFilterTransformer[] transformers)
    {
        var wrap = new FilterTransformersWrap { Transformers = transformers };
        return new QueryTransformerService(wrap);
    }

    #endregion
}
