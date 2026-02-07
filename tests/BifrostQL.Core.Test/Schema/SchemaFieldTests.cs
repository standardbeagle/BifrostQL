using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Schema;

public class SchemaFieldConfigTests
{
    [Fact]
    public void FromMetadata_MissingKey_ReturnsFlatMode()
    {
        var metadata = new Dictionary<string, object?>();

        var config = SchemaFieldConfig.FromMetadata(metadata);

        config.Mode.Should().Be(SchemaDisplayMode.Flat);
    }

    [Fact]
    public void FromMetadata_NullValue_ReturnsFlatMode()
    {
        var metadata = new Dictionary<string, object?> { ["schema-display"] = null };

        var config = SchemaFieldConfig.FromMetadata(metadata);

        config.Mode.Should().Be(SchemaDisplayMode.Flat);
    }

    [Fact]
    public void FromMetadata_UnknownValue_ReturnsFlatMode()
    {
        var metadata = new Dictionary<string, object?> { ["schema-display"] = "nonsense" };

        var config = SchemaFieldConfig.FromMetadata(metadata);

        config.Mode.Should().Be(SchemaDisplayMode.Flat);
    }

    [Fact]
    public void FromMetadata_Flat_ReturnsFlatMode()
    {
        var metadata = new Dictionary<string, object?> { ["schema-display"] = "flat" };

        var config = SchemaFieldConfig.FromMetadata(metadata);

        config.Mode.Should().Be(SchemaDisplayMode.Flat);
    }

    [Fact]
    public void FromMetadata_Prefix_ReturnsPrefixMode()
    {
        var metadata = new Dictionary<string, object?> { ["schema-display"] = "prefix" };

        var config = SchemaFieldConfig.FromMetadata(metadata);

        config.Mode.Should().Be(SchemaDisplayMode.Prefix);
    }

    [Fact]
    public void FromMetadata_Field_ReturnsFieldMode()
    {
        var metadata = new Dictionary<string, object?> { ["schema-display"] = "field" };

        var config = SchemaFieldConfig.FromMetadata(metadata);

        config.Mode.Should().Be(SchemaDisplayMode.Field);
        config.DefaultSchema.Should().Be("dbo");
    }

    [Fact]
    public void FromMetadata_FieldCaseInsensitive_ReturnsFieldMode()
    {
        var metadata = new Dictionary<string, object?> { ["schema-display"] = "FIELD" };

        var config = SchemaFieldConfig.FromMetadata(metadata);

        config.Mode.Should().Be(SchemaDisplayMode.Field);
    }

    [Fact]
    public void FromMetadata_CustomDefaultSchema_Parsed()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["schema-display"] = "field",
            ["schema-default"] = "public",
        };

        var config = SchemaFieldConfig.FromMetadata(metadata);

        config.DefaultSchema.Should().Be("public");
    }

    [Fact]
    public void FromMetadata_ExcludedSchemas_ParsedFromCommaSeparated()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["schema-display"] = "field",
            ["schema-excluded"] = "sys, internal",
        };

        var config = SchemaFieldConfig.FromMetadata(metadata);

        config.ExcludedSchemas.Should().BeEquivalentTo("sys", "internal");
    }

    [Fact]
    public void FromMetadata_Permissions_ParsedFromSemicolonDelimited()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["schema-display"] = "field",
            ["schema-permissions"] = "sales:admin,manager;hr:admin",
        };

        var config = SchemaFieldConfig.FromMetadata(metadata);

        config.Permissions.Should().HaveCount(2);
        config.Permissions[0].SchemaName.Should().Be("sales");
        config.Permissions[0].AllowedRoles.Should().BeEquivalentTo("admin", "manager");
        config.Permissions[1].SchemaName.Should().Be("hr");
        config.Permissions[1].AllowedRoles.Should().BeEquivalentTo("admin");
    }

    [Fact]
    public void FromMetadata_EmptyPermissions_ReturnsEmptyList()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["schema-display"] = "field",
            ["schema-permissions"] = "",
        };

        var config = SchemaFieldConfig.FromMetadata(metadata);

        config.Permissions.Should().BeEmpty();
    }

    [Fact]
    public void Disabled_ReturnsSameInstance()
    {
        var a = SchemaFieldConfig.Disabled;
        var b = SchemaFieldConfig.Disabled;
        a.Should().BeSameAs(b);
    }
}

public class SchemaPermissionTests
{
    [Fact]
    public void IsSchemaAllowed_NoPermissions_ReturnsTrue()
    {
        var config = new SchemaFieldConfig { Mode = SchemaDisplayMode.Field };

        config.IsSchemaAllowed("sales", new[] { "user" }).Should().BeTrue();
    }

    [Fact]
    public void IsSchemaAllowed_ExcludedSchema_ReturnsFalse()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            ExcludedSchemas = new[] { "sys" },
        };

        config.IsSchemaAllowed("sys", new[] { "admin" }).Should().BeFalse();
    }

    [Fact]
    public void IsSchemaAllowed_ExcludedSchemaCaseInsensitive_ReturnsFalse()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            ExcludedSchemas = new[] { "SYS" },
        };

        config.IsSchemaAllowed("sys", new[] { "admin" }).Should().BeFalse();
    }

    [Fact]
    public void IsSchemaAllowed_PermissionWithMatchingRole_ReturnsTrue()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            Permissions = new[]
            {
                new SchemaPermission { SchemaName = "sales", AllowedRoles = new[] { "admin", "sales_user" } },
            },
        };

        config.IsSchemaAllowed("sales", new[] { "sales_user" }).Should().BeTrue();
    }

    [Fact]
    public void IsSchemaAllowed_PermissionWithNoMatchingRole_ReturnsFalse()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            Permissions = new[]
            {
                new SchemaPermission { SchemaName = "hr", AllowedRoles = new[] { "hr_admin" } },
            },
        };

        config.IsSchemaAllowed("hr", new[] { "sales_user" }).Should().BeFalse();
    }

    [Fact]
    public void IsSchemaAllowed_PermissionWithEmptyRoles_GrantsAccess()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            Permissions = new[]
            {
                new SchemaPermission { SchemaName = "public", AllowedRoles = Array.Empty<string>() },
            },
        };

        config.IsSchemaAllowed("public", new[] { "anyone" }).Should().BeTrue();
    }

    [Fact]
    public void IsSchemaAllowed_NoRolesProvided_WithPermission_ReturnsFalse()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            Permissions = new[]
            {
                new SchemaPermission { SchemaName = "hr", AllowedRoles = new[] { "admin" } },
            },
        };

        config.IsSchemaAllowed("hr", null).Should().BeFalse();
        config.IsSchemaAllowed("hr", Array.Empty<string>()).Should().BeFalse();
    }

    [Fact]
    public void IsSchemaAllowed_NullRoles_UnrestrictedSchema_ReturnsTrue()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
        };

        config.IsSchemaAllowed("sales", null).Should().BeTrue();
    }

    [Fact]
    public void IsSchemaAllowed_RoleMatchIsCaseInsensitive()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            Permissions = new[]
            {
                new SchemaPermission { SchemaName = "sales", AllowedRoles = new[] { "Admin" } },
            },
        };

        config.IsSchemaAllowed("sales", new[] { "admin" }).Should().BeTrue();
    }

    [Fact]
    public void IsSchemaExcluded_NotInList_ReturnsFalse()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            ExcludedSchemas = new[] { "sys" },
        };

        config.IsSchemaExcluded("dbo").Should().BeFalse();
    }
}

public class SchemaGroupingTests
{
    [Fact]
    public void GroupTablesBySchema_DefaultSchemaTables_GroupedUnderEmptyKey()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };
        var tables = CreateTables(("Users", "dbo"), ("Orders", "sales"));

        var grouped = config.GroupTablesBySchema(tables);

        grouped.Should().ContainKey("");
        grouped[""].Should().ContainSingle(t => t.DbName == "Users");
    }

    [Fact]
    public void GroupTablesBySchema_NonDefaultSchemaTables_GroupedUnderSchemaName()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };
        var tables = CreateTables(("Users", "dbo"), ("Orders", "sales"), ("Employees", "hr"));

        var grouped = config.GroupTablesBySchema(tables);

        grouped.Should().ContainKey("sales");
        grouped["sales"].Should().ContainSingle(t => t.DbName == "Orders");
        grouped.Should().ContainKey("hr");
        grouped["hr"].Should().ContainSingle(t => t.DbName == "Employees");
    }

    [Fact]
    public void GroupTablesBySchema_ExcludedSchemas_OmittedFromResult()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
            ExcludedSchemas = new[] { "sys" },
        };
        var tables = CreateTables(("Users", "dbo"), ("SysLogs", "sys"));

        var grouped = config.GroupTablesBySchema(tables);

        grouped.Should().NotContainKey("sys");
        grouped.Should().HaveCount(1);
    }

    [Fact]
    public void GroupTablesBySchema_CustomDefaultSchema_UsedCorrectly()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "public",
        };
        var tables = CreateTables(("Users", "public"), ("AuditLog", "dbo"));

        var grouped = config.GroupTablesBySchema(tables);

        grouped[""].Should().ContainSingle(t => t.DbName == "Users");
        grouped["dbo"].Should().ContainSingle(t => t.DbName == "AuditLog");
    }

    [Fact]
    public void GroupTablesBySchema_MultipleTablesInSameSchema_GroupedTogether()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };
        var tables = CreateTables(("Orders", "sales"), ("Products", "sales"), ("Invoices", "sales"));

        var grouped = config.GroupTablesBySchema(tables);

        grouped.Should().ContainKey("sales");
        grouped["sales"].Should().HaveCount(3);
    }

    [Fact]
    public void GroupTablesBySchema_EmptyInput_ReturnsEmptyDictionary()
    {
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };

        var grouped = config.GroupTablesBySchema(Array.Empty<IDbTable>());

        grouped.Should().BeEmpty();
    }

    private static IReadOnlyCollection<IDbTable> CreateTables(params (string name, string schema)[] specs)
    {
        var fixture = DbModelTestFixture.Create();
        foreach (var (name, schema) in specs)
        {
            fixture.WithTable(name, t => t
                .WithSchema(schema)
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"));
        }
        return fixture.Build().Tables;
    }
}

public class SchemaTypeNameTests
{
    [Fact]
    public void GetSchemaQueryTypeName_SimpleSchema()
    {
        SchemaFieldConfig.GetSchemaQueryTypeName("sales").Should().Be("salesSchemaQuery");
    }

    [Fact]
    public void GetSchemaQueryTypeName_SchemaWithSpecialChars()
    {
        SchemaFieldConfig.GetSchemaQueryTypeName("my schema").Should().Be("my_schemaSchemaQuery");
    }

    [Fact]
    public void GetSchemaMutationTypeName_SimpleSchema()
    {
        SchemaFieldConfig.GetSchemaMutationTypeName("hr").Should().Be("hrSchemaInput");
    }
}

public class SchemaFieldSchemaGeneratorTests
{
    [Fact]
    public void FieldMode_DefaultSchemaTables_AppearOnRootQueryType()
    {
        var model = CreateMultiSchemaModel();
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };

        var schema = SchemaFieldSchemaGenerator.SchemaTextFromModel(model, config);

        schema.Should().Contain("type database {");
        schema.Should().Contain("Users(");
    }

    [Fact]
    public void FieldMode_NonDefaultSchemas_AppearAsTopLevelFields()
    {
        var model = CreateMultiSchemaModel();
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };

        var schema = SchemaFieldSchemaGenerator.SchemaTextFromModel(model, config);

        schema.Should().Contain("sales: salesSchemaQuery");
        schema.Should().Contain("hr: hrSchemaQuery");
    }

    [Fact]
    public void FieldMode_SchemaQueryTypes_ContainTheirTables()
    {
        var model = CreateMultiSchemaModel();
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };

        var schema = SchemaFieldSchemaGenerator.SchemaTextFromModel(model, config);

        schema.Should().Contain("type salesSchemaQuery {");
        schema.Should().Contain("type hrSchemaQuery {");
    }

    [Fact]
    public void FieldMode_TablesInSalesSchema_AppearInSalesSchemaQuery()
    {
        var model = CreateMultiSchemaModel();
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };

        var schema = SchemaFieldSchemaGenerator.SchemaTextFromModel(model, config);

        // Orders should appear inside salesSchemaQuery, not in root database type
        var salesTypeStart = schema.IndexOf("type salesSchemaQuery {", StringComparison.Ordinal);
        var salesTypeEnd = schema.IndexOf("}", salesTypeStart);
        var salesTypeBody = schema.Substring(salesTypeStart, salesTypeEnd - salesTypeStart);
        salesTypeBody.Should().Contain("Orders(");
    }

    [Fact]
    public void FieldMode_AllTableTypeDefinitions_StillGenerated()
    {
        var model = CreateMultiSchemaModel();
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };

        var schema = SchemaFieldSchemaGenerator.SchemaTextFromModel(model, config);

        schema.Should().Contain("type Users {");
        schema.Should().Contain("type Orders {");
        schema.Should().Contain("type Employees {");
    }

    [Fact]
    public void FieldMode_DynamicJoins_ReferenceAllTables()
    {
        var model = CreateMultiSchemaModel();
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };

        var schema = SchemaFieldSchemaGenerator.SchemaTextFromModel(model, config);

        // Dynamic join types for each table should reference all tables (cross-schema)
        schema.Should().Contain("type Users_join {");
        schema.Should().Contain("type Orders_join {");
        schema.Should().Contain("type Employees_join {");
    }

    [Fact]
    public void FieldMode_MutationTypes_GroupedBySchema()
    {
        var model = CreateMultiSchemaModel();
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };

        var schema = SchemaFieldSchemaGenerator.SchemaTextFromModel(model, config);

        schema.Should().Contain("type databaseInput {");
        schema.Should().Contain("type salesSchemaInput {");
        schema.Should().Contain("type hrSchemaInput {");
    }

    [Fact]
    public void FieldMode_MutationRootType_ContainsSchemaFields()
    {
        var model = CreateMultiSchemaModel();
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };

        var schema = SchemaFieldSchemaGenerator.SchemaTextFromModel(model, config);

        // Root mutation should contain schema fields
        schema.Should().Contain("sales: salesSchemaInput");
        schema.Should().Contain("hr: hrSchemaInput");
    }

    [Fact]
    public void FieldMode_ExcludedSchemas_NotGenerated()
    {
        var model = CreateMultiSchemaModel();
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
            ExcludedSchemas = new[] { "hr" },
        };

        var schema = SchemaFieldSchemaGenerator.SchemaTextFromModel(model, config);

        schema.Should().NotContain("hrSchemaQuery");
        schema.Should().NotContain("hrSchemaInput");
        schema.Should().Contain("salesSchemaQuery");
    }

    [Fact]
    public void FieldMode_PagedTypes_GeneratedForAllTables()
    {
        var model = CreateMultiSchemaModel();
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };

        var schema = SchemaFieldSchemaGenerator.SchemaTextFromModel(model, config);

        schema.Should().Contain("type Users_paged {");
        schema.Should().Contain("type Orders_paged {");
        schema.Should().Contain("type Employees_paged {");
    }

    [Fact]
    public void FieldMode_FilterTypes_GeneratedForAllTables()
    {
        var model = CreateMultiSchemaModel();
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };

        var schema = SchemaFieldSchemaGenerator.SchemaTextFromModel(model, config);

        schema.Should().Contain("input TableFilterUsersInput {");
        schema.Should().Contain("input TableFilterOrdersInput {");
        schema.Should().Contain("input TableFilterEmployeesInput {");
    }

    [Fact]
    public void FieldMode_MetadataSchemaTypes_Generated()
    {
        var model = CreateMultiSchemaModel();
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };

        var schema = SchemaFieldSchemaGenerator.SchemaTextFromModel(model, config);

        schema.Should().Contain("type dbTableSchema {");
        schema.Should().Contain("type dbColumnSchema {");
        schema.Should().Contain("type dbMetadataSchema {");
    }

    [Fact]
    public void FieldMode_DbSchemaField_OnRootQueryType()
    {
        var model = CreateMultiSchemaModel();
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };

        var schema = SchemaFieldSchemaGenerator.SchemaTextFromModel(model, config);

        schema.Should().Contain("_dbSchema(graphQlName: String): [dbTableSchema!]!");
    }

    [Fact]
    public void FieldMode_NoDynamicJoins_StillWorks()
    {
        var model = CreateMultiSchemaModel();
        var config = new SchemaFieldConfig
        {
            Mode = SchemaDisplayMode.Field,
            DefaultSchema = "dbo",
        };

        var schema = SchemaFieldSchemaGenerator.SchemaTextFromModel(model, config, includeDynamicJoins: false);

        schema.Should().NotContain("type Users_join {");
        schema.Should().Contain("type Users {");
    }

    private static IDbModel CreateMultiSchemaModel()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("sales")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithColumn("Status", "nvarchar"))
            .WithTable("Employees", t => t
                .WithSchema("hr")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Salary", "decimal"))
            .Build();
    }
}

public class SchemaFieldBackwardCompatibilityTests
{
    private static readonly System.Reflection.MethodInfo SchemaTextFromModelMethod =
        typeof(SchemaFieldSchemaGenerator).Assembly
            .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
            .GetMethod("SchemaTextFromModel", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!;

    [Fact]
    public void FlatMode_OriginalSchemaGenerator_Unchanged()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("sales")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal"))
            .Build();

        var schema = (string)SchemaTextFromModelMethod.Invoke(null, new object[] { model, true })!;

        schema.Should().Contain("type database {");
        schema.Should().Contain("Users(");
        schema.Should().Contain("Orders(");
        schema.Should().NotContain("SchemaQuery");
    }

    [Fact]
    public void PrefixMode_StillWorksWithSchemaPrefixOptions()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("schema-prefix", "enabled")
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("sales")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal"))
            .WithForeignKey("FK_dummy", "dbo", "Users", new[] { "Id" }, "dbo", "Users", new[] { "Id" })
            .Build();

        model.GetTableFromDbName("Users").GraphQlName.Should().Be("Users");
        model.GetTableFromDbName("Orders").GraphQlName.Should().Be("sales_Orders");
    }
}
