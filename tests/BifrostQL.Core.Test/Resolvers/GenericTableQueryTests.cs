using System.Reflection;
using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Resolvers;

public sealed class GenericTableQueryTests
{
    private static readonly MethodInfo SchemaTextFromModelMethod = typeof(DbSchema).Assembly
        .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
        .GetMethod("SchemaTextFromModel", BindingFlags.Static | BindingFlags.Public)!;

    private static readonly MethodInfo IsGenericTableEnabledMethod = typeof(DbSchema).Assembly
        .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
        .GetMethod("IsGenericTableEnabled", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly MethodInfo GetGenericTableTypesMethod = typeof(DbSchema).Assembly
        .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
        .GetMethod("GetGenericTableTypes", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static string GetSchemaText(IDbModel model)
        => (string)SchemaTextFromModelMethod.Invoke(null, new object[] { model, true })!;

    private static bool InvokeIsGenericTableEnabled(IDbModel model)
        => (bool)IsGenericTableEnabledMethod.Invoke(null, new object[] { model })!;

    private static string InvokeGetGenericTableTypes()
        => (string)GetGenericTableTypesMethod.Invoke(null, Array.Empty<object>())!;

    #region GenericTableConfig Tests

    [Fact]
    public void Config_FromModel_WhenDisabled_ReturnsDisabledConfig()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();

        var config = GenericTableConfig.FromModel(model);

        config.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Config_FromModel_WhenEnabled_ReturnsEnabledConfig()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();

        var config = GenericTableConfig.FromModel(model);

        config.Enabled.Should().BeTrue();
        config.RequiredRole.Should().Be(GenericTableConfig.DefaultRequiredRole);
        config.MaxRows.Should().Be(GenericTableConfig.DefaultMaxRows);
        config.AllowedTables.Should().BeNull();
        config.DeniedTables.Should().BeNull();
    }

    [Fact]
    public void Config_FromModel_WithCustomRole_UsesCustomRole()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithModelMetadata("generic-table-role", "super-admin")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();

        var config = GenericTableConfig.FromModel(model);

        config.RequiredRole.Should().Be("super-admin");
    }

    [Fact]
    public void Config_FromModel_WithCustomMaxRows_UsesCustomMaxRows()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithModelMetadata("generic-table-max-rows", "500")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();

        var config = GenericTableConfig.FromModel(model);

        config.MaxRows.Should().Be(500);
    }

    [Fact]
    public void Config_FromModel_WithInvalidMaxRows_UsesDefault()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithModelMetadata("generic-table-max-rows", "not-a-number")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();

        var config = GenericTableConfig.FromModel(model);

        config.MaxRows.Should().Be(GenericTableConfig.DefaultMaxRows);
    }

    [Fact]
    public void Config_FromModel_WithZeroMaxRows_UsesDefault()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithModelMetadata("generic-table-max-rows", "0")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();

        var config = GenericTableConfig.FromModel(model);

        config.MaxRows.Should().Be(GenericTableConfig.DefaultMaxRows);
    }

    [Fact]
    public void Config_FromModel_WithAllowedTables_ParsesList()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithModelMetadata("generic-table-allowed", "Users, Orders, Products")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();

        var config = GenericTableConfig.FromModel(model);

        config.AllowedTables.Should().BeEquivalentTo(new[] { "Users", "Orders", "Products" });
    }

    [Fact]
    public void Config_FromModel_WithDeniedTables_ParsesList()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithModelMetadata("generic-table-denied", "AuditLog, SystemConfig")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();

        var config = GenericTableConfig.FromModel(model);

        config.DeniedTables.Should().BeEquivalentTo(new[] { "AuditLog", "SystemConfig" });
    }

    #endregion

    #region IsTableAllowed Tests

    [Fact]
    public void IsTableAllowed_WhenDisabled_ReturnsFalse()
    {
        var config = new GenericTableConfig { Enabled = false };

        config.IsTableAllowed("Users").Should().BeFalse();
    }

    [Fact]
    public void IsTableAllowed_WhenEnabledNoRestrictions_ReturnsTrue()
    {
        var config = new GenericTableConfig { Enabled = true };

        config.IsTableAllowed("Users").Should().BeTrue();
    }

    [Fact]
    public void IsTableAllowed_WhenDenied_ReturnsFalse()
    {
        var config = new GenericTableConfig
        {
            Enabled = true,
            DeniedTables = new[] { "AuditLog" },
        };

        config.IsTableAllowed("AuditLog").Should().BeFalse();
    }

    [Fact]
    public void IsTableAllowed_WhenDeniedCaseInsensitive_ReturnsFalse()
    {
        var config = new GenericTableConfig
        {
            Enabled = true,
            DeniedTables = new[] { "AuditLog" },
        };

        config.IsTableAllowed("auditlog").Should().BeFalse();
    }

    [Fact]
    public void IsTableAllowed_WhenNotInDenied_ReturnsTrue()
    {
        var config = new GenericTableConfig
        {
            Enabled = true,
            DeniedTables = new[] { "AuditLog" },
        };

        config.IsTableAllowed("Users").Should().BeTrue();
    }

    [Fact]
    public void IsTableAllowed_WhenInAllowedList_ReturnsTrue()
    {
        var config = new GenericTableConfig
        {
            Enabled = true,
            AllowedTables = new[] { "Users", "Orders" },
        };

        config.IsTableAllowed("Users").Should().BeTrue();
    }

    [Fact]
    public void IsTableAllowed_WhenNotInAllowedList_ReturnsFalse()
    {
        var config = new GenericTableConfig
        {
            Enabled = true,
            AllowedTables = new[] { "Users", "Orders" },
        };

        config.IsTableAllowed("Products").Should().BeFalse();
    }

    [Fact]
    public void IsTableAllowed_WhenAllowedCaseInsensitive_ReturnsTrue()
    {
        var config = new GenericTableConfig
        {
            Enabled = true,
            AllowedTables = new[] { "Users" },
        };

        config.IsTableAllowed("users").Should().BeTrue();
    }

    [Fact]
    public void IsTableAllowed_DeniedTakesPrecedenceOverAllowed()
    {
        var config = new GenericTableConfig
        {
            Enabled = true,
            AllowedTables = new[] { "Users", "AuditLog" },
            DeniedTables = new[] { "AuditLog" },
        };

        config.IsTableAllowed("AuditLog").Should().BeFalse();
    }

    #endregion

    #region ResolveTable Tests

    [Fact]
    public void ResolveTable_ValidTable_ReturnsTable()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();
        var config = GenericTableConfig.FromModel(model);
        var resolver = new GenericTableQueryResolver(model, config);

        var table = resolver.ResolveTable("Users");

        table.GraphQlName.Should().Be("Users");
    }

    [Fact]
    public void ResolveTable_DisabledTable_ThrowsExecutionError()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithModelMetadata("generic-table-denied", "Users")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();
        var config = GenericTableConfig.FromModel(model);
        var resolver = new GenericTableQueryResolver(model, config);

        var act = () => resolver.ResolveTable("Users");

        act.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>().WithMessage("*not allowed*");
    }

    [Fact]
    public void ResolveTable_NonExistentTable_ThrowsExecutionError()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();
        var config = GenericTableConfig.FromModel(model);
        var resolver = new GenericTableQueryResolver(model, config);

        var act = () => resolver.ResolveTable("NonExistent");

        act.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>().WithMessage("*does not exist*");
    }

    [Fact]
    public void ResolveTable_NotInAllowedList_ThrowsExecutionError()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithModelMetadata("generic-table-allowed", "Orders")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .WithTable("Orders", t => t.WithPrimaryKey("Id"))
            .Build();
        var config = GenericTableConfig.FromModel(model);
        var resolver = new GenericTableQueryResolver(model, config);

        var act = () => resolver.ResolveTable("Users");

        act.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>().WithMessage("*not allowed*");
    }

    #endregion

    #region Authorization Tests

    [Fact]
    public void ValidateAuthorization_NoUser_ThrowsExecutionError()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();
        var config = GenericTableConfig.FromModel(model);
        var resolver = new GenericTableQueryResolver(model, config);

        var userContext = new Dictionary<string, object?>();
        var act = () => resolver.ValidateAuthorization(userContext);

        act.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>().WithMessage("*Authentication required*");
    }

    [Fact]
    public void ValidateAuthorization_NonClaimsPrincipal_ThrowsExecutionError()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();
        var config = GenericTableConfig.FromModel(model);
        var resolver = new GenericTableQueryResolver(model, config);

        var userContext = new Dictionary<string, object?> { ["user"] = "not-a-principal" };
        var act = () => resolver.ValidateAuthorization(userContext);

        act.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>().WithMessage("*Authentication required*");
    }

    [Fact]
    public void ValidateAuthorization_MissingRole_ThrowsExecutionError()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();
        var config = GenericTableConfig.FromModel(model);
        var resolver = new GenericTableQueryResolver(model, config);

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "testuser") }, "test");
        var principal = new ClaimsPrincipal(identity);
        var userContext = new Dictionary<string, object?> { ["user"] = principal };

        var act = () => resolver.ValidateAuthorization(userContext);

        act.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>().WithMessage("*does not have the required role*");
    }

    [Fact]
    public void ValidateAuthorization_WithRoleClaim_Succeeds()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();
        var config = GenericTableConfig.FromModel(model);
        var resolver = new GenericTableQueryResolver(model, config);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "testuser"),
            new Claim("role", "bifrost-admin"),
        }, "test");
        var principal = new ClaimsPrincipal(identity);
        var userContext = new Dictionary<string, object?> { ["user"] = principal };

        var act = () => resolver.ValidateAuthorization(userContext);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateAuthorization_WithCustomRole_Succeeds()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithModelMetadata("generic-table-role", "super-admin")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();
        var config = GenericTableConfig.FromModel(model);
        var resolver = new GenericTableQueryResolver(model, config);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "testuser"),
            new Claim("role", "super-admin"),
        }, "test");
        var principal = new ClaimsPrincipal(identity);
        var userContext = new Dictionary<string, object?> { ["user"] = principal };

        var act = () => resolver.ValidateAuthorization(userContext);

        act.Should().NotThrow();
    }

    #endregion

    #region ExtractColumnMetadata Tests

    [Fact]
    public void ExtractColumnMetadata_ReturnsAllColumns()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar", isNullable: true)
                .WithColumn("Age", "int"))
            .Build();
        var table = model.GetTableByFullGraphQlName("Users");

        var metadata = GenericTableQueryResolver.ExtractColumnMetadata(table);

        metadata.Should().HaveCount(4);

        var idCol = metadata.First(c => c.Name == "Id");
        idCol.DataType.Should().Be("int");
        idCol.IsPrimaryKey.Should().BeTrue();
        idCol.IsNullable.Should().BeFalse();

        var emailCol = metadata.First(c => c.Name == "Email");
        emailCol.IsNullable.Should().BeTrue();
        emailCol.IsPrimaryKey.Should().BeFalse();
    }

    #endregion

    #region BuildWhereClause Tests

    [Fact]
    public void BuildWhereClause_NullFilter_ReturnsEmpty()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableByFullGraphQlName("Users");

        var (whereSql, parameters) = GenericTableQueryResolver.BuildWhereClause(table, SqlServerDialect.Instance, null);

        whereSql.Should().BeEmpty();
        parameters.Should().BeEmpty();
    }

    [Fact]
    public void BuildWhereClause_EmptyFilter_ReturnsEmpty()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableByFullGraphQlName("Users");

        var (whereSql, parameters) = GenericTableQueryResolver.BuildWhereClause(table, SqlServerDialect.Instance, new Dictionary<string, object?>());

        whereSql.Should().BeEmpty();
        parameters.Should().BeEmpty();
    }

    [Fact]
    public void BuildWhereClause_EqFilter_GeneratesCorrectSql()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableByFullGraphQlName("Users");
        var filter = new Dictionary<string, object?>
        {
            ["Name"] = new Dictionary<string, object?> { ["_eq"] = "John" },
        };

        var (whereSql, parameters) = GenericTableQueryResolver.BuildWhereClause(table, SqlServerDialect.Instance, filter);

        whereSql.Should().Contain("WHERE");
        whereSql.Should().Contain("[Name] = @gp0");
        parameters.Should().HaveCount(1);
        parameters[0].name.Should().Be("@gp0");
        parameters[0].value.Should().Be("John");
    }

    [Fact]
    public void BuildWhereClause_LikeFilter_GeneratesCorrectSql()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableByFullGraphQlName("Users");
        var filter = new Dictionary<string, object?>
        {
            ["Name"] = new Dictionary<string, object?> { ["_like"] = "%John%" },
        };

        var (whereSql, parameters) = GenericTableQueryResolver.BuildWhereClause(table, SqlServerDialect.Instance, filter);

        whereSql.Should().Contain("[Name] LIKE @gp0");
        parameters[0].value.Should().Be("%John%");
    }

    [Fact]
    public void BuildWhereClause_MultipleFilters_CombinesWithAnd()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Age", "int"))
            .Build();
        var table = model.GetTableByFullGraphQlName("Users");
        var filter = new Dictionary<string, object?>
        {
            ["Name"] = new Dictionary<string, object?> { ["_eq"] = "John" },
            ["Age"] = new Dictionary<string, object?> { ["_gt"] = 18 },
        };

        var (whereSql, parameters) = GenericTableQueryResolver.BuildWhereClause(table, SqlServerDialect.Instance, filter);

        whereSql.Should().Contain("AND");
        parameters.Should().HaveCount(2);
    }

    [Fact]
    public void BuildWhereClause_ComparisonOperators_GenerateCorrectSql()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Age", "int"))
            .Build();
        var table = model.GetTableByFullGraphQlName("Users");
        var filter = new Dictionary<string, object?>
        {
            ["Age"] = new Dictionary<string, object?>
            {
                ["_gte"] = 18,
                ["_lt"] = 65,
            },
        };

        var (whereSql, parameters) = GenericTableQueryResolver.BuildWhereClause(table, SqlServerDialect.Instance, filter);

        whereSql.Should().Contain("[Age] >= @gp0");
        whereSql.Should().Contain("[Age] < @gp1");
        parameters.Should().HaveCount(2);
    }

    [Fact]
    public void BuildWhereClause_UnknownColumn_IsIgnored()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableByFullGraphQlName("Users");
        var filter = new Dictionary<string, object?>
        {
            ["NonExistentColumn"] = new Dictionary<string, object?> { ["_eq"] = "value" },
        };

        var (whereSql, parameters) = GenericTableQueryResolver.BuildWhereClause(table, SqlServerDialect.Instance, filter);

        whereSql.Should().BeEmpty();
        parameters.Should().BeEmpty();
    }

    [Fact]
    public void BuildWhereClause_UnknownOperator_IsIgnored()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableByFullGraphQlName("Users");
        var filter = new Dictionary<string, object?>
        {
            ["Name"] = new Dictionary<string, object?> { ["_unknown_op"] = "value" },
        };

        var (whereSql, parameters) = GenericTableQueryResolver.BuildWhereClause(table, SqlServerDialect.Instance, filter);

        whereSql.Should().BeEmpty();
        parameters.Should().BeEmpty();
    }

    [Fact]
    public void BuildWhereClause_NullValue_UsesDbNull()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar", isNullable: true))
            .Build();
        var table = model.GetTableByFullGraphQlName("Users");
        var filter = new Dictionary<string, object?>
        {
            ["Name"] = new Dictionary<string, object?> { ["_eq"] = null },
        };

        var (whereSql, parameters) = GenericTableQueryResolver.BuildWhereClause(table, SqlServerDialect.Instance, filter);

        whereSql.Should().Contain("[Name] = @gp0");
        parameters[0].value.Should().Be(DBNull.Value);
    }

    [Fact]
    public void BuildWhereClause_NonDictionaryFilterValue_IsIgnored()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableByFullGraphQlName("Users");
        var filter = new Dictionary<string, object?>
        {
            ["Name"] = "not-a-dictionary",
        };

        var (whereSql, parameters) = GenericTableQueryResolver.BuildWhereClause(table, SqlServerDialect.Instance, filter);

        whereSql.Should().BeEmpty();
        parameters.Should().BeEmpty();
    }

    #endregion

    #region Schema Generation Tests

    [Fact]
    public void IsGenericTableEnabled_WhenNotSet_ReturnsFalse()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();

        InvokeIsGenericTableEnabled(model).Should().BeFalse();
    }

    [Fact]
    public void IsGenericTableEnabled_WhenEnabled_ReturnsTrue()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();

        InvokeIsGenericTableEnabled(model).Should().BeTrue();
    }

    [Fact]
    public void IsGenericTableEnabled_CaseInsensitive()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "Enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();

        InvokeIsGenericTableEnabled(model).Should().BeTrue();
    }

    [Fact]
    public void SchemaText_WhenDisabled_DoesNotContainTableField()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();

        var schema = GetSchemaText(model);

        schema.Should().NotContain("_table(");
        schema.Should().NotContain("GenericTableResult");
    }

    [Fact]
    public void SchemaText_WhenEnabled_ContainsTableField()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();

        var schema = GetSchemaText(model);

        schema.Should().Contain("_table(name: String!, limit: Int, offset: Int, filter: JSON): GenericTableResult!");
    }

    [Fact]
    public void SchemaText_WhenEnabled_ContainsGenericTableResultType()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();

        var schema = GetSchemaText(model);

        schema.Should().Contain("type GenericTableResult {");
        schema.Should().Contain("tableName: String!");
        schema.Should().Contain("columns: [GenericColumnMetadata!]!");
        schema.Should().Contain("rows: [JSON]!");
        schema.Should().Contain("totalCount: Int!");
    }

    [Fact]
    public void SchemaText_WhenEnabled_ContainsGenericColumnMetadataType()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();

        var schema = GetSchemaText(model);

        schema.Should().Contain("type GenericColumnMetadata {");
        schema.Should().Contain("name: String!");
        schema.Should().Contain("dataType: String!");
        schema.Should().Contain("isNullable: Boolean!");
        schema.Should().Contain("isPrimaryKey: Boolean!");
    }

    [Fact]
    public void GetGenericTableTypes_ContainsExpectedTypes()
    {
        var types = InvokeGetGenericTableTypes();

        types.Should().Contain("type GenericTableResult");
        types.Should().Contain("type GenericColumnMetadata");
    }

    [Fact]
    public void SchemaFromModel_WhenEnabled_BuildsWithoutErrors()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();

        var act = () => DbSchema.FromModel(model);

        act.Should().NotThrow();
    }

    #endregion

    #region GenericTableResult Tests

    [Fact]
    public void GenericTableResult_DefaultValues_AreEmpty()
    {
        var result = new GenericTableResult
        {
            TableName = "Users",
        };

        result.TableName.Should().Be("Users");
        result.Columns.Should().BeEmpty();
        result.Rows.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public void GenericColumnMetadata_StoresAllProperties()
    {
        var col = new GenericColumnMetadata
        {
            Name = "Id",
            DataType = "int",
            IsNullable = false,
            IsPrimaryKey = true,
        };

        col.Name.Should().Be("Id");
        col.DataType.Should().Be("int");
        col.IsNullable.Should().BeFalse();
        col.IsPrimaryKey.Should().BeTrue();
    }

    #endregion

    #region NeqFilter Tests

    [Fact]
    public void BuildWhereClause_NeqFilter_GeneratesCorrectSql()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Status", "nvarchar"))
            .Build();
        var table = model.GetTableByFullGraphQlName("Users");
        var filter = new Dictionary<string, object?>
        {
            ["Status"] = new Dictionary<string, object?> { ["_neq"] = "inactive" },
        };

        var (whereSql, parameters) = GenericTableQueryResolver.BuildWhereClause(table, SqlServerDialect.Instance, filter);

        whereSql.Should().Contain("[Status] <> @gp0");
        parameters[0].value.Should().Be("inactive");
    }

    [Fact]
    public void BuildWhereClause_LteFilter_GeneratesCorrectSql()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Age", "int"))
            .Build();
        var table = model.GetTableByFullGraphQlName("Users");
        var filter = new Dictionary<string, object?>
        {
            ["Age"] = new Dictionary<string, object?> { ["_lte"] = 100 },
        };

        var (whereSql, parameters) = GenericTableQueryResolver.BuildWhereClause(table, SqlServerDialect.Instance, filter);

        whereSql.Should().Contain("[Age] <= @gp0");
        parameters[0].value.Should().Be(100);
    }

    #endregion
}
