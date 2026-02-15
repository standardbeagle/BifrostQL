using BifrostQL.Core.Forms;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

public class LookupTableDetectorTests
{
    #region IsLookupTable - Positive Cases

    [Theory]
    [InlineData("statuses")]
    [InlineData("types")]
    [InlineData("categories")]
    [InlineData("priorities")]
    [InlineData("roles")]
    [InlineData("states")]
    [InlineData("countries")]
    [InlineData("regions")]
    [InlineData("currencies")]
    [InlineData("languages")]
    public void IsLookupTable_CommonLookupNames_ReturnsTrue(string tableName)
    {
        var model = DbModelTestFixture.Create()
            .WithTable(tableName, t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName(tableName);

        LookupTableDetector.IsLookupTable(table).Should().BeTrue();
    }

    [Theory]
    [InlineData("order_status")]
    [InlineData("task_type")]
    [InlineData("product_category")]
    [InlineData("ticket_priority")]
    public void IsLookupTable_SuffixedNames_ReturnsTrue(string tableName)
    {
        var model = DbModelTestFixture.Create()
            .WithTable(tableName, t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName(tableName);

        LookupTableDetector.IsLookupTable(table).Should().BeTrue();
    }

    [Fact]
    public void IsLookupTable_SixColumns_ReturnsTrue()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Code", "varchar")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Description", "nvarchar")
                .WithColumn("Icon", "varchar")
                .WithColumn("SortOrder", "int"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        LookupTableDetector.IsLookupTable(table).Should().BeTrue();
    }

    [Fact]
    public void IsLookupTable_TwoColumns_ReturnsTrue()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("types", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("types");

        LookupTableDetector.IsLookupTable(table).Should().BeTrue();
    }

    #endregion

    #region IsLookupTable - Negative Cases

    [Fact]
    public void IsLookupTable_TooManyColumns_ReturnsFalse()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Code", "varchar")
                .WithColumn("Desc", "nvarchar")
                .WithColumn("Icon", "varchar")
                .WithColumn("Color", "varchar")
                .WithColumn("Extra", "varchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        LookupTableDetector.IsLookupTable(table).Should().BeFalse();
    }

    [Fact]
    public void IsLookupTable_SingleColumn_ReturnsFalse()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        LookupTableDetector.IsLookupTable(table).Should().BeFalse();
    }

    [Fact]
    public void IsLookupTable_CompositePrimaryKey_ReturnsFalse()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id1", "int", isPrimaryKey: true)
                .WithColumn("Id2", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        LookupTableDetector.IsLookupTable(table).Should().BeFalse();
    }

    [Fact]
    public void IsLookupTable_HasOutboundForeignKeys_ReturnsFalse()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("categories", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("ParentId", "int"))
            .WithTable("ParentTable", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .WithSingleLink("categories", "ParentId", "ParentTable", "Id", "ParentTable")
            .Build();
        var table = model.GetTableFromDbName("categories");

        LookupTableDetector.IsLookupTable(table).Should().BeFalse();
    }

    [Fact]
    public void IsLookupTable_ContainsBinaryColumn_ReturnsFalse()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Data", "varbinary"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        LookupTableDetector.IsLookupTable(table).Should().BeFalse();
    }

    [Fact]
    public void IsLookupTable_ContainsXmlColumn_ReturnsFalse()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Config", "xml"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        LookupTableDetector.IsLookupTable(table).Should().BeFalse();
    }

    [Fact]
    public void IsLookupTable_NonLookupName_ReturnsFalse()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("Users");

        LookupTableDetector.IsLookupTable(table).Should().BeFalse();
    }

    [Fact]
    public void IsLookupTable_NoPrimaryKey_ReturnsFalse()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int")
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        LookupTableDetector.IsLookupTable(table).Should().BeFalse();
    }

    #endregion

    #region DetectColumnRoles

    [Fact]
    public void DetectColumnRoles_IdColumn_DetectedFromPrimaryKey()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.IdColumn.Should().Be("Id");
    }

    [Fact]
    public void DetectColumnRoles_LabelColumn_DetectsName()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.LabelColumn.Should().Be("Name");
    }

    [Fact]
    public void DetectColumnRoles_LabelColumn_DetectsTitle()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("categories", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Title", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("categories");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.LabelColumn.Should().Be("Title");
    }

    [Fact]
    public void DetectColumnRoles_LabelColumn_DetectsLabel()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Label", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.LabelColumn.Should().Be("Label");
    }

    [Fact]
    public void DetectColumnRoles_LabelColumn_FallsBackToFirstVarchar()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("SortOrder", "int")
                .WithColumn("Code", "varchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        // "Code" is detected as value column since it's a string named "code",
        // but label fallback should find the first string column
        roles.LabelColumn.Should().NotBeNull();
    }

    [Fact]
    public void DetectColumnRoles_ValueColumn_DetectsCode()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Code", "varchar")
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.ValueColumn.Should().Be("Code");
    }

    [Fact]
    public void DetectColumnRoles_ValueColumn_DetectsSlug()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("categories", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Slug", "varchar")
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("categories");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.ValueColumn.Should().Be("Slug");
    }

    [Fact]
    public void DetectColumnRoles_ValueColumn_NotDetectedForIntType()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Code", "int")
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.ValueColumn.Should().BeNull();
    }

    [Fact]
    public void DetectColumnRoles_SortColumn_DetectsSortOrder()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("sort_order", "int"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.SortColumn.Should().Be("sort_order");
    }

    [Fact]
    public void DetectColumnRoles_SortColumn_DetectsPosition()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("position", "int"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.SortColumn.Should().Be("position");
    }

    [Fact]
    public void DetectColumnRoles_SortColumn_DetectsDisplayOrder()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("display_order", "int"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.SortColumn.Should().Be("display_order");
    }

    [Fact]
    public void DetectColumnRoles_DescriptionColumn_Detected()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("description", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.DescriptionColumn.Should().Be("description");
    }

    [Fact]
    public void DetectColumnRoles_IconColumn_Detected()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("icon", "varchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.IconColumn.Should().Be("icon");
    }

    [Fact]
    public void DetectColumnRoles_IconColumn_DetectsSuffix()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("status_icon", "varchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.IconColumn.Should().Be("status_icon");
    }

    [Fact]
    public void DetectColumnRoles_ColorColumn_Detected()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("priorities", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("color", "varchar"))
            .Build();
        var table = model.GetTableFromDbName("priorities");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.ColorColumn.Should().Be("color");
    }

    [Fact]
    public void DetectColumnRoles_ColorColumn_DetectsHexColor()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("priorities", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("hex_color", "varchar"))
            .Build();
        var table = model.GetTableFromDbName("priorities");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.ColorColumn.Should().Be("hex_color");
    }

    [Fact]
    public void DetectColumnRoles_AllRolesDetected()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Code", "varchar")
                .WithColumn("Name", "nvarchar")
                .WithColumn("description", "nvarchar")
                .WithColumn("icon", "varchar")
                .WithColumn("display_order", "int"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.IdColumn.Should().Be("Id");
        roles.ValueColumn.Should().Be("Code");
        roles.LabelColumn.Should().Be("Name");
        roles.DescriptionColumn.Should().Be("description");
        roles.IconColumn.Should().Be("icon");
        roles.SortColumn.Should().Be("display_order");
    }

    [Fact]
    public void DetectColumnRoles_HasRichData_TrueWithIcon()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("icon", "varchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.HasRichData.Should().BeTrue();
    }

    [Fact]
    public void DetectColumnRoles_HasRichData_FalseWithoutRichColumns()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = LookupTableDetector.DetectColumnRoles(table);

        roles.HasRichData.Should().BeFalse();
    }

    #endregion

    #region SelectUiMode

    [Theory]
    [InlineData(0, LookupUiMode.Dropdown)]
    [InlineData(10, LookupUiMode.Dropdown)]
    [InlineData(50, LookupUiMode.Dropdown)]
    [InlineData(51, LookupUiMode.Autocomplete)]
    [InlineData(200, LookupUiMode.Autocomplete)]
    [InlineData(500, LookupUiMode.Autocomplete)]
    [InlineData(501, LookupUiMode.AsyncSearch)]
    [InlineData(10000, LookupUiMode.AsyncSearch)]
    public void SelectUiMode_DefaultThresholds_ReturnsCorrectMode(int rowCount, LookupUiMode expected)
    {
        LookupTableDetector.SelectUiMode(rowCount).Should().Be(expected);
    }

    [Fact]
    public void SelectUiMode_CustomThresholds_Respected()
    {
        LookupTableDetector.SelectUiMode(30, dropdownThreshold: 20, autocompleteThreshold: 100)
            .Should().Be(LookupUiMode.Autocomplete);
    }

    [Fact]
    public void SelectUiMode_CustomThresholds_LargeCount()
    {
        LookupTableDetector.SelectUiMode(200, dropdownThreshold: 20, autocompleteThreshold: 100)
            .Should().Be(LookupUiMode.AsyncSearch);
    }

    #endregion

    #region LookupTableConfig

    [Fact]
    public void FromDetection_CreatesConfigWithDetectedRoles()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Code", "varchar")
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var config = LookupTableConfig.FromDetection(table);

        config.TableName.Should().Be("statuses");
        config.Roles.IdColumn.Should().Be("Id");
        config.Roles.ValueColumn.Should().Be("Code");
        config.Roles.LabelColumn.Should().Be("Name");
        config.DropdownThreshold.Should().Be(50);
        config.AutocompleteThreshold.Should().Be(500);
    }

    [Fact]
    public void FromMetadata_OverridesDetectedRoles()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Code", "varchar")
                .WithColumn("Name", "nvarchar")
                .WithColumn("DisplayName", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var config = LookupTableConfig.FromMetadata(table, labelColumn: "DisplayName");

        config.Roles.LabelColumn.Should().Be("DisplayName");
        config.Roles.ValueColumn.Should().Be("Code");
    }

    [Fact]
    public void FromMetadata_CustomThresholds()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("countries", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("countries");

        var config = LookupTableConfig.FromMetadata(table, dropdownThreshold: 100, autocompleteThreshold: 1000);

        config.DropdownThreshold.Should().Be(100);
        config.AutocompleteThreshold.Should().Be(1000);
    }

    #endregion

    #region ConfigPatternDetector.DetectLookupTables

    [Fact]
    public void ConfigPatternDetector_DetectLookupTables_FindsLookupTables()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar")
                .WithColumn("StatusId", "int"))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.DetectLookupTables(model);

        results.Should().HaveCount(1);
        results[0].TableName.Should().Be("statuses");
        results[0].Roles.LabelColumn.Should().Be("Name");
    }

    [Fact]
    public void ConfigPatternDetector_DetectLookupTables_MultipleLookups()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .WithTable("priorities", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Level", "int"))
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar")
                .WithColumn("Phone", "nvarchar"))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.DetectLookupTables(model);

        results.Should().HaveCount(2);
        results.Should().Contain(r => r.TableName == "statuses");
        results.Should().Contain(r => r.TableName == "priorities");
    }

    [Fact]
    public void ConfigPatternDetector_DetectLookupTables_EmptyModel()
    {
        var model = DbModelTestFixture.Create().Build();

        var detector = new ConfigPatternDetector();
        var results = detector.DetectLookupTables(model);

        results.Should().BeEmpty();
    }

    #endregion
}

public class LookupFormIntegrationTests
{
    #region BifrostFormBuilder Lookup Detection

    [Fact]
    public void BifrostFormBuilder_DetectsLookupTables()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar")
                .WithColumn("Phone", "nvarchar"))
            .Build();

        var builder = new BifrostFormBuilder(model);

        builder.LookupConfigs.Should().ContainKey("statuses");
        builder.LookupConfigs.Should().NotContainKey("Users");
    }

    [Fact]
    public void BifrostFormBuilder_LookupConfig_HasCorrectRoles()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Code", "varchar")
                .WithColumn("Name", "nvarchar")
                .WithColumn("sort_order", "int"))
            .Build();

        var builder = new BifrostFormBuilder(model);
        var config = builder.LookupConfigs["statuses"];

        config.Roles.IdColumn.Should().Be("Id");
        config.Roles.ValueColumn.Should().Be("Code");
        config.Roles.LabelColumn.Should().Be("Name");
        config.Roles.SortColumn.Should().Be("sort_order");
    }

    [Fact]
    public void BifrostFormBuilder_ForeignKeyToLookup_SmallOptionCount_NoDataAutocomplete()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("StatusId", "int")
                .WithColumn("Total", "decimal"))
            .WithSingleLink("Orders", "StatusId", "statuses", "Id", "statuses")
            .Build();

        var builder = new BifrostFormBuilder(model);
        var fkOptions = new Dictionary<string, IReadOnlyList<(string value, string displayText)>>
        {
            ["StatusId"] = new List<(string, string)>
            {
                ("1", "Open"),
                ("2", "Closed"),
                ("3", "Pending")
            }
        };

        var html = builder.GenerateForm("Orders", FormMode.Insert, foreignKeyOptions: fkOptions);

        // With only 3 options (below 50 threshold), no data-autocomplete attribute
        html.Should().NotContain("data-autocomplete");
        html.Should().Contain("<select");
        html.Should().Contain("name=\"StatusId\"");
    }

    [Fact]
    public void BifrostFormBuilder_ForeignKeyToNonLookup_NoDataAutocomplete()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar")
                .WithColumn("Phone", "nvarchar"))
            .WithTable("Orders", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("UserId", "int")
                .WithColumn("Total", "decimal"))
            .WithSingleLink("Orders", "UserId", "Users", "Id", "Users")
            .Build();

        var builder = new BifrostFormBuilder(model);
        var html = builder.GenerateForm("Orders", FormMode.Insert);

        html.Should().NotContain("data-autocomplete");
    }

    [Fact]
    public void BifrostFormBuilder_BackwardCompatible_WithoutLookupTables()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar"))
            .Build();

        var builder = new BifrostFormBuilder(model);

        builder.LookupConfigs.Should().BeEmpty();
        var html = builder.GenerateForm("Users", FormMode.Insert);
        html.Should().Contain("<form method=\"POST\"");
        html.Should().Contain("name=\"Name\"");
    }

    #endregion

    #region ForeignKeyHandler GetDisplayColumn with Roles

    [Fact]
    public void GetDisplayColumn_WithRoles_UsesLabelColumn()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Code", "varchar")
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = new LookupColumnRoles
        {
            IdColumn = "Id",
            LabelColumn = "Code"
        };

        var displayColumn = ForeignKeyHandler.GetDisplayColumn(table, roles);

        displayColumn.Should().Be("Code");
    }

    [Fact]
    public void GetDisplayColumn_NullRoles_FallsBackToHeuristic()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var displayColumn = ForeignKeyHandler.GetDisplayColumn(table, null);

        displayColumn.Should().Be("Name");
    }

    [Fact]
    public void GetDisplayColumn_RolesWithNullLabel_FallsBackToHeuristic()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("statuses");

        var roles = new LookupColumnRoles
        {
            IdColumn = "Id",
            LabelColumn = null
        };

        var displayColumn = ForeignKeyHandler.GetDisplayColumn(table, roles);

        displayColumn.Should().Be("Name");
    }

    #endregion

    #region GenerateSelect with UI Mode

    [Fact]
    public void GenerateSelect_AutocompleteMode_AddsDataAttribute()
    {
        var column = new ColumnDto
        {
            ColumnName = "StatusId", GraphQlName = "statusId", NormalizedName = "Status",
            DataType = "int", IsNullable = true
        };
        var options = new List<(string value, string displayText)>
        {
            ("1", "Open"),
            ("2", "Closed")
        };

        var html = ForeignKeyHandler.GenerateSelect(column, options, uiMode: LookupUiMode.Autocomplete);

        html.Should().Contain("data-autocomplete=\"true\"");
        html.Should().Contain("<select");
    }

    [Fact]
    public void GenerateSelect_DropdownMode_NoDataAttribute()
    {
        var column = new ColumnDto
        {
            ColumnName = "StatusId", GraphQlName = "statusId", NormalizedName = "Status",
            DataType = "int", IsNullable = true
        };
        var options = new List<(string value, string displayText)> { ("1", "Open") };

        var html = ForeignKeyHandler.GenerateSelect(column, options, uiMode: LookupUiMode.Dropdown);

        html.Should().NotContain("data-autocomplete");
    }

    [Fact]
    public void GenerateSelect_NullMode_NoDataAttribute()
    {
        var column = new ColumnDto
        {
            ColumnName = "StatusId", GraphQlName = "statusId", NormalizedName = "Status",
            DataType = "int", IsNullable = true
        };
        var options = new List<(string value, string displayText)> { ("1", "Open") };

        var html = ForeignKeyHandler.GenerateSelect(column, options, uiMode: null);

        html.Should().NotContain("data-autocomplete");
    }

    [Fact]
    public void GenerateSelect_AutocompleteMode_PreservesExistingBehavior()
    {
        var column = new ColumnDto
        {
            ColumnName = "StatusId", GraphQlName = "statusId", NormalizedName = "Status",
            DataType = "int", IsNullable = false
        };
        var options = new List<(string value, string displayText)>
        {
            ("1", "Open"),
            ("2", "Closed")
        };

        var html = ForeignKeyHandler.GenerateSelect(column, options, currentValue: "2", uiMode: LookupUiMode.Autocomplete);

        html.Should().Contain("required");
        html.Should().Contain("<option value=\"2\" selected>Closed</option>");
        html.Should().Contain("-- Select --");
        html.Should().Contain("data-autocomplete=\"true\"");
    }

    #endregion
}
