using BifrostQL.Core.Forms;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;

namespace BifrostQL.Core.Test.Forms;

public class ForeignKeyHandlerTests
{
    #region IsForeignKey Detection

    [Fact]
    public void IsForeignKey_ColumnWithSingleLink_ReturnsTrue()
    {
        var model = CreateModelWithForeignKey();
        var ordersTable = model.GetTableFromDbName("Orders");
        var userIdColumn = ordersTable.ColumnLookup["UserId"];

        Assert.True(ForeignKeyHandler.IsForeignKey(userIdColumn, ordersTable));
    }

    [Fact]
    public void IsForeignKey_RegularColumn_ReturnsFalse()
    {
        var model = CreateModelWithForeignKey();
        var ordersTable = model.GetTableFromDbName("Orders");
        var totalColumn = ordersTable.ColumnLookup["Total"];

        Assert.False(ForeignKeyHandler.IsForeignKey(totalColumn, ordersTable));
    }

    [Fact]
    public void IsForeignKey_PrimaryKeyColumn_ReturnsFalse()
    {
        var model = CreateModelWithForeignKey();
        var ordersTable = model.GetTableFromDbName("Orders");
        var idColumn = ordersTable.ColumnLookup["Id"];

        Assert.False(ForeignKeyHandler.IsForeignKey(idColumn, ordersTable));
    }

    [Fact]
    public void IsForeignKey_TableWithNoLinks_ReturnsFalse()
    {
        var model = CreateModelWithForeignKey();
        var usersTable = model.GetTableFromDbName("Users");
        var nameColumn = usersTable.ColumnLookup["Name"];

        Assert.False(ForeignKeyHandler.IsForeignKey(nameColumn, usersTable));
    }

    #endregion

    #region GetReferencedTable

    [Fact]
    public void GetReferencedTable_ForeignKeyColumn_ReturnsParentTable()
    {
        var model = CreateModelWithForeignKey();
        var ordersTable = model.GetTableFromDbName("Orders");
        var userIdColumn = ordersTable.ColumnLookup["UserId"];

        var referenced = ForeignKeyHandler.GetReferencedTable(userIdColumn, ordersTable);

        Assert.NotNull(referenced);
        Assert.Equal("Users", referenced!.DbName);
    }

    [Fact]
    public void GetReferencedTable_RegularColumn_ReturnsNull()
    {
        var model = CreateModelWithForeignKey();
        var ordersTable = model.GetTableFromDbName("Orders");
        var totalColumn = ordersTable.ColumnLookup["Total"];

        var referenced = ForeignKeyHandler.GetReferencedTable(totalColumn, ordersTable);

        Assert.Null(referenced);
    }

    #endregion

    #region GetReferencedKeyColumn

    [Fact]
    public void GetReferencedKeyColumn_ForeignKeyColumn_ReturnsParentKeyColumnName()
    {
        var model = CreateModelWithForeignKey();
        var ordersTable = model.GetTableFromDbName("Orders");
        var userIdColumn = ordersTable.ColumnLookup["UserId"];

        var keyColumn = ForeignKeyHandler.GetReferencedKeyColumn(userIdColumn, ordersTable);

        Assert.Equal("Id", keyColumn);
    }

    [Fact]
    public void GetReferencedKeyColumn_RegularColumn_ReturnsNull()
    {
        var model = CreateModelWithForeignKey();
        var ordersTable = model.GetTableFromDbName("Orders");
        var totalColumn = ordersTable.ColumnLookup["Total"];

        var keyColumn = ForeignKeyHandler.GetReferencedKeyColumn(totalColumn, ordersTable);

        Assert.Null(keyColumn);
    }

    #endregion

    #region GetDisplayColumn - Priority: name > title > description > varchar > PK

    [Fact]
    public void GetDisplayColumn_TableWithNameColumn_ReturnsName()
    {
        var model = CreateModelWithForeignKey();
        var usersTable = model.GetTableFromDbName("Users");

        var displayColumn = ForeignKeyHandler.GetDisplayColumn(usersTable);

        Assert.Equal("Name", displayColumn);
    }

    [Fact]
    public void GetDisplayColumn_TableWithTitleColumn_ReturnsTitle()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Categories", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Title", "nvarchar")
                .WithColumn("SortOrder", "int"))
            .Build();
        var table = model.GetTableFromDbName("Categories");

        var displayColumn = ForeignKeyHandler.GetDisplayColumn(table);

        Assert.Equal("Title", displayColumn);
    }

    [Fact]
    public void GetDisplayColumn_TableWithDescriptionColumn_ReturnsDescription()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Statuses", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Description", "nvarchar")
                .WithColumn("SortOrder", "int"))
            .Build();
        var table = model.GetTableFromDbName("Statuses");

        var displayColumn = ForeignKeyHandler.GetDisplayColumn(table);

        Assert.Equal("Description", displayColumn);
    }

    [Fact]
    public void GetDisplayColumn_NameTakesPriorityOverTitle()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Categories", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Title", "nvarchar")
                .WithColumn("Name", "nvarchar"))
            .Build();
        var table = model.GetTableFromDbName("Categories");

        var displayColumn = ForeignKeyHandler.GetDisplayColumn(table);

        Assert.Equal("Name", displayColumn);
    }

    [Fact]
    public void GetDisplayColumn_FallsBackToFirstVarchar()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Codes", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Code", "varchar")
                .WithColumn("Value", "int"))
            .Build();
        var table = model.GetTableFromDbName("Codes");

        var displayColumn = ForeignKeyHandler.GetDisplayColumn(table);

        Assert.Equal("Code", displayColumn);
    }

    [Fact]
    public void GetDisplayColumn_FallsBackToPrimaryKey()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Numbers", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Value", "int")
                .WithColumn("Count", "bigint"))
            .Build();
        var table = model.GetTableFromDbName("Numbers");

        var displayColumn = ForeignKeyHandler.GetDisplayColumn(table);

        Assert.Equal("Id", displayColumn);
    }

    [Fact]
    public void GetDisplayColumn_SkipsPrimaryKeyVarchar()
    {
        // If the only varchar column IS the primary key, skip it and look for PK fallback
        var model = DbModelTestFixture.Create()
            .WithTable("Tags", t => t
                .WithColumn("Code", "varchar", isPrimaryKey: true)
                .WithColumn("SortOrder", "int"))
            .Build();
        var table = model.GetTableFromDbName("Tags");

        // The varchar PK should be skipped in the varchar search, but returned as PK fallback
        var displayColumn = ForeignKeyHandler.GetDisplayColumn(table);

        Assert.Equal("Code", displayColumn);
    }

    #endregion

    #region GenerateSelect

    [Fact]
    public void GenerateSelect_GeneratesSelectElement()
    {
        var column = new ColumnDto
        {
            ColumnName = "DepartmentId", GraphQlName = "departmentId", NormalizedName = "Department",
            DataType = "int", IsNullable = false
        };
        var options = new List<(string value, string displayText)>
        {
            ("1", "Engineering"),
            ("2", "Sales"),
            ("3", "Marketing")
        };

        var html = ForeignKeyHandler.GenerateSelect(column, options);

        Assert.Contains("<select", html);
        Assert.Contains("</select>", html);
        Assert.Contains("name=\"DepartmentId\"", html);
        Assert.Contains("id=\"departmentid\"", html);
    }

    [Fact]
    public void GenerateSelect_IncludesEmptyPlaceholderOption()
    {
        var column = new ColumnDto
        {
            ColumnName = "DepartmentId", GraphQlName = "departmentId", NormalizedName = "Department",
            DataType = "int", IsNullable = false
        };
        var options = new List<(string value, string displayText)> { ("1", "Engineering") };

        var html = ForeignKeyHandler.GenerateSelect(column, options);

        Assert.Contains("<option value=\"\">-- Select --</option>", html);
    }

    [Fact]
    public void GenerateSelect_RendersAllOptions()
    {
        var column = new ColumnDto
        {
            ColumnName = "DepartmentId", GraphQlName = "departmentId", NormalizedName = "Department",
            DataType = "int", IsNullable = true
        };
        var options = new List<(string value, string displayText)>
        {
            ("1", "Engineering"),
            ("2", "Sales"),
            ("3", "Marketing")
        };

        var html = ForeignKeyHandler.GenerateSelect(column, options);

        Assert.Contains("<option value=\"1\">Engineering</option>", html);
        Assert.Contains("<option value=\"2\">Sales</option>", html);
        Assert.Contains("<option value=\"3\">Marketing</option>", html);
    }

    [Fact]
    public void GenerateSelect_RequiredColumn_HasRequiredAttribute()
    {
        var column = new ColumnDto
        {
            ColumnName = "DepartmentId", GraphQlName = "departmentId", NormalizedName = "Department",
            DataType = "int", IsNullable = false
        };
        var options = new List<(string value, string displayText)> { ("1", "Engineering") };

        var html = ForeignKeyHandler.GenerateSelect(column, options);

        Assert.Contains("required", html);
        Assert.Contains("aria-required=\"true\"", html);
    }

    [Fact]
    public void GenerateSelect_NullableColumn_NoRequiredAttribute()
    {
        var column = new ColumnDto
        {
            ColumnName = "DepartmentId", GraphQlName = "departmentId", NormalizedName = "Department",
            DataType = "int", IsNullable = true
        };
        var options = new List<(string value, string displayText)> { ("1", "Engineering") };

        var html = ForeignKeyHandler.GenerateSelect(column, options);

        Assert.DoesNotContain("required", html);
        Assert.DoesNotContain("aria-required", html);
    }

    [Fact]
    public void GenerateSelect_CurrentValue_MarksSelectedOption()
    {
        var column = new ColumnDto
        {
            ColumnName = "DepartmentId", GraphQlName = "departmentId", NormalizedName = "Department",
            DataType = "int", IsNullable = false
        };
        var options = new List<(string value, string displayText)>
        {
            ("1", "Engineering"),
            ("2", "Sales"),
            ("3", "Marketing")
        };

        var html = ForeignKeyHandler.GenerateSelect(column, options, currentValue: "2");

        Assert.Contains("<option value=\"2\" selected>Sales</option>", html);
        Assert.DoesNotContain("<option value=\"1\" selected", html);
        Assert.DoesNotContain("<option value=\"3\" selected", html);
    }

    [Fact]
    public void GenerateSelect_NoCurrentValue_NoSelectedOption()
    {
        var column = new ColumnDto
        {
            ColumnName = "DepartmentId", GraphQlName = "departmentId", NormalizedName = "Department",
            DataType = "int", IsNullable = true
        };
        var options = new List<(string value, string displayText)> { ("1", "Engineering") };

        var html = ForeignKeyHandler.GenerateSelect(column, options);

        Assert.DoesNotContain("selected", html);
    }

    [Fact]
    public void GenerateSelect_EmptyOptions_RendersSelectWithPlaceholderOnly()
    {
        var column = new ColumnDto
        {
            ColumnName = "DepartmentId", GraphQlName = "departmentId", NormalizedName = "Department",
            DataType = "int", IsNullable = true
        };
        var options = Array.Empty<(string, string)>();

        var html = ForeignKeyHandler.GenerateSelect(column, options);

        Assert.Contains("<select", html);
        Assert.Contains("<option value=\"\">-- Select --</option>", html);
        Assert.Contains("</select>", html);
    }

    [Fact]
    public void GenerateSelect_HtmlEncodesDisplayText()
    {
        var column = new ColumnDto
        {
            ColumnName = "DepartmentId", GraphQlName = "departmentId", NormalizedName = "Department",
            DataType = "int", IsNullable = true
        };
        var options = new List<(string value, string displayText)>
        {
            ("1", "R&D <Engineering>")
        };

        var html = ForeignKeyHandler.GenerateSelect(column, options);

        Assert.DoesNotContain("<Engineering>", html);
        Assert.Contains("R&amp;D &lt;Engineering&gt;", html);
    }

    #endregion

    #region Helper Methods

    private static IDbModel CreateModelWithForeignKey()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar"))
            .WithTable("Orders", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("UserId", "int")
                .WithColumn("Total", "decimal")
                .WithColumn("Status", "nvarchar"))
            .WithSingleLink("Orders", "UserId", "Users", "Id", "Users")
            .Build();
    }

    #endregion
}
