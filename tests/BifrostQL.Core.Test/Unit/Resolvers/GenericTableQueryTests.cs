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
        => (string)SchemaTextFromModelMethod.Invoke(null, new object[] { model })!;

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
    public void Config_FromModel_WithInvalidMaxRows_Throws()
    {
        // A non-numeric max-rows is an operator typo — fail rather than silently
        // capping at the default.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithModelMetadata("generic-table-max-rows", "not-a-number")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();

        var act = () => GenericTableConfig.FromModel(model);

        act.Should().Throw<InvalidOperationException>().WithMessage("*positive integer*");
    }

    [Fact]
    public void Config_FromModel_WithZeroMaxRows_Throws()
    {
        // Zero rows is nonsensical config; treat it as invalid rather than a
        // silent "use the default" sentinel.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithModelMetadata("generic-table-max-rows", "0")
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();

        var act = () => GenericTableConfig.FromModel(model);

        act.Should().Throw<InvalidOperationException>();
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

    #region BuildFilter Tests

    // The rendered WHERE of BuildFilter's TableFilter, qualified by the table name the
    // resolver passes, plus the bound parameters.
    private static (string Where, IReadOnlyList<SqlParameterInfo> Parameters) RenderFilter(
        IDbModel model, string tableName, Dictionary<string, object?>? filter)
    {
        var table = model.GetTableByFullGraphQlName(tableName);
        var tableFilter = GenericTableQueryResolver.BuildFilter(table, filter);
        if (tableFilter == null)
            return ("", Array.Empty<SqlParameterInfo>());
        var parameters = new SqlParameterCollection();
        var parts = tableFilter.RenderParts(model, SqlServerDialect.Instance, parameters, table.DbName);
        parts.Joins.Should().BeEmpty("a _table filter is flat: it never joins another table");
        return (parts.Where, parameters.Parameters);
    }

    private static IDbModel UsersModel() => DbModelTestFixture.Create()
        .WithTable("Users", t => t
            .WithPrimaryKey("Id")
            .WithColumn("Name", "nvarchar", isNullable: true)
            .WithColumn("Age", "int"))
        .Build();

    [Fact]
    public void BuildFilter_NullOrEmptyFilter_ReturnsNull()
    {
        var table = UsersModel().GetTableByFullGraphQlName("Users");

        GenericTableQueryResolver.BuildFilter(table, null).Should().BeNull();
        GenericTableQueryResolver.BuildFilter(table, new Dictionary<string, object?>()).Should().BeNull();
    }

    [Theory]
    [InlineData("_eq", "John", "[Users].[Name] = @p0")]
    [InlineData("_neq", "John", "[Users].[Name] != @p0")]
    [InlineData("_like", "%John%", "[Users].[Name] LIKE @p0")]
    public void BuildFilter_ComparisonOnOneColumn_BindsTheValue(string op, string value, string expected)
    {
        var (where, parameters) = RenderFilter(UsersModel(), "Users", new Dictionary<string, object?>
        {
            ["Name"] = new Dictionary<string, object?> { [op] = value },
        });

        where.Should().Be(expected);
        parameters.Select(p => p.Value).Should().Equal(value);
    }

    [Fact]
    public void BuildFilter_TwoOperatorsOnOneColumn_AreAnded()
    {
        var (where, parameters) = RenderFilter(UsersModel(), "Users", new Dictionary<string, object?>
        {
            ["Age"] = new Dictionary<string, object?> { ["_gte"] = 18, ["_lt"] = 65 },
        });

        where.Should().Be("(([Users].[Age] >= @p0) AND ([Users].[Age] < @p1))");
        parameters.Select(p => p.Value).Should().Equal(18, 65);
    }

    [Fact]
    public void BuildFilter_TwoColumns_AreAnded()
    {
        var (where, parameters) = RenderFilter(UsersModel(), "Users", new Dictionary<string, object?>
        {
            ["Name"] = new Dictionary<string, object?> { ["_eq"] = "John" },
            ["Age"] = new Dictionary<string, object?> { ["_gt"] = 18 },
        });

        where.Should().Be("(([Users].[Name] = @p0) AND ([Users].[Age] > @p1))");
        parameters.Select(p => p.Value).Should().Equal("John", 18);
    }

    [Theory]
    [InlineData("_eq", "[Users].[Name] IS NULL")]
    [InlineData("_neq", "[Users].[Name] IS NOT NULL")]
    public void BuildFilter_NullValue_RendersANullTest(string op, string expected)
    {
        // `col = NULL` matches no row on any engine; the shared renderer tests for NULL.
        var (where, parameters) = RenderFilter(UsersModel(), "Users", new Dictionary<string, object?>
        {
            ["Name"] = new Dictionary<string, object?> { [op] = null },
        });

        where.Should().Be(expected);
        parameters.Should().BeEmpty();
    }

    [Fact]
    public void BuildFilter_ColumnKeyIsCaseInsensitive_RendersTheColumnsName()
    {
        var (where, _) = RenderFilter(UsersModel(), "Users", new Dictionary<string, object?>
        {
            ["name"] = new Dictionary<string, object?> { ["_eq"] = "John" },
        });

        where.Should().Be("[Users].[Name] = @p0");
    }

    [Fact]
    public void BuildFilter_UnknownColumn_Throws()
    {
        // Silently dropping the predicate would return rows the caller filtered
        // out — an unknown column must fail, not widen the result set.
        var act = () => RenderFilter(UsersModel(), "Users", new Dictionary<string, object?>
        {
            ["NonExistentColumn"] = new Dictionary<string, object?> { ["_eq"] = "value" },
        });

        act.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>()
            .WithMessage("*unknown column*");
    }

    [Fact]
    public void BuildFilter_UnknownOperator_Throws()
    {
        var act = () => RenderFilter(UsersModel(), "Users", new Dictionary<string, object?>
        {
            ["Name"] = new Dictionary<string, object?> { ["_unknown_op"] = "value" },
        });

        act.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>()
            .WithMessage("*Unknown filter operator*");
    }

    [Theory]
    [InlineData("scalar")]
    [InlineData("empty")]
    public void BuildFilter_MalformedFilterShape_Throws(string shape)
    {
        // A scalar or an empty operator object where operators are expected must fail
        // rather than be silently skipped (which would drop the intended predicate).
        object? entry = shape == "scalar" ? "John" : new Dictionary<string, object?>();
        var act = () => RenderFilter(UsersModel(), "Users", new Dictionary<string, object?> { ["Name"] = entry });

        act.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>()
            .WithMessage("*must be an object of operators*");
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
}
