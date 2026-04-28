using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Views;

namespace BifrostQL.Core.Test.Views;

public class ListViewBuilderTests
{
    #region Basic Structure

    [Fact]
    public void GenerateListView_ContainsListWrapper()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("class=\"bifrost-list\"", html);
    }

    [Fact]
    public void GenerateListView_ContainsTitle()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("<h1>Users</h1>", html);
    }

    [Fact]
    public void GenerateListView_ContainsNewButton()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("href=\"/bifrost/new/Users\"", html);
        Assert.Contains("btn-primary", html);
        Assert.Contains("New User", html);
    }

    [Fact]
    public void GenerateListView_ContainsTable()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("<table>", html);
        Assert.Contains("<thead>", html);
        Assert.Contains("<tbody>", html);
        Assert.Contains("</table>", html);
    }

    #endregion

    #region Column Headers

    [Fact]
    public void GenerateListView_HeadersHaveSortLinks()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("sort=Id", html);
        Assert.Contains("sort=Name", html);
        Assert.Contains("sort=Email", html);
    }

    [Fact]
    public void GenerateListView_Headers_DefaultSortDirectionIsAsc()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("dir=asc", html);
    }

    [Fact]
    public void GenerateListView_CurrentSortAsc_LinkTogglesDesc()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination,
            currentSort: "Name", currentDir: "asc");

        // The Name column header should link to desc
        var nameHeader = ExtractTh(html, "Name");
        Assert.Contains("dir=desc", nameHeader);
    }

    [Fact]
    public void GenerateListView_CurrentSortDesc_LinkTogglesAsc()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination,
            currentSort: "Name", currentDir: "desc");

        // The Name column header should link to asc
        var nameHeader = ExtractTh(html, "Name");
        Assert.Contains("dir=asc", nameHeader);
    }

    [Fact]
    public void GenerateListView_CurrentSort_ShowsSortIndicator()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination,
            currentSort: "Name", currentDir: "asc");

        // Sort indicator should appear in the sorted column header
        var nameHeader = ExtractTh(html, "Name");
        Assert.Contains("&#9650;", nameHeader); // up arrow for asc
    }

    [Fact]
    public void GenerateListView_DescSort_ShowsDownArrow()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination,
            currentSort: "Name", currentDir: "desc");

        var nameHeader = ExtractTh(html, "Name");
        Assert.Contains("&#9660;", nameHeader); // down arrow for desc
    }

    [Fact]
    public void GenerateListView_ContainsActionsHeader()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("<th>Actions</th>", html);
    }

    #endregion

    #region Table Rows

    [Fact]
    public void GenerateListView_RowsDisplayValues()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("<td>42</td>", html);
        Assert.Contains("alice@example.com", html);
    }

    [Fact]
    public void GenerateListView_FirstTextColumn_LinksToDetail()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("href=\"/bifrost/view/Users/42\"", html);
        Assert.Contains(">Alice</a>", html);
    }

    [Fact]
    public void GenerateListView_RowActions_ContainsViewEditDelete()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains(">View</a>", html);
        Assert.Contains(">Edit</a>", html);
        Assert.Contains(">Delete</a>", html);
    }

    [Fact]
    public void GenerateListView_RowActions_CorrectUrls()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("href=\"/bifrost/view/Users/42\"", html);
        Assert.Contains("href=\"/bifrost/edit/Users/42\"", html);
        Assert.Contains("href=\"/bifrost/delete/Users/42\"", html);
    }

    #endregion

    #region Empty List

    [Fact]
    public void GenerateListView_EmptyList_ShowsMessage()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = new List<IReadOnlyDictionary<string, object?>>();
        var pagination = new PaginationInfo(1, 25, 0);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("No records found", html);
    }

    [Fact]
    public void GenerateListView_EmptyList_NoTable()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = new List<IReadOnlyDictionary<string, object?>>();
        var pagination = new PaginationInfo(1, 25, 0);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.DoesNotContain("<table>", html);
    }

    #endregion

    #region Pagination

    [Fact]
    public void GenerateListView_Pagination_ShowsPageInfo()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(2, 25, 250);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("Page 2 of 10", html);
    }

    [Fact]
    public void GenerateListView_Pagination_ContainsNavElement()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(2, 25, 250);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("class=\"pagination\"", html);
    }

    [Fact]
    public void GenerateListView_MiddlePage_AllLinksEnabled()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(5, 25, 250);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains(">First</a>", html);
        Assert.Contains(">Previous</a>", html);
        Assert.Contains(">Next</a>", html);
        Assert.Contains(">Last</a>", html);
    }

    [Fact]
    public void GenerateListView_FirstPage_PreviousDisabled()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 250);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("<span class=\"btn-secondary disabled\">First</span>", html);
        Assert.Contains("<span class=\"btn-secondary disabled\">Previous</span>", html);
        Assert.Contains(">Next</a>", html);
        Assert.Contains(">Last</a>", html);
    }

    [Fact]
    public void GenerateListView_LastPage_NextDisabled()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(10, 25, 250);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains(">First</a>", html);
        Assert.Contains(">Previous</a>", html);
        Assert.Contains("<span class=\"btn-secondary disabled\">Next</span>", html);
        Assert.Contains("<span class=\"btn-secondary disabled\">Last</span>", html);
    }

    [Fact]
    public void GenerateListView_MiddlePage_CorrectPageLinks()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(5, 25, 250);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("page=1", html); // First
        Assert.Contains("page=4", html); // Previous
        Assert.Contains("page=6", html); // Next
        Assert.Contains("page=10", html); // Last
    }

    [Fact]
    public void GenerateListView_Pagination_PreservesSortParams()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(2, 25, 250);

        var html = builder.GenerateListView("Users", records, pagination,
            currentSort: "Name", currentDir: "asc");

        // Pagination links should include sort params
        var navSection = ExtractNav(html);
        Assert.Contains("sort=Name", navSection);
        Assert.Contains("dir=asc", navSection);
    }

    [Fact]
    public void GenerateListView_Pagination_PreservesSearchParam()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(2, 25, 250);

        var html = builder.GenerateListView("Users", records, pagination,
            currentSearch: "alice");

        var navSection = ExtractNav(html);
        Assert.Contains("search=alice", navSection);
    }

    #endregion

    #region Search Form

    [Fact]
    public void GenerateListView_ContainsSearchForm()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("class=\"bifrost-search\"", html);
        Assert.Contains("type=\"search\"", html);
        Assert.Contains("name=\"search\"", html);
    }

    [Fact]
    public void GenerateListView_SearchForm_PreservesSearchValue()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination,
            currentSearch: "alice");

        Assert.Contains("value=\"alice\"", html);
    }

    [Fact]
    public void GenerateListView_SearchForm_ShowsClearButton_WhenSearchActive()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination,
            currentSearch: "alice");

        Assert.Contains(">Clear</a>", html);
    }

    [Fact]
    public void GenerateListView_SearchForm_NoClearButton_WhenNoSearch()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.DoesNotContain(">Clear</a>", html);
    }

    [Fact]
    public void GenerateListView_SearchForm_PreservesSortAsHiddenFields()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination,
            currentSort: "Name", currentDir: "desc");

        var searchForm = ExtractSearchForm(html);
        Assert.Contains("type=\"hidden\"", searchForm);
        Assert.Contains("name=\"sort\"", searchForm);
        Assert.Contains("value=\"Name\"", searchForm);
        Assert.Contains("name=\"dir\"", searchForm);
        Assert.Contains("value=\"desc\"", searchForm);
    }

    #endregion

    #region Custom Base Path

    [Fact]
    public void GenerateListView_CustomBasePath_UsedInLinks()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model, "/admin");
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("href=\"/admin/new/Users\"", html);
        Assert.Contains("href=\"/admin/view/Users/42\"", html);
        Assert.Contains("href=\"/admin/edit/Users/42\"", html);
        Assert.Contains("href=\"/admin/delete/Users/42\"", html);
    }

    #endregion

    #region XSS Protection

    [Fact]
    public void GenerateListView_HtmlEncodesValues()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "<script>alert('xss')</script>", ["Email"] = "test@test.com" }
        };
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void GenerateListView_HtmlEncodesSearchValue()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = CreateRecords();
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination,
            currentSearch: "<script>alert('xss')</script>");

        Assert.DoesNotContain("<script>alert", html);
    }

    #endregion

    #region Max Columns

    [Fact]
    public void GenerateListView_LimitsDisplayColumns()
    {
        var model = CreateModelWithManyColumns();
        var builder = new ListViewBuilder(model, maxColumns: 3);
        var records = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["Id"] = 1, ["Col1"] = "a", ["Col2"] = "b", ["Col3"] = "c",
                ["Col4"] = "d", ["Col5"] = "e"
            }
        };
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Items", records, pagination);

        // Should show at most 3 columns plus Actions
        var headerCount = CountOccurrences(html, "<th>");
        Assert.Equal(4, headerCount); // 3 data columns + Actions
    }

    #endregion

    #region Value Formatting in Cells

    [Fact]
    public void GenerateListView_BooleanColumn_DisplaysYesNo()
    {
        var model = CreateModelWithBoolColumn();
        var builder = new ListViewBuilder(model);
        var records = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Widget", ["Active"] = true }
        };
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Products", records, pagination);

        Assert.Contains("Yes", html);
    }

    [Fact]
    public void GenerateListView_DateColumn_DisplaysTimeElement()
    {
        var model = CreateModelWithDateColumn();
        var builder = new ListViewBuilder(model);
        var records = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Widget", ["CreatedAt"] = new DateTime(2024, 1, 15) }
        };
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Products", records, pagination);

        Assert.Contains("<time datetime=", html);
    }

    [Fact]
    public void GenerateListView_NullValue_DisplaysEmpty()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var records = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = null, ["Email"] = "test@test.com" }
        };
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        // Null values in list view show as empty cells (not the (null) indicator used in detail view)
        Assert.DoesNotContain("(null)", html);
    }

    [Fact]
    public void GenerateListView_LongText_IsTruncated()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var longText = new string('x', 200);
        var records = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Test", ["Email"] = longText }
        };
        var pagination = new PaginationInfo(1, 25, 1);

        var html = builder.GenerateListView("Users", records, pagination);

        Assert.Contains("&hellip;", html);
    }

    #endregion

    #region GenerateTableHeader Direct

    [Fact]
    public void GenerateTableHeader_ReturnsThead()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var table = model.GetTableFromDbName("Users");
        var columns = table.Columns.ToList();

        var html = builder.GenerateTableHeader(columns);

        Assert.Contains("<thead>", html);
        Assert.Contains("</thead>", html);
    }

    #endregion

    #region GenerateTableRow Direct

    [Fact]
    public void GenerateTableRow_ReturnsTr()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var table = model.GetTableFromDbName("Users");
        var columns = table.Columns.ToList();
        var record = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Alice", ["Email"] = "alice@test.com" };

        var html = builder.GenerateTableRow(record, columns, table);

        Assert.Contains("<tr>", html);
        Assert.Contains("</tr>", html);
    }

    #endregion

    #region GeneratePagination Direct

    [Fact]
    public void GeneratePagination_ReturnsNav()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);
        var pagination = new PaginationInfo(1, 25, 100);

        var html = builder.GeneratePagination(pagination);

        Assert.Contains("<nav", html);
        Assert.Contains("</nav>", html);
    }

    #endregion

    #region GenerateSearchForm Direct

    [Fact]
    public void GenerateSearchForm_ReturnsForm()
    {
        var model = CreateSimpleModel();
        var builder = new ListViewBuilder(model);

        var html = builder.GenerateSearchForm("Users");

        Assert.Contains("<form method=\"GET\"", html);
        Assert.Contains("</form>", html);
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

    private static IDbModel CreateModelWithManyColumns()
    {
        return DbModelTestFixture.Create()
            .WithTable("Items", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Col1", "nvarchar")
                .WithColumn("Col2", "nvarchar")
                .WithColumn("Col3", "nvarchar")
                .WithColumn("Col4", "nvarchar")
                .WithColumn("Col5", "nvarchar"))
            .Build();
    }

    private static IDbModel CreateModelWithBoolColumn()
    {
        return DbModelTestFixture.Create()
            .WithTable("Products", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Active", "bit"))
            .Build();
    }

    private static IDbModel CreateModelWithDateColumn()
    {
        return DbModelTestFixture.Create()
            .WithTable("Products", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("CreatedAt", "datetime2"))
            .Build();
    }

    private static List<IReadOnlyDictionary<string, object?>> CreateRecords()
    {
        return new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Id"] = 42, ["Name"] = "Alice", ["Email"] = "alice@example.com" }
        };
    }

    /// <summary>
    /// Extracts the content of the th element containing the specified column name.
    /// </summary>
    private static string ExtractTh(string html, string columnLabel)
    {
        var searchLabel = $">{columnLabel}</a>";
        var idx = html.IndexOf(searchLabel, StringComparison.Ordinal);
        if (idx < 0) return "";

        var start = html.LastIndexOf("<th>", idx, StringComparison.Ordinal);
        if (start < 0) return "";

        var end = html.IndexOf("</th>", idx, StringComparison.Ordinal);
        if (end < 0) return "";

        return html.Substring(start, end - start + 5);
    }

    /// <summary>
    /// Extracts the nav element from the HTML.
    /// </summary>
    private static string ExtractNav(string html)
    {
        var start = html.IndexOf("<nav", StringComparison.Ordinal);
        if (start < 0) return "";

        var end = html.IndexOf("</nav>", start, StringComparison.Ordinal);
        if (end < 0) return "";

        return html.Substring(start, end - start + 6);
    }

    /// <summary>
    /// Extracts the search form from the HTML.
    /// </summary>
    private static string ExtractSearchForm(string html)
    {
        var searchClass = "class=\"bifrost-search\"";
        var idx = html.IndexOf(searchClass, StringComparison.Ordinal);
        if (idx < 0) return "";

        var start = html.LastIndexOf("<form", idx, StringComparison.Ordinal);
        if (start < 0) return "";

        var end = html.IndexOf("</form>", idx, StringComparison.Ordinal);
        if (end < 0) return "";

        return html.Substring(start, end - start + 7);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    #endregion
}
