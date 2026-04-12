using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Schema;

public class EnumTableConfigTests
{
    [Fact]
    public void FromTable_ReturnsNull_WhenNoEnumMetadata()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var table = model.GetTableFromDbName("Users");
        var config = EnumTableConfig.FromTable(table);

        config.Should().BeNull();
    }

    [Fact]
    public void FromTable_ParsesTrueValue_WithAutoDetect()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Status", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithMetadata("enum", "true"))
            .Build();

        var table = model.GetTableFromDbName("Status");
        var config = EnumTableConfig.FromTable(table);

        config.Should().NotBeNull();
        config!.TableName.Should().Be("Status");
        config.ValueColumn.Should().BeNull();
        config.LabelColumn.Should().BeNull();
        config.GraphQlEnumName.Should().Be("StatusValues");
    }

    [Fact]
    public void FromTable_ParsesExplicitValueColumn()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Status", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("code", "varchar")
                .WithColumn("display_name", "nvarchar")
                .WithMetadata("enum", "code"))
            .Build();

        var table = model.GetTableFromDbName("Status");
        var config = EnumTableConfig.FromTable(table);

        config.Should().NotBeNull();
        config!.ValueColumn.Should().Be("code");
        config.LabelColumn.Should().BeNull();
    }

    [Fact]
    public void FromTable_ParsesValueAndLabelColumns()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Status", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("code", "varchar")
                .WithColumn("display_name", "nvarchar")
                .WithMetadata("enum", "code:display_name"))
            .Build();

        var table = model.GetTableFromDbName("Status");
        var config = EnumTableConfig.FromTable(table);

        config.Should().NotBeNull();
        config!.ValueColumn.Should().Be("code");
        config.LabelColumn.Should().Be("display_name");
    }

    [Fact]
    public void FromTable_ReturnsNull_WhenMetadataValueIsEmpty()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Status", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithMetadata("enum", ""))
            .Build();

        var table = model.GetTableFromDbName("Status");
        var config = EnumTableConfig.FromTable(table);

        config.Should().BeNull();
    }

    [Fact]
    public void ResolveValueColumn_AutoDetectsFirstStringColumn()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Status", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("sort_order", "int")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Description", "nvarchar")
                .WithMetadata("enum", "true"))
            .Build();

        var table = model.GetTableFromDbName("Status");
        var config = EnumTableConfig.FromTable(table)!;

        var resolved = config.ResolveValueColumn(table);

        resolved.Should().Be("Name");
    }

    [Fact]
    public void ResolveValueColumn_SkipsPrimaryKeyColumns()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Status", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id", "nvarchar")
                .WithColumn("Code", "varchar")
                .WithMetadata("enum", "true"))
            .Build();

        var table = model.GetTableFromDbName("Status");
        var config = EnumTableConfig.FromTable(table)!;

        var resolved = config.ResolveValueColumn(table);

        resolved.Should().Be("Code");
    }

    [Fact]
    public void ResolveValueColumn_ReturnsNull_WhenNoStringColumns()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Status", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("sort_order", "int")
                .WithMetadata("enum", "true"))
            .Build();

        var table = model.GetTableFromDbName("Status");
        var config = EnumTableConfig.FromTable(table)!;

        var resolved = config.ResolveValueColumn(table);

        resolved.Should().BeNull();
    }

    [Fact]
    public void ResolveValueColumn_UsesExplicitColumn_WhenConfigured()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Status", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("code", "varchar")
                .WithColumn("Name", "nvarchar")
                .WithMetadata("enum", "code"))
            .Build();

        var table = model.GetTableFromDbName("Status");
        var config = EnumTableConfig.FromTable(table)!;

        var resolved = config.ResolveValueColumn(table);

        resolved.Should().Be("code");
    }

    [Fact]
    public void ResolveValueColumn_ReturnsNull_WhenExplicitColumnNotFound()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Status", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithMetadata("enum", "nonexistent"))
            .Build();

        var table = model.GetTableFromDbName("Status");
        var config = EnumTableConfig.FromTable(table)!;

        var resolved = config.ResolveValueColumn(table);

        resolved.Should().BeNull();
    }
}

public class EnumValueSanitizerTests
{
    [Theory]
    [InlineData("Active", "ACTIVE")]
    [InlineData("in progress", "IN_PROGRESS")]
    [InlineData("COMPLETED", "COMPLETED")]
    [InlineData("on-hold", "ON_HOLD")]
    public void Sanitize_ConvertsToUppercaseAndReplacesDelimiters(string input, string expected)
    {
        EnumValueSanitizer.Sanitize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("3rd_party", "_3RD_PARTY")]
    [InlineData("1st", "_1ST")]
    public void Sanitize_PrefixesWithUnderscore_WhenStartsWithDigit(string input, string expected)
    {
        EnumValueSanitizer.Sanitize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("status@#$code", "STATUSCODE")]
    [InlineData("a!b@c#d", "ABCD")]
    public void Sanitize_RemovesInvalidCharacters(string input, string expected)
    {
        EnumValueSanitizer.Sanitize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("a  b", "A_B")]
    [InlineData("a--b", "A_B")]
    [InlineData("a - b", "A_B")]
    public void Sanitize_CollapsesConsecutiveUnderscores(string input, string expected)
    {
        EnumValueSanitizer.Sanitize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@#$")]
    public void Sanitize_ReturnsNull_ForUnrepresentableValues(string? input)
    {
        EnumValueSanitizer.Sanitize(input).Should().BeNull();
    }

    [Fact]
    public void Sanitize_TrimsLeadingAndTrailingUnderscores()
    {
        EnumValueSanitizer.Sanitize("_test_").Should().Be("TEST");
    }

    [Fact]
    public void Sanitize_HandlesWhitespaceAroundValue()
    {
        EnumValueSanitizer.Sanitize("  Active  ").Should().Be("ACTIVE");
    }

    [Fact]
    public void SanitizeAll_FiltersDuplicates_FirstOccurrenceWins()
    {
        var values = new[] { "Active", "active", "ACTIVE" };

        var results = EnumValueSanitizer.SanitizeAll(values);

        results.Should().HaveCount(1);
        results[0].GraphQlName.Should().Be("ACTIVE");
        results[0].DatabaseValue.Should().Be("Active");
    }

    [Fact]
    public void SanitizeAll_FiltersNullAndEmptyValues()
    {
        var values = new[] { "Active", null, "", "  ", "Closed" };

        var results = EnumValueSanitizer.SanitizeAll(values);

        results.Should().HaveCount(2);
        results[0].GraphQlName.Should().Be("ACTIVE");
        results[1].GraphQlName.Should().Be("CLOSED");
    }

    [Fact]
    public void SanitizeAll_PreservesOriginalDatabaseValue()
    {
        var values = new[] { "In Progress", "3rd Party" };

        var results = EnumValueSanitizer.SanitizeAll(values);

        results.Should().HaveCount(2);
        results[0].Should().Be(new EnumValueEntry("IN_PROGRESS", "In Progress"));
        results[1].Should().Be(new EnumValueEntry("_3RD_PARTY", "3rd Party"));
    }
}

public class EnumTableSchemaGeneratorTests
{
    [Fact]
    public void GetEnumTypeDefinition_GeneratesValidEnumType()
    {
        var config = new EnumTableConfig
        {
            TableName = "OrderStatus",
            GraphQlEnumName = "OrderStatusValues",
        };
        var values = new List<EnumValueEntry>
        {
            new("PENDING", "Pending"),
            new("IN_PROGRESS", "In Progress"),
            new("COMPLETED", "Completed"),
        };

        var generator = new EnumTableSchemaGenerator(config, values);
        var result = generator.GetEnumTypeDefinition();

        result.Should().Contain("enum OrderStatusValues {");
        result.Should().Contain("    PENDING");
        result.Should().Contain("    IN_PROGRESS");
        result.Should().Contain("    COMPLETED");
        result.Should().Contain("}");
    }

    [Fact]
    public void GetEnumTypeDefinition_ReturnsEmpty_WhenNoValues()
    {
        var config = new EnumTableConfig
        {
            TableName = "Empty",
            GraphQlEnumName = "EmptyValues",
        };

        var generator = new EnumTableSchemaGenerator(config, Array.Empty<EnumValueEntry>());
        var result = generator.GetEnumTypeDefinition();

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetFilterTypeDefinition_GeneratesFilterInput()
    {
        var config = new EnumTableConfig
        {
            TableName = "Status",
            GraphQlEnumName = "StatusValues",
        };
        var values = new List<EnumValueEntry>
        {
            new("ACTIVE", "Active"),
            new("CLOSED", "Closed"),
        };

        var generator = new EnumTableSchemaGenerator(config, values);
        var result = generator.GetFilterTypeDefinition();

        result.Should().Contain("input FilterTypeStatusValuesInput {");
        result.Should().Contain("_eq: StatusValues");
        result.Should().Contain("_neq: StatusValues");
        result.Should().Contain("_in: [StatusValues]");
        result.Should().Contain("_nin: [StatusValues]");
    }

    [Fact]
    public void GetFilterTypeDefinition_ReturnsEmpty_WhenNoValues()
    {
        var config = new EnumTableConfig
        {
            TableName = "Empty",
            GraphQlEnumName = "EmptyValues",
        };

        var generator = new EnumTableSchemaGenerator(config, Array.Empty<EnumValueEntry>());
        var result = generator.GetFilterTypeDefinition();

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetFilterInputTypeName_ReturnsCorrectName()
    {
        var config = new EnumTableConfig
        {
            TableName = "Status",
            GraphQlEnumName = "StatusValues",
        };
        var generator = new EnumTableSchemaGenerator(config, new List<EnumValueEntry>
        {
            new("ACTIVE", "Active"),
        });

        generator.GetFilterInputTypeName().Should().Be("FilterTypeStatusValuesInput");
    }

    [Fact]
    public void EnumTypeName_ReturnsConfiguredName()
    {
        var config = new EnumTableConfig
        {
            TableName = "Priority",
            GraphQlEnumName = "PriorityValues",
        };
        var generator = new EnumTableSchemaGenerator(config, Array.Empty<EnumValueEntry>());

        generator.EnumTypeName.Should().Be("PriorityValues");
    }
}

public class EnumTableIntegrationTests
{
    [Fact]
    public void EndToEnd_ConfigParsingThroughSchemaGeneration()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("OrderStatus", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Description", "nvarchar")
                .WithMetadata("enum", "Name:Description"))
            .Build();

        var table = model.GetTableFromDbName("OrderStatus");
        var config = EnumTableConfig.FromTable(table);

        config.Should().NotBeNull();
        config!.ValueColumn.Should().Be("Name");
        config.LabelColumn.Should().Be("Description");

        var resolvedColumn = config.ResolveValueColumn(table);
        resolvedColumn.Should().Be("Name");

        // Simulate pre-loaded database values
        var dbValues = new[] { "Pending", "In Progress", "Completed", "On-Hold", "3rd Party Review" };
        var sanitized = EnumValueSanitizer.SanitizeAll(dbValues);

        sanitized.Should().HaveCount(5);

        var generator = new EnumTableSchemaGenerator(config, sanitized);
        var enumDef = generator.GetEnumTypeDefinition();
        var filterDef = generator.GetFilterTypeDefinition();

        enumDef.Should().Contain("enum OrderStatusValues {");
        enumDef.Should().Contain("    PENDING");
        enumDef.Should().Contain("    IN_PROGRESS");
        enumDef.Should().Contain("    COMPLETED");
        enumDef.Should().Contain("    ON_HOLD");
        enumDef.Should().Contain("    _3RD_PARTY_REVIEW");

        filterDef.Should().Contain("input FilterTypeOrderStatusValuesInput {");
        filterDef.Should().Contain("_eq: OrderStatusValues");
    }

    [Fact]
    public void EndToEnd_AutoDetectValueColumn()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Priority", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("sort_order", "int")
                .WithColumn("Name", "nvarchar")
                .WithMetadata("enum", "true"))
            .Build();

        var table = model.GetTableFromDbName("Priority");
        var config = EnumTableConfig.FromTable(table)!;

        config.ValueColumn.Should().BeNull("auto-detect mode");

        var resolvedColumn = config.ResolveValueColumn(table);
        resolvedColumn.Should().Be("Name");

        var dbValues = new[] { "Low", "Medium", "High", "Critical" };
        var sanitized = EnumValueSanitizer.SanitizeAll(dbValues);
        var generator = new EnumTableSchemaGenerator(config, sanitized);

        var enumDef = generator.GetEnumTypeDefinition();
        enumDef.Should().Contain("enum PriorityValues {");
        enumDef.Should().Contain("    LOW");
        enumDef.Should().Contain("    CRITICAL");
    }

    [Fact]
    public void NonEnumTable_IsNotAffected()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Status", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithMetadata("enum", "true"))
            .Build();

        var usersConfig = EnumTableConfig.FromTable(model.GetTableFromDbName("Users"));
        var statusConfig = EnumTableConfig.FromTable(model.GetTableFromDbName("Status"));

        usersConfig.Should().BeNull();
        statusConfig.Should().NotBeNull();
    }
}
