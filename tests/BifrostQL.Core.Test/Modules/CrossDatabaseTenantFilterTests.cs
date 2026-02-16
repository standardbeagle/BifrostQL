using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Cross-database tenant filtering consistency tests.
/// Verifies that tenant isolation behaves identically across all 4 database dialects:
/// SQL Server, PostgreSQL, MySQL, and SQLite.
///
/// Security critical: any behavioral difference between databases in tenant filtering
/// could lead to data leakage. These tests assert identical security semantics
/// regardless of the underlying SQL dialect.
/// </summary>
public class CrossDatabaseTenantFilterTests
{
    /// <summary>
    /// All supported dialect configurations for cross-database testing.
    /// Each entry provides the dialect instance and the schema name convention for that database.
    /// </summary>
    public static IEnumerable<object[]> AllDialects =>
        new List<object[]>
        {
            new object[] { SqlServerDialect.Instance, "dbo", "SQL Server" },
            new object[] { PostgresDialect.Instance, "public", "PostgreSQL" },
            new object[] { MySqlDialect.Instance, "mydb", "MySQL" },
            new object[] { SqliteDialect.Instance, "main", "SQLite" },
        };

    #region Tenant Filter Applied - All Databases

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TenantFilter_InjectsWhereClause_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        var model = CreateTenantModel(schema);
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Total", "Status")
            .Build();

        service.ApplyTransformers(query, model, TenantContext(42));

        var (sql, parameters) = GenerateSql(query, model, dialect);

        sql.Should().Contain("WHERE",
            $"{dbName}: tenant filter must produce a WHERE clause");
        sql.Should().Contain("tenant_id",
            $"{dbName}: tenant column must appear in WHERE clause");
        parameters.Parameters.Should().Contain(p => Equals(p.Value, 42),
            $"{dbName}: tenant ID must be parameterized");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TenantFilter_ParameterizesValue_NeverInlinesTenantId(
        ISqlDialect dialect, string schema, string dbName)
    {
        var model = CreateTenantModel(schema);
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Total")
            .Build();

        service.ApplyTransformers(query, model, TenantContext(99));

        var (sql, parameters) = GenerateSql(query, model, dialect);

        parameters.Parameters.Should().Contain(p => Equals(p.Value, 99),
            $"{dbName}: tenant ID value must be a SQL parameter, never inlined in SQL text");
        sql.Should().NotContain("99",
            $"{dbName}: raw tenant ID value must not appear in SQL text");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TenantFilter_UsesDialectIdentifierEscaping(
        ISqlDialect dialect, string schema, string dbName)
    {
        var model = CreateTenantModel(schema);
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id")
            .Build();

        service.ApplyTransformers(query, model, TenantContext(1));

        var (sql, _) = GenerateSql(query, model, dialect);

        var escapedTenantCol = dialect.EscapeIdentifier("tenant_id");
        sql.Should().Contain(escapedTenantCol,
            $"{dbName}: tenant column must use dialect-specific identifier escaping ({escapedTenantCol})");

        var escapedTable = dialect.EscapeIdentifier("Orders");
        sql.Should().Contain(escapedTable,
            $"{dbName}: table name must use dialect-specific identifier escaping ({escapedTable})");
    }

    #endregion

    #region Tenant Context Required - Error Consistency

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TenantFilter_ThrowsWhenTenantContextMissing_AllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect; // dialect does not affect error behavior
        var model = CreateTenantModel(schema);
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Total")
            .Build();

        var emptyContext = new Dictionary<string, object?>();

        var act = () => service.ApplyTransformers(query, model, emptyContext);
        act.Should().Throw<BifrostExecutionError>(
                $"{dbName}: must throw when tenant context is missing")
            .WithMessage("*Tenant context required*")
            .WithMessage("*tenant_id*");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TenantFilter_ThrowsWhenTenantIdIsNull_AllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = CreateTenantModel(schema);
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id")
            .Build();

        var nullContext = new Dictionary<string, object?> { ["tenant_id"] = null };

        var act = () => service.ApplyTransformers(query, model, nullContext);
        act.Should().Throw<BifrostExecutionError>(
                $"{dbName}: must throw when tenant ID value is null")
            .WithMessage("*cannot be null*");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TenantFilter_ThrowsWhenColumnNotInTable_AllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema(schema)
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
        act.Should().Throw<BifrostExecutionError>(
                $"{dbName}: must throw when tenant column does not exist in table")
            .WithMessage("*not found in table*");
    }

    #endregion

    #region Custom Tenant Context Key - All Databases

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TenantFilter_CustomContextKey_WorksAcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("tenant-context-key", "org_id")
            .WithTable("Orders", t => t
                .WithSchema(schema)
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

        var (sql, parameters) = GenerateSql(query, model, dialect);

        sql.Should().Contain("tenant_id",
            $"{dbName}: custom context key should still filter on the configured column");
        parameters.Parameters.Should().Contain(p => Equals(p.Value, 77),
            $"{dbName}: custom context key value must be parameterized");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TenantFilter_CustomContextKey_ThrowsWhenMissing_AllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("tenant-context-key", "org_id")
            .WithTable("Orders", t => t
                .WithSchema(schema)
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithMetadata("tenant-filter", "tenant_id"))
            .Build();

        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(model.GetTableFromDbName("Orders"))
            .WithColumns("Id")
            .Build();

        // Provide default key instead of configured custom key
        var wrongContext = new Dictionary<string, object?> { ["tenant_id"] = 42 };

        var act = () => service.ApplyTransformers(query, model, wrongContext);
        act.Should().Throw<BifrostExecutionError>(
                $"{dbName}: must throw when custom context key is not provided")
            .WithMessage("*org_id*");
    }

    #endregion

    #region Combined Filters - All Databases

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TenantFilter_CombinesWithUserFilter_AllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        var model = CreateTenantModel(schema);
        var table = model.GetTableFromDbName("Orders");

        var service = CreateTransformerService(new TenantFilterTransformer());
        var userFilter = TableFilterFactory.Equals("Orders", "Status", "Active");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Total", "Status")
            .WithFilter(userFilter)
            .Build();

        service.ApplyTransformers(query, model, TenantContext(42));

        var (sql, parameters) = GenerateSql(query, model, dialect);

        sql.Should().Contain("WHERE",
            $"{dbName}: combined filter must produce WHERE clause");
        sql.Should().Contain("tenant_id",
            $"{dbName}: tenant filter must be present alongside user filter");
        sql.Should().Contain("Status",
            $"{dbName}: user-supplied filter must still be present");
        parameters.Parameters.Count.Should().BeGreaterThanOrEqualTo(2,
            $"{dbName}: both tenant and user filter values must be parameterized");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TenantFilter_WithSoftDelete_GeneratesBothFilters_AllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema(schema)
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

        var (sql, parameters) = GenerateSql(query, model, dialect);

        sql.Should().Contain("tenant_id",
            $"{dbName}: tenant filter must be present when combined with soft-delete");
        sql.Should().Contain("deleted_at",
            $"{dbName}: soft-delete filter must be present when combined with tenant filter");
        sql.Should().Contain("IS NULL",
            $"{dbName}: soft-delete must check for NULL");
        parameters.Parameters.Should().Contain(p => p.Value != null && p.Value.Equals(42),
            $"{dbName}: tenant_id parameter must be bound");
    }

    #endregion

    #region Unflagged Tables - No Filter Injection

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TenantFilter_DoesNotApplyToUnflaggedTable_AllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema(schema)
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
            $"{dbName}: tenant filter must not inject into tables without tenant-filter metadata");
    }

    #endregion

    #region String Tenant ID - All Databases

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TenantFilter_StringTenantId_WorksAcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema(schema)
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

        var (sql, parameters) = GenerateSql(query, model, dialect);

        sql.Should().Contain("tenant_id",
            $"{dbName}: string tenant ID must still produce tenant filter");
        parameters.Parameters.Should().Contain(p => Equals(p.Value, "acme-corp"),
            $"{dbName}: string tenant ID must be parameterized");
    }

    #endregion

    #region Multi-Table Tenant Isolation - All Databases

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TenantFilter_MultipleTablesAllGetFiltered_AllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema(schema)
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("Total", "decimal")
                .WithMetadata("tenant-filter", "tenant_id"))
            .WithTable("Invoices", t => t
                .WithSchema(schema)
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
                $"{dbName}: tenant filter must apply to {table.DbName}");

            var filter = transformer.GetAdditionalFilter(table, context);
            filter.Should().NotBeNull(
                $"{dbName}: tenant filter must produce a filter for {table.DbName}");
            filter!.ColumnName.Should().Be("tenant_id",
                $"{dbName}: filter must target tenant_id column on {table.DbName}");
            filter.Next!.Value.Should().Be(10,
                $"{dbName}: filter must carry the tenant ID value for {table.DbName}");
        }
    }

    #endregion

    #region Cross-Database Behavioral Equivalence

    [Fact]
    public void TenantFilter_AllDialects_ProduceStructurallyEquivalentFilters()
    {
        var dialects = new (ISqlDialect dialect, string schema, string name)[]
        {
            (SqlServerDialect.Instance, "dbo", "SQL Server"),
            (PostgresDialect.Instance, "public", "PostgreSQL"),
            (MySqlDialect.Instance, "mydb", "MySQL"),
            (SqliteDialect.Instance, "main", "SQLite"),
        };

        var transformer = new TenantFilterTransformer();

        foreach (var (dialect, schema, name) in dialects)
        {
            var model = CreateTenantModel(schema);
            var table = model.GetTableFromDbName("Orders");
            var context = new QueryTransformContext
            {
                Model = model,
                UserContext = TenantContext(42),
                QueryType = QueryType.Standard,
                Path = "",
                IsNestedQuery = false
            };

            transformer.AppliesTo(table, context).Should().BeTrue(
                $"{name}: transformer must apply to tenant-filtered table");

            var filter = transformer.GetAdditionalFilter(table, context);
            filter.Should().NotBeNull($"{name}: filter must not be null");
            filter!.ColumnName.Should().Be("tenant_id",
                $"{name}: filter column must be tenant_id");
            filter.Next.Should().NotBeNull($"{name}: filter must have a value node");
            filter.Next!.Value.Should().Be(42,
                $"{name}: filter value must be the tenant ID");
        }
    }

    [Fact]
    public void TenantFilter_AllDialects_ProduceSameParameterCount()
    {
        var dialects = new (ISqlDialect dialect, string schema)[]
        {
            (SqlServerDialect.Instance, "dbo"),
            (PostgresDialect.Instance, "public"),
            (MySqlDialect.Instance, "mydb"),
            (SqliteDialect.Instance, "main"),
        };

        int? expectedParamCount = null;

        foreach (var (dialect, schema) in dialects)
        {
            var model = CreateTenantModel(schema);
            var table = model.GetTableFromDbName("Orders");

            var service = CreateTransformerService(new TenantFilterTransformer());
            var query = GqlObjectQueryBuilder.Create()
                .WithDbTable(table)
                .WithColumns("Id", "Total", "Status")
                .Build();

            service.ApplyTransformers(query, model, TenantContext(42));

            var (_, parameters) = GenerateSql(query, model, dialect);

            if (expectedParamCount == null)
            {
                expectedParamCount = parameters.Parameters.Count;
            }
            else
            {
                parameters.Parameters.Count.Should().Be(expectedParamCount.Value,
                    "all dialects must produce the same number of SQL parameters for equivalent queries");
            }
        }
    }

    [Fact]
    public void TenantFilter_AllDialects_ParameterValuesAreIdentical()
    {
        var dialects = new (ISqlDialect dialect, string schema)[]
        {
            (SqlServerDialect.Instance, "dbo"),
            (PostgresDialect.Instance, "public"),
            (MySqlDialect.Instance, "mydb"),
            (SqliteDialect.Instance, "main"),
        };

        List<object?>? expectedValues = null;

        foreach (var (dialect, schema) in dialects)
        {
            var model = CreateTenantModel(schema);
            var table = model.GetTableFromDbName("Orders");

            var service = CreateTransformerService(new TenantFilterTransformer());
            var query = GqlObjectQueryBuilder.Create()
                .WithDbTable(table)
                .WithColumns("Id", "Total")
                .Build();

            service.ApplyTransformers(query, model, TenantContext(42));

            var (_, parameters) = GenerateSql(query, model, dialect);
            var values = parameters.Parameters.Select(p => p.Value).ToList();

            if (expectedValues == null)
            {
                expectedValues = values;
            }
            else
            {
                values.Should().BeEquivalentTo(expectedValues,
                    "all dialects must bind identical parameter values for equivalent tenant-filtered queries");
            }
        }
    }

    #endregion

    #region Helper Methods

    private static IDbModel CreateTenantModel(string schema)
    {
        return DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema(schema)
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
        GqlObjectQuery query, IDbModel model, ISqlDialect dialect)
    {
        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, dialect, sqls, parameters);
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
