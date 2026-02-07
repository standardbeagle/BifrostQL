using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

public class SchemaPrefixTests
{
    [Fact]
    public void Disabled_ByDefault_DoesNotPrefixNonDboTables()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("sales")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal"))
            .Build();

        var orders = model.GetTableFromDbName("Orders");
        orders.GraphQlName.Should().Be("Orders");
    }

    [Fact]
    public void Disabled_ExplicitMetadata_DoesNotPrefixNonDboTables()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("schema-prefix", "disabled")
            .WithTable("Orders", t => t
                .WithSchema("sales")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal"))
            .WithForeignKey("FK_dummy", "sales", "Orders", new[] { "Id" }, "sales", "Orders", new[] { "Id" })
            .Build();

        var orders = model.GetTableFromDbName("Orders");
        orders.GraphQlName.Should().Be("Orders");
    }

    [Fact]
    public void Enabled_DefaultSchemaDbo_DoesNotPrefixDboTables()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Users", "dbo"), ("Orders", "sales"));

        var users = model.GetTableFromDbName("Users");
        users.GraphQlName.Should().Be("Users");
    }

    [Fact]
    public void Enabled_DefaultSchemaDbo_PrefixesNonDboTablesWithUnderscore()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Users", "dbo"), ("Orders", "sales"));

        var orders = model.GetTableFromDbName("Orders");
        orders.GraphQlName.Should().Be("sales_Orders");
    }

    [Fact]
    public void Enabled_CamelCaseFormat_PrefixesNonDboTables()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.CamelCase,
            ("Users", "dbo"), ("Orders", "sales"));

        var orders = model.GetTableFromDbName("Orders");
        orders.GraphQlName.Should().Be("salesOrders");
    }

    [Fact]
    public void Enabled_CustomDefaultSchema_DoesNotPrefixDefaultSchemaTables()
    {
        var model = CreateModelWithSchemaPrefix("sales", SchemaPrefixFormat.Underscore,
            ("Orders", "sales"), ("AuditLog", "dbo"));

        var orders = model.GetTableFromDbName("Orders");
        orders.GraphQlName.Should().Be("Orders");

        var auditLog = model.GetTableFromDbName("AuditLog");
        auditLog.GraphQlName.Should().Be("dbo_AuditLog");
    }

    [Fact]
    public void Enabled_DerivedTypeNames_UsePrefixedGraphQlName()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Users", "dbo"), ("Orders", "sales"));

        var orders = model.GetTableFromDbName("Orders");
        orders.ColumnEnumTypeName.Should().Be("sales_OrdersEnum");
        orders.ColumnFilterTypeName.Should().Be("FilterTypesales_OrdersEnumInput");
        orders.TableFilterTypeName.Should().Be("TableFiltersales_OrdersInput");
        orders.TableColumnSortEnumName.Should().Be("sales_OrdersSortEnum");
        orders.JoinFieldName.Should().Be("_join_sales_Orders");
        orders.SingleFieldName.Should().Be("_single_sales_Orders");
        orders.AggregateValueTypeName.Should().Be("sales_Orders_AggregateValue");
        orders.GetActionTypeName(MutateActions.Insert).Should().Be("Insert_sales_Orders");
    }

    [Fact]
    public void Enabled_DboTable_DerivedTypeNamesUnchanged()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Users", "dbo"), ("Orders", "sales"));

        var users = model.GetTableFromDbName("Users");
        users.ColumnEnumTypeName.Should().Be("UsersEnum");
        users.TableFilterTypeName.Should().Be("TableFilterUsersInput");
    }

    [Fact]
    public void Enabled_DbNameAndTableSchema_Unchanged()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Orders", "sales"));

        var orders = model.GetTableFromDbName("Orders");
        orders.DbName.Should().Be("Orders");
        orders.TableSchema.Should().Be("sales");
    }

    [Fact]
    public void Enabled_DbTableRef_UnchangedBySchemaPrefixing()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Orders", "sales"));

        var orders = model.GetTableFromDbName("Orders");
        orders.DbTableRef.Should().Be("[sales].[Orders]");
    }

    [Fact]
    public void Enabled_NormalizedName_UnchangedBySchemaPrefixing()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Orders", "sales"));

        var orders = model.GetTableFromDbName("Orders");
        orders.NormalizedName.Should().Be("Order");
    }

    [Fact]
    public void Enabled_DefaultSchemaComparison_IsCaseInsensitive()
    {
        var model = CreateModelWithSchemaPrefix("DBO", SchemaPrefixFormat.Underscore,
            ("Users", "dbo"), ("Orders", "sales"));

        var users = model.GetTableFromDbName("Users");
        users.GraphQlName.Should().Be("Users");

        var orders = model.GetTableFromDbName("Orders");
        orders.GraphQlName.Should().Be("sales_Orders");
    }

    [Fact]
    public void FromMetadata_EnabledWithDefaults_ParsesCorrectly()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["schema-prefix"] = "enabled"
        };

        var options = SchemaPrefixOptions.FromMetadata(metadata);
        options.Enabled.Should().BeTrue();
        options.DefaultSchema.Should().Be("dbo");
        options.PrefixFormat.Should().Be(SchemaPrefixFormat.Underscore);
    }

    [Fact]
    public void FromMetadata_CustomDefaultAndCamelCase_ParsesCorrectly()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["schema-prefix"] = "enabled",
            ["schema-prefix-default"] = "public",
            ["schema-prefix-format"] = "camelcase"
        };

        var options = SchemaPrefixOptions.FromMetadata(metadata);
        options.Enabled.Should().BeTrue();
        options.DefaultSchema.Should().Be("public");
        options.PrefixFormat.Should().Be(SchemaPrefixFormat.CamelCase);
    }

    [Fact]
    public void FromMetadata_Missing_ReturnsDisabled()
    {
        var metadata = new Dictionary<string, object?>();

        var options = SchemaPrefixOptions.FromMetadata(metadata);
        options.Enabled.Should().BeFalse();
    }

    [Fact]
    public void FromMetadata_FormatCaseInsensitive_ParsesCamelCase()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["schema-prefix"] = "ENABLED",
            ["schema-prefix-format"] = "CamelCase"
        };

        var options = SchemaPrefixOptions.FromMetadata(metadata);
        options.Enabled.Should().BeTrue();
        options.PrefixFormat.Should().Be(SchemaPrefixFormat.CamelCase);
    }

    [Fact]
    public void Enabled_MultipleSchemas_EachPrefixedCorrectly()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Users", "dbo"), ("Orders", "sales"), ("AuditLog", "audit"));

        model.GetTableFromDbName("Users").GraphQlName.Should().Be("Users");
        model.GetTableFromDbName("Orders").GraphQlName.Should().Be("sales_Orders");
        model.GetTableFromDbName("AuditLog").GraphQlName.Should().Be("audit_AuditLog");
    }

    [Fact]
    public void Enabled_ViaMetadataLoader_AppliesPrefixes()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("schema-prefix", "enabled")
            .WithModelMetadata("schema-prefix-default", "dbo")
            .WithModelMetadata("schema-prefix-format", "underscore")
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

    [Fact]
    public void Enabled_CamelCase_CapitalizesFirstCharOfOriginalName()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.CamelCase,
            ("orders", "sales"));

        var orders = model.GetTableFromDbName("orders");
        orders.GraphQlName.Should().Be("salesOrders");
    }

    [Fact]
    public void Enabled_SchemaWithSpecialChars_ConvertedToValidGraphQl()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Users", "my schema"));

        var users = model.GetTableFromDbName("Users");
        users.GraphQlName.Should().Be("my_schema_Users");
    }

    [Fact]
    public void Enabled_MatchName_MatchesByGraphQlName()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Users", "dbo"), ("Orders", "sales"));

        var orders = model.GetTableFromDbName("Orders");
        orders.MatchName("sales_Orders").Should().BeTrue();
    }

    [Fact]
    public void Enabled_MatchName_AlsoMatchesByFullName()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Users", "dbo"), ("Orders", "sales"));

        var orders = model.GetTableFromDbName("Orders");
        orders.MatchName("sales_sales_Orders").Should().BeTrue();
    }

    [Fact]
    public void Enabled_MatchName_DboTableMatchesByGraphQlName()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Users", "dbo"), ("Orders", "sales"));

        var users = model.GetTableFromDbName("Users");
        users.MatchName("Users").Should().BeTrue();
    }

    [Fact]
    public void Disabled_Static_ReturnsSameInstance()
    {
        var a = SchemaPrefixOptions.Disabled;
        var b = SchemaPrefixOptions.Disabled;
        a.Should().BeSameAs(b);
    }

    [Fact]
    public void ApplyPrefix_WhenDisabled_ReturnsSameInstance()
    {
        var table = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("sales")
                .WithPrimaryKey("Id"))
            .Build()
            .GetTableFromDbName("Orders");

        var options = SchemaPrefixOptions.Disabled;
        var result = options.ApplyPrefix("Orders", "sales");
        result.Should().Be("Orders");
    }

    [Fact]
    public void Enabled_GetJoinTypeName_UsesPrefixedNames()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Users", "dbo"), ("Orders", "sales"));

        var users = model.GetTableFromDbName("Users");
        var orders = model.GetTableFromDbName("Orders");

        users.GetJoinTypeName(orders).Should().Be("TableOnUserssales_Orders");
        orders.GetJoinTypeName(users).Should().Be("TableOnsales_OrdersUsers");
    }

    [Fact]
    public void Enabled_MetadataPreserved_AfterPrefixing()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Orders", "sales"));

        var orders = model.GetTableFromDbName("Orders");
        orders.Metadata.Should().NotBeNull();
    }

    [Fact]
    public void Enabled_ColumnLookup_PreservedAfterPrefixing()
    {
        var model = CreateModelWithSchemaPrefix("dbo", SchemaPrefixFormat.Underscore,
            ("Orders", "sales"));

        var orders = model.GetTableFromDbName("Orders");
        orders.ColumnLookup.Should().ContainKey("Id");
        orders.ColumnLookup.Should().ContainKey("Total");
    }

    private static IDbModel CreateModelWithSchemaPrefix(
        string defaultSchema,
        SchemaPrefixFormat format,
        params (string tableName, string schema)[] tables)
    {
        var fixture = DbModelTestFixture.Create()
            .WithModelMetadata("schema-prefix", "enabled")
            .WithModelMetadata("schema-prefix-default", defaultSchema)
            .WithModelMetadata("schema-prefix-format", format == SchemaPrefixFormat.CamelCase ? "camelcase" : "underscore");

        foreach (var (tableName, schema) in tables)
        {
            fixture.WithTable(tableName, t => t
                .WithSchema(schema)
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal"));
        }

        // Use foreign keys path to go through DbModel.FromTables for metadata processing
        if (tables.Length > 0)
        {
            var first = tables[0];
            fixture.WithForeignKey("FK_self", first.schema, first.tableName, new[] { "Id" },
                first.schema, first.tableName, new[] { "Id" });
        }

        return fixture.Build();
    }
}
