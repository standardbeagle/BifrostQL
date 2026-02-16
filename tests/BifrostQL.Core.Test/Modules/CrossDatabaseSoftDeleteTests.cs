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
/// Cross-database soft delete consistency tests.
/// Verifies that soft-delete filtering, mutation transformation, and _includeDeleted bypass
/// behave identically across all 4 database dialects: SQL Server, PostgreSQL, MySQL, and SQLite.
///
/// Data integrity critical: any behavioral difference between databases in soft-delete handling
/// could lead to deleted records appearing in queries or permanent data loss on DELETE.
/// These tests assert identical semantics regardless of the underlying SQL dialect.
/// </summary>
public class CrossDatabaseSoftDeleteTests
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

    #region Soft Delete Filter Applied - All Databases

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteFilter_InjectsIsNullWhereClause_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        var model = CreateSoftDeleteModel(schema);
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name", "Email")
            .Build();

        service.ApplyTransformers(query, model, EmptyUserContext());

        var (sql, _) = GenerateSql(query, model, dialect);

        sql.Should().Contain("WHERE",
            $"{dbName}: soft-delete filter must produce a WHERE clause");
        sql.Should().Contain("deleted_at",
            $"{dbName}: soft-delete column must appear in WHERE clause");
        sql.Should().Contain("IS NULL",
            $"{dbName}: soft-delete filter must check for NULL");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteFilter_UsesDialectIdentifierEscaping(
        ISqlDialect dialect, string schema, string dbName)
    {
        var model = CreateSoftDeleteModel(schema);
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id")
            .Build();

        service.ApplyTransformers(query, model, EmptyUserContext());

        var (sql, _) = GenerateSql(query, model, dialect);

        var escapedDeletedAt = dialect.EscapeIdentifier("deleted_at");
        sql.Should().Contain(escapedDeletedAt,
            $"{dbName}: soft-delete column must use dialect-specific identifier escaping ({escapedDeletedAt})");

        var escapedTable = dialect.EscapeIdentifier("Users");
        sql.Should().Contain(escapedTable,
            $"{dbName}: table name must use dialect-specific identifier escaping ({escapedTable})");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteFilter_CombinesWithUserFilter_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        var model = CreateSoftDeleteModel(schema);
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var userFilter = TableFilterFactory.Equals("Users", "Name", "Alice");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .WithFilter(userFilter)
            .Build();

        service.ApplyTransformers(query, model, EmptyUserContext());

        var (sql, parameters) = GenerateSql(query, model, dialect);

        sql.Should().Contain("WHERE",
            $"{dbName}: combined filter must produce WHERE clause");
        sql.Should().Contain("deleted_at",
            $"{dbName}: soft-delete filter must be present alongside user filter");
        sql.Should().Contain("IS NULL",
            $"{dbName}: soft-delete must check for NULL");
        sql.Should().Contain("Name",
            $"{dbName}: user-supplied filter must still be present");
        parameters.Parameters.Should().NotBeEmpty(
            $"{dbName}: user filter value must be parameterized");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteFilter_WithTenantFilter_GeneratesBothFilters_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        var model = CreateSoftDeleteWithTenantModel(schema);
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

    #region Include Deleted Bypass - All Databases

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteFilter_IncludeDeleted_OmitsFilter_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = CreateSoftDeleteModel(schema);
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .Build();

        var userContext = new Dictionary<string, object?> { ["include_deleted"] = true };
        service.ApplyTransformers(query, model, userContext);

        query.Filter.Should().BeNull(
            $"{dbName}: soft-delete filter must not apply when include_deleted is true");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteFilter_IncludeDeletedPerTable_OmitsFilterForSpecificTable_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = CreateSoftDeleteModel(schema);
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .Build();

        var tableKey = $"include_deleted:{schema}.Users";
        var userContext = new Dictionary<string, object?> { [tableKey] = true };
        service.ApplyTransformers(query, model, userContext);

        query.Filter.Should().BeNull(
            $"{dbName}: soft-delete filter must not apply when table-specific include_deleted is true");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteFilter_IncludeDeletedForOtherTable_StillFilters_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = CreateSoftDeleteModel(schema);
        var table = model.GetTableFromDbName("Users");

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .Build();

        var otherTableKey = $"include_deleted:{schema}.Orders";
        var userContext = new Dictionary<string, object?> { [otherTableKey] = true };
        service.ApplyTransformers(query, model, userContext);

        query.Filter.Should().NotBeNull(
            $"{dbName}: soft-delete filter must still apply when include_deleted is set for a different table");
    }

    #endregion

    #region Mutation Transformation - All Databases

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteMutation_DeleteConvertsToUpdate_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = CreateSoftDeleteModel(schema);
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

        result.MutationType.Should().Be(MutationType.Update,
            $"{dbName}: DELETE must be converted to UPDATE for soft-delete tables");
        result.Data.Should().ContainKey("deleted_at",
            $"{dbName}: soft-delete must set deleted_at column");
        result.Data["deleted_at"].Should().BeOfType<DateTimeOffset>(
            $"{dbName}: deleted_at must be a DateTimeOffset value");
        result.Errors.Should().BeEmpty(
            $"{dbName}: soft-delete mutation must not produce errors");
        result.AdditionalFilter.Should().NotBeNull(
            $"{dbName}: soft-delete mutation must add IS NULL filter");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteMutation_DeletePreservesOriginalData_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = CreateSoftDeleteModel(schema);
        var table = model.GetTableFromDbName("Users");

        var transformer = new SoftDeleteMutationTransformer();
        var context = new MutationTransformContext
        {
            Model = model,
            UserContext = EmptyUserContext()
        };

        var data = new Dictionary<string, object?> { ["Id"] = 1 };
        var result = transformer.Transform(table, MutationType.Delete, data, context);

        result.Data.Should().ContainKey("Id",
            $"{dbName}: original data must be preserved in soft-delete mutation");
        result.Data["Id"].Should().Be(1,
            $"{dbName}: original data values must not be altered");
        result.Data.Should().ContainKey("deleted_at",
            $"{dbName}: deleted_at must be added to mutation data");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteMutation_Update_AddsIsNullFilter_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = CreateSoftDeleteModel(schema);
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

        result.MutationType.Should().Be(MutationType.Update,
            $"{dbName}: UPDATE should remain an UPDATE");
        result.Data.Should().NotContainKey("deleted_at",
            $"{dbName}: UPDATE must not set deleted_at");
        result.AdditionalFilter.Should().NotBeNull(
            $"{dbName}: UPDATE must filter to non-deleted records only");
        result.AdditionalFilter!.ColumnName.Should().Be("deleted_at",
            $"{dbName}: UPDATE filter must target deleted_at column");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteMutation_Insert_NotAffected_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = CreateSoftDeleteModel(schema);
        var table = model.GetTableFromDbName("Users");

        var transformer = new SoftDeleteMutationTransformer();
        var context = new MutationTransformContext
        {
            Model = model,
            UserContext = EmptyUserContext()
        };

        transformer.AppliesTo(table, MutationType.Insert, context).Should().BeFalse(
            $"{dbName}: soft-delete must not apply to INSERT operations");
    }

    #endregion

    #region Soft Delete By Column Population - All Databases

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteMutation_DeleteWithDeletedBy_PopulatesUserColumn_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = CreateSoftDeleteWithDeletedByModel(schema);
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

        result.MutationType.Should().Be(MutationType.Update,
            $"{dbName}: DELETE must be converted to UPDATE");
        result.Data.Should().ContainKey("deleted_at",
            $"{dbName}: deleted_at must be set");
        result.Data.Should().ContainKey("deleted_by",
            $"{dbName}: deleted_by must be populated from user context");
        result.Data["deleted_by"].Should().Be(99,
            $"{dbName}: deleted_by must carry the user ID from context");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteMutation_DeleteWithoutUserContext_SkipsDeletedBy_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = CreateSoftDeleteWithDeletedByModel(schema);
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

        result.MutationType.Should().Be(MutationType.Update,
            $"{dbName}: DELETE must be converted to UPDATE even without user context");
        result.Data.Should().ContainKey("deleted_at",
            $"{dbName}: deleted_at must be set regardless of user context");
        result.Data.Should().NotContainKey("deleted_by",
            $"{dbName}: deleted_by must not be set when user_id is missing from context");
    }

    #endregion

    #region Error Consistency - All Databases

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteFilter_MissingColumn_ThrowsExecutionError_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema(schema)
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
        act.Should().Throw<BifrostExecutionError>(
                $"{dbName}: must throw when soft-delete column does not exist in table")
            .WithMessage("*not found in table*");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteFilter_EmptyColumnMetadata_ReturnsNull_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema(schema)
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

        transformer.AppliesTo(table, context).Should().BeTrue(
            $"{dbName}: transformer should still report AppliesTo=true for empty column metadata");
        var filter = transformer.GetAdditionalFilter(table, context);
        filter.Should().BeNull(
            $"{dbName}: empty column name should result in no filter");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteMutation_MissingColumn_ReturnsError_AcrossAllDatabases(
        ISqlDialect dialect, string schema, string dbName)
    {
        _ = dialect;
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema(schema)
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

        result.Errors.Should().NotBeEmpty(
            $"{dbName}: mutation with missing column must return errors");
        result.Errors[0].Should().Contain("not found in table",
            $"{dbName}: error message must identify the missing column");
    }

    #endregion

    #region Unflagged Tables - No Filter Injection

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void SoftDeleteFilter_DoesNotApplyToUnflaggedTable_AcrossAllDatabases(
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

        var service = CreateTransformerService(new SoftDeleteFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .Build();

        service.ApplyTransformers(query, model, EmptyUserContext());

        query.Filter.Should().BeNull(
            $"{dbName}: soft-delete filter must not inject into tables without soft-delete metadata");
    }

    #endregion

    #region Cross-Database Behavioral Equivalence

    [Fact]
    public void SoftDeleteFilter_AllDialects_ProduceStructurallyEquivalentFilters()
    {
        var dialects = new (ISqlDialect dialect, string schema, string name)[]
        {
            (SqlServerDialect.Instance, "dbo", "SQL Server"),
            (PostgresDialect.Instance, "public", "PostgreSQL"),
            (MySqlDialect.Instance, "mydb", "MySQL"),
            (SqliteDialect.Instance, "main", "SQLite"),
        };

        var transformer = new SoftDeleteFilterTransformer();

        foreach (var (dialect, schema, name) in dialects)
        {
            _ = dialect;
            var model = CreateSoftDeleteModel(schema);
            var table = model.GetTableFromDbName("Users");
            var context = new QueryTransformContext
            {
                Model = model,
                UserContext = EmptyUserContext(),
                QueryType = QueryType.Standard,
                Path = "",
                IsNestedQuery = false
            };

            transformer.AppliesTo(table, context).Should().BeTrue(
                $"{name}: transformer must apply to soft-delete table");

            var filter = transformer.GetAdditionalFilter(table, context);
            filter.Should().NotBeNull($"{name}: filter must not be null");
            filter!.ColumnName.Should().Be("deleted_at",
                $"{name}: filter column must be deleted_at");
            filter.Next.Should().NotBeNull($"{name}: filter must have a value node");
            filter.Next!.Value.Should().BeNull(
                $"{name}: filter value must be null (IS NULL check)");
        }
    }

    [Fact]
    public void SoftDeleteFilter_AllDialects_ProduceSameParameterCount()
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
            var model = CreateSoftDeleteModel(schema);
            var table = model.GetTableFromDbName("Users");

            var service = CreateTransformerService(new SoftDeleteFilterTransformer());
            var query = GqlObjectQueryBuilder.Create()
                .WithDbTable(table)
                .WithColumns("Id", "Name", "Email")
                .Build();

            service.ApplyTransformers(query, model, EmptyUserContext());

            var (_, parameters) = GenerateSql(query, model, dialect);

            if (expectedParamCount == null)
            {
                expectedParamCount = parameters.Parameters.Count;
            }
            else
            {
                parameters.Parameters.Count.Should().Be(expectedParamCount.Value,
                    "all dialects must produce the same number of SQL parameters for equivalent soft-delete queries");
            }
        }
    }

    [Fact]
    public void SoftDeleteMutation_AllDialects_ProduceIdenticalTransformResults()
    {
        var schemas = new (string schema, string name)[]
        {
            ("dbo", "SQL Server"),
            ("public", "PostgreSQL"),
            ("mydb", "MySQL"),
            ("main", "SQLite"),
        };

        MutationTransformResult? firstResult = null;

        foreach (var (schema, name) in schemas)
        {
            var model = CreateSoftDeleteWithDeletedByModel(schema);
            var table = model.GetTableFromDbName("Users");

            var transformer = new SoftDeleteMutationTransformer();
            var context = new MutationTransformContext
            {
                Model = model,
                UserContext = new Dictionary<string, object?> { ["user_id"] = 42 }
            };

            var data = new Dictionary<string, object?> { ["Id"] = 1 };
            var result = transformer.Transform(table, MutationType.Delete, data, context);

            if (firstResult == null)
            {
                firstResult = result;
            }
            else
            {
                result.MutationType.Should().Be(firstResult.MutationType,
                    $"{name}: mutation type must match across all dialects");
                result.Data.Keys.Should().BeEquivalentTo(firstResult.Data.Keys,
                    $"{name}: mutation data keys must match across all dialects");
                result.Data["Id"].Should().Be(firstResult.Data["Id"],
                    $"{name}: original data must be preserved identically");
                result.Data["deleted_by"].Should().Be(firstResult.Data["deleted_by"],
                    $"{name}: deleted_by must be populated identically");
                result.Errors.Should().BeEquivalentTo(firstResult.Errors,
                    $"{name}: error state must match across all dialects");
                result.AdditionalFilter.Should().NotBeNull(
                    $"{name}: additional filter must be present");
                result.AdditionalFilter!.ColumnName.Should().Be(
                    firstResult.AdditionalFilter!.ColumnName,
                    $"{name}: additional filter column must match across all dialects");
            }
        }
    }

    [Fact]
    public void SoftDeleteFilter_AllDialects_IncludeDeletedBypassIsConsistent()
    {
        var dialects = new (ISqlDialect dialect, string schema, string name)[]
        {
            (SqlServerDialect.Instance, "dbo", "SQL Server"),
            (PostgresDialect.Instance, "public", "PostgreSQL"),
            (MySqlDialect.Instance, "mydb", "MySQL"),
            (SqliteDialect.Instance, "main", "SQLite"),
        };

        var transformer = new SoftDeleteFilterTransformer();

        foreach (var (dialect, schema, name) in dialects)
        {
            _ = dialect;
            var model = CreateSoftDeleteModel(schema);
            var table = model.GetTableFromDbName("Users");

            // Global include_deleted
            var globalContext = new QueryTransformContext
            {
                Model = model,
                UserContext = new Dictionary<string, object?> { ["include_deleted"] = true },
                QueryType = QueryType.Standard,
                Path = "",
                IsNestedQuery = false
            };
            transformer.AppliesTo(table, globalContext).Should().BeFalse(
                $"{name}: global include_deleted must bypass soft-delete filter");

            // Table-specific include_deleted
            var tableKey = $"include_deleted:{schema}.Users";
            var tableContext = new QueryTransformContext
            {
                Model = model,
                UserContext = new Dictionary<string, object?> { [tableKey] = true },
                QueryType = QueryType.Standard,
                Path = "",
                IsNestedQuery = false
            };
            transformer.AppliesTo(table, tableContext).Should().BeFalse(
                $"{name}: table-specific include_deleted must bypass soft-delete filter");

            // Without include_deleted
            var normalContext = new QueryTransformContext
            {
                Model = model,
                UserContext = EmptyUserContext(),
                QueryType = QueryType.Standard,
                Path = "",
                IsNestedQuery = false
            };
            transformer.AppliesTo(table, normalContext).Should().BeTrue(
                $"{name}: soft-delete filter must apply when include_deleted is not set");
        }
    }

    #endregion

    #region Helper Methods

    private static IDbModel CreateSoftDeleteModel(string schema)
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema(schema)
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithColumn("Email", "varchar")
                .WithColumn("deleted_at", "datetime", isNullable: true)
                .WithMetadata("soft-delete", "deleted_at"))
            .Build();
    }

    private static IDbModel CreateSoftDeleteWithDeletedByModel(string schema)
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema(schema)
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithColumn("Email", "varchar")
                .WithColumn("deleted_at", "datetime", isNullable: true)
                .WithColumn("deleted_by", "int", isNullable: true)
                .WithMetadata("soft-delete", "deleted_at")
                .WithMetadata("soft-delete-by", "deleted_by"))
            .Build();
    }

    private static IDbModel CreateSoftDeleteWithTenantModel(string schema)
    {
        return DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema(schema)
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("Total", "decimal")
                .WithColumn("deleted_at", "datetime", isNullable: true)
                .WithMetadata("tenant-filter", "tenant_id")
                .WithMetadata("soft-delete", "deleted_at"))
            .Build();
    }

    private static IDictionary<string, object?> EmptyUserContext()
    {
        return new Dictionary<string, object?>();
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
