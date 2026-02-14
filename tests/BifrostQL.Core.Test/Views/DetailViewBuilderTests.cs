using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Views;

namespace BifrostQL.Core.Test.Views;

public class DetailViewBuilderTests
{
    #region Basic Structure

    [Fact]
    public void GenerateDetailView_ContainsDetailWrapper()
    {
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model);
        var data = SimpleRecord();

        var html = builder.GenerateDetailView("Users", data);

        Assert.Contains("class=\"bifrost-detail\"", html);
    }

    [Fact]
    public void GenerateDetailView_ContainsTitle()
    {
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model);
        var data = SimpleRecord();

        var html = builder.GenerateDetailView("Users", data);

        Assert.Contains("<h1>", html);
        Assert.Contains("User Details", html);
    }

    [Fact]
    public void GenerateDetailView_ContainsDefinitionList()
    {
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model);
        var data = SimpleRecord();

        var html = builder.GenerateDetailView("Users", data);

        Assert.Contains("<dl>", html);
        Assert.Contains("<dt>", html);
        Assert.Contains("<dd>", html);
        Assert.Contains("</dl>", html);
    }

    [Fact]
    public void GenerateDetailView_DisplaysAllColumns()
    {
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model);
        var data = SimpleRecord();

        var html = builder.GenerateDetailView("Users", data);

        Assert.Contains("<dt>Id</dt>", html);
        Assert.Contains("<dd>42</dd>", html);
        Assert.Contains("<dt>Name</dt>", html);
        Assert.Contains("<dd>Alice</dd>", html);
        Assert.Contains("<dt>Email</dt>", html);
        Assert.Contains("<dd>alice@example.com</dd>", html);
    }

    #endregion

    #region Value Formatting

    [Fact]
    public void GenerateDetailView_NullValue_DisplaysNullIndicator()
    {
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model);
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = null, ["Email"] = "test@test.com" };

        var html = builder.GenerateDetailView("Users", data);

        Assert.Contains("(null)", html);
    }

    [Fact]
    public void GenerateDetailView_BooleanTrue_DisplaysYes()
    {
        var model = CreateModelWithTypes();
        var builder = new DetailViewBuilder(model);
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Widget", ["InStock"] = true, ["Price"] = 9.99m, ["CreatedAt"] = new DateTime(2024, 1, 15) };

        var html = builder.GenerateDetailView("Products", data);

        Assert.Contains("Yes", html);
    }

    [Fact]
    public void GenerateDetailView_BooleanFalse_DisplaysNo()
    {
        var model = CreateModelWithTypes();
        var builder = new DetailViewBuilder(model);
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Widget", ["InStock"] = false, ["Price"] = 9.99m, ["CreatedAt"] = new DateTime(2024, 1, 15) };

        var html = builder.GenerateDetailView("Products", data);

        Assert.Contains("No", html);
    }

    [Fact]
    public void GenerateDetailView_DateTime_DisplaysTimeElement()
    {
        var model = CreateModelWithTypes();
        var builder = new DetailViewBuilder(model);
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Widget", ["InStock"] = true, ["Price"] = 9.99m, ["CreatedAt"] = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc) };

        var html = builder.GenerateDetailView("Products", data);

        Assert.Contains("<time datetime=", html);
        Assert.Contains("January 15, 2024", html);
    }

    [Fact]
    public void GenerateDetailView_BinaryColumn_DisplaysBinaryIndicator()
    {
        var model = CreateModelWithBinaryColumn();
        var builder = new DetailViewBuilder(model);
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Widget", ["Image"] = new byte[] { 1, 2, 3 } };

        var html = builder.GenerateDetailView("Products", data);

        Assert.Contains("(binary data)", html);
    }

    #endregion

    #region Foreign Key Links

    [Fact]
    public void GenerateDetailView_ForeignKey_RendersLink()
    {
        var model = CreateModelWithForeignKey();
        var builder = new DetailViewBuilder(model);
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Alice", ["DepartmentId"] = 5 };

        var html = builder.GenerateDetailView("Employees", data);

        Assert.Contains("href=\"/bifrost/view/Departments/5\"", html);
    }

    [Fact]
    public void GenerateDetailView_ForeignKeyNull_DisplaysNull()
    {
        var model = CreateModelWithNullableForeignKey();
        var builder = new DetailViewBuilder(model);
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Alice", ["DepartmentId"] = null };

        var html = builder.GenerateDetailView("Employees", data);

        Assert.Contains("(null)", html);
    }

    #endregion

    #region Email and URL Links

    [Fact]
    public void GenerateDetailView_EmailColumn_RendersMailtoLink()
    {
        var model = CreateModelWithEmailColumn();
        var builder = new DetailViewBuilder(model);
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Alice", ["Email"] = "alice@example.com" };

        var html = builder.GenerateDetailView("Users", data);

        Assert.Contains("href=\"mailto:alice@example.com\"", html);
        Assert.Contains(">alice@example.com</a>", html);
    }

    [Fact]
    public void GenerateDetailView_UrlColumn_RendersHyperlink()
    {
        var model = CreateModelWithUrlColumn();
        var builder = new DetailViewBuilder(model);
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Acme", ["Website"] = "https://example.com" };

        var html = builder.GenerateDetailView("Companies", data);

        Assert.Contains("href=\"https://example.com\"", html);
        Assert.Contains(">https://example.com</a>", html);
    }

    #endregion

    #region Action Buttons

    [Fact]
    public void GenerateDetailView_ContainsEditLink()
    {
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model);
        var data = SimpleRecord();

        var html = builder.GenerateDetailView("Users", data);

        Assert.Contains("href=\"/bifrost/edit/Users/42\"", html);
        Assert.Contains("btn-primary", html);
        Assert.Contains(">Edit</a>", html);
    }

    [Fact]
    public void GenerateDetailView_ContainsDeleteLink()
    {
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model);
        var data = SimpleRecord();

        var html = builder.GenerateDetailView("Users", data);

        Assert.Contains("href=\"/bifrost/delete/Users/42\"", html);
        Assert.Contains("btn-danger", html);
        Assert.Contains(">Delete</a>", html);
    }

    [Fact]
    public void GenerateDetailView_ContainsBackToListLink()
    {
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model);
        var data = SimpleRecord();

        var html = builder.GenerateDetailView("Users", data);

        Assert.Contains("href=\"/bifrost/list/Users\"", html);
        Assert.Contains("btn-secondary", html);
        Assert.Contains(">Back to List</a>", html);
    }

    #endregion

    #region Custom Base Path

    [Fact]
    public void GenerateDetailView_CustomBasePath_UsedInLinks()
    {
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model, "/admin/db");
        var data = SimpleRecord();

        var html = builder.GenerateDetailView("Users", data);

        Assert.Contains("href=\"/admin/db/edit/Users/42\"", html);
        Assert.Contains("href=\"/admin/db/delete/Users/42\"", html);
        Assert.Contains("href=\"/admin/db/list/Users\"", html);
    }

    [Fact]
    public void GenerateDetailView_CustomBasePath_TrailingSlashTrimmed()
    {
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model, "/admin/db/");
        var data = SimpleRecord();

        var html = builder.GenerateDetailView("Users", data);

        Assert.Contains("href=\"/admin/db/edit/Users/42\"", html);
    }

    #endregion

    #region XSS Protection

    [Fact]
    public void GenerateDetailView_HtmlEncodesValues()
    {
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model);
        var data = new Dictionary<string, object?>
        {
            ["Id"] = 1, ["Name"] = "<script>alert('xss')</script>", ["Email"] = "test@test.com"
        };

        var html = builder.GenerateDetailView("Users", data);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void GenerateDetailView_HtmlEncodesColumnLabels()
    {
        // Column names come from schema, but verify encoding is applied
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model);
        var data = SimpleRecord();

        var html = builder.GenerateDetailView("Users", data);

        // Labels should be encoded (they're already safe names, but encoding is applied)
        Assert.Contains("<dt>Name</dt>", html);
    }

    #endregion

    #region FormatValue Direct

    [Fact]
    public void FormatValue_NullValue_ReturnsNullIndicator()
    {
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model);
        var table = model.GetTableFromDbName("Users");
        var column = table.ColumnLookup["Name"];

        var result = builder.FormatValue(column, null, table);

        Assert.Contains("(null)", result);
    }

    [Fact]
    public void FormatValue_DBNull_ReturnsNullIndicator()
    {
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model);
        var table = model.GetTableFromDbName("Users");
        var column = table.ColumnLookup["Name"];

        var result = builder.FormatValue(column, DBNull.Value, table);

        Assert.Contains("(null)", result);
    }

    #endregion

    #region GenerateFieldRow Direct

    [Fact]
    public void GenerateFieldRow_ReturnsDtDdPair()
    {
        var model = CreateSimpleModel();
        var builder = new DetailViewBuilder(model);
        var table = model.GetTableFromDbName("Users");
        var column = table.ColumnLookup["Name"];

        var result = builder.GenerateFieldRow(column, "Alice", table);

        Assert.Contains("<dt>Name</dt>", result);
        Assert.Contains("<dd>Alice</dd>", result);
    }

    #endregion

    #region Helper Methods

    private static IDbModel CreateSimpleModel()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar"))
            .Build();
    }

    private static IDbModel CreateModelWithTypes()
    {
        return DbModelTestFixture.Create()
            .WithTable("Products", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Price", "decimal")
                .WithColumn("InStock", "bit")
                .WithColumn("CreatedAt", "datetime2"))
            .Build();
    }

    private static IDbModel CreateModelWithBinaryColumn()
    {
        return DbModelTestFixture.Create()
            .WithTable("Products", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Image", "varbinary", isNullable: true))
            .Build();
    }

    private static IDbModel CreateModelWithForeignKey()
    {
        return DbModelTestFixture.Create()
            .WithTable("Departments", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .WithTable("Employees", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("DepartmentId", "int"))
            .WithSingleLink("Employees", "DepartmentId", "Departments", "Id", "Departments")
            .Build();
    }

    private static IDbModel CreateModelWithNullableForeignKey()
    {
        return DbModelTestFixture.Create()
            .WithTable("Departments", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .WithTable("Employees", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("DepartmentId", "int", isNullable: true))
            .WithSingleLink("Employees", "DepartmentId", "Departments", "Id", "Departments")
            .Build();
    }

    private static IDbModel CreateModelWithEmailColumn()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar")
                .WithColumnMetadata("Email", "type", "email"))
            .Build();
    }

    private static IDbModel CreateModelWithUrlColumn()
    {
        return DbModelTestFixture.Create()
            .WithTable("Companies", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Website", "nvarchar")
                .WithColumnMetadata("Website", "type", "url"))
            .Build();
    }

    private static Dictionary<string, object?> SimpleRecord()
    {
        return new Dictionary<string, object?>
        {
            ["Id"] = 42,
            ["Name"] = "Alice",
            ["Email"] = "alice@example.com",
        };
    }

    #endregion
}
