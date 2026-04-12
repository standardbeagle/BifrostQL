using BifrostQL.Core.Forms;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Test.Forms;

public class EnumHandlerTests
{
    #region ShouldUseRadio

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(10, false)]
    public void ShouldUseRadio_ReturnsCorrectResult(int count, bool expected)
    {
        Assert.Equal(expected, EnumHandler.ShouldUseRadio(count));
    }

    #endregion

    #region GenerateRadioGroup

    [Fact]
    public void GenerateRadioGroup_GeneratesFieldsetWithLegend()
    {
        var column = CreateColumn("Status", isNullable: true);
        var values = new[] { "active", "inactive" };

        var html = EnumHandler.GenerateRadioGroup(column, "Status", values);

        Assert.Contains("<fieldset>", html);
        Assert.Contains("<legend>Status</legend>", html);
        Assert.Contains("</fieldset>", html);
    }

    [Fact]
    public void GenerateRadioGroup_GeneratesRadioInputsForEachValue()
    {
        var column = CreateColumn("Status", isNullable: true);
        var values = new[] { "active", "inactive", "pending" };

        var html = EnumHandler.GenerateRadioGroup(column, "Status", values);

        Assert.Contains("type=\"radio\"", html);
        Assert.Contains("value=\"active\"", html);
        Assert.Contains("value=\"inactive\"", html);
        Assert.Contains("value=\"pending\"", html);
    }

    [Fact]
    public void GenerateRadioGroup_AllRadiosShareSameName()
    {
        var column = CreateColumn("Status", isNullable: true);
        var values = new[] { "active", "inactive" };

        var html = EnumHandler.GenerateRadioGroup(column, "Status", values);

        // Both radio inputs should have name="Status"
        var count = CountOccurrences(html, "name=\"Status\"");
        Assert.Equal(2, count);
    }

    [Fact]
    public void GenerateRadioGroup_WithDisplayNames_UsesDisplayText()
    {
        var column = CreateColumn("Status", isNullable: true);
        var values = new[] { "active", "inactive", "pending" };
        var displayNames = new Dictionary<string, string>
        {
            ["active"] = "Active",
            ["inactive"] = "Inactive",
            ["pending"] = "Pending Approval"
        };

        var html = EnumHandler.GenerateRadioGroup(column, "Status", values, displayNames);

        Assert.Contains("> Active</label>", html);
        Assert.Contains("> Inactive</label>", html);
        Assert.Contains("> Pending Approval</label>", html);
    }

    [Fact]
    public void GenerateRadioGroup_WithoutDisplayNames_UsesRawValues()
    {
        var column = CreateColumn("Status", isNullable: true);
        var values = new[] { "active", "inactive" };

        var html = EnumHandler.GenerateRadioGroup(column, "Status", values);

        Assert.Contains("> active</label>", html);
        Assert.Contains("> inactive</label>", html);
    }

    [Fact]
    public void GenerateRadioGroup_CurrentValue_MarksChecked()
    {
        var column = CreateColumn("Status", isNullable: true);
        var values = new[] { "active", "inactive", "pending" };

        var html = EnumHandler.GenerateRadioGroup(column, "Status", values, currentValue: "inactive");

        Assert.Contains("value=\"inactive\" checked>", html);
        Assert.DoesNotContain("value=\"active\" checked", html);
        Assert.DoesNotContain("value=\"pending\" checked", html);
    }

    [Fact]
    public void GenerateRadioGroup_NoCurrentValue_NoneChecked()
    {
        var column = CreateColumn("Status", isNullable: true);
        var values = new[] { "active", "inactive" };

        var html = EnumHandler.GenerateRadioGroup(column, "Status", values);

        Assert.DoesNotContain("checked", html);
    }

    [Fact]
    public void GenerateRadioGroup_RequiredColumn_AddsRequiredToFirstRadio()
    {
        var column = CreateColumn("Status", isNullable: false);
        var values = new[] { "active", "inactive" };

        var html = EnumHandler.GenerateRadioGroup(column, "Status", values);

        // First radio has required
        Assert.Contains("value=\"active\" required>", html);
        // Second does not
        Assert.DoesNotContain("value=\"inactive\" required", html);
    }

    [Fact]
    public void GenerateRadioGroup_NullableColumn_NoRequired()
    {
        var column = CreateColumn("Status", isNullable: true);
        var values = new[] { "active", "inactive" };

        var html = EnumHandler.GenerateRadioGroup(column, "Status", values);

        Assert.DoesNotContain("required", html);
    }

    [Fact]
    public void GenerateRadioGroup_HtmlEncodesValues()
    {
        var column = CreateColumn("Status", isNullable: true);
        var values = new[] { "<script>alert('xss')</script>" };

        var html = EnumHandler.GenerateRadioGroup(column, "Status", values);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void GenerateRadioGroup_WrapsRadiosInLabels()
    {
        var column = CreateColumn("Status", isNullable: true);
        var values = new[] { "active" };

        var html = EnumHandler.GenerateRadioGroup(column, "Status", values);

        Assert.Contains("<label><input type=\"radio\"", html);
        Assert.Contains("</label>", html);
    }

    #endregion

    #region GenerateEnumSelect

    [Fact]
    public void GenerateEnumSelect_GeneratesSelectElement()
    {
        var column = CreateColumn("Country", isNullable: true);
        var values = new[] { "US", "CA", "GB", "DE", "FR" };

        var html = EnumHandler.GenerateEnumSelect(column, values);

        Assert.Contains("<select", html);
        Assert.Contains("</select>", html);
        Assert.Contains("name=\"Country\"", html);
        Assert.Contains("id=\"country\"", html);
    }

    [Fact]
    public void GenerateEnumSelect_IncludesPlaceholderOption()
    {
        var column = CreateColumn("Country", isNullable: true);
        var values = new[] { "US", "CA" };

        var html = EnumHandler.GenerateEnumSelect(column, values);

        Assert.Contains("<option value=\"\">-- Select --</option>", html);
    }

    [Fact]
    public void GenerateEnumSelect_RendersAllOptions()
    {
        var column = CreateColumn("Country", isNullable: true);
        var values = new[] { "US", "CA", "GB", "DE", "FR" };

        var html = EnumHandler.GenerateEnumSelect(column, values);

        Assert.Contains("<option value=\"US\">US</option>", html);
        Assert.Contains("<option value=\"CA\">CA</option>", html);
        Assert.Contains("<option value=\"GB\">GB</option>", html);
        Assert.Contains("<option value=\"DE\">DE</option>", html);
        Assert.Contains("<option value=\"FR\">FR</option>", html);
    }

    [Fact]
    public void GenerateEnumSelect_WithDisplayNames_UsesDisplayText()
    {
        var column = CreateColumn("Country", isNullable: true);
        var values = new[] { "US", "CA" };
        var displayNames = new Dictionary<string, string>
        {
            ["US"] = "United States",
            ["CA"] = "Canada"
        };

        var html = EnumHandler.GenerateEnumSelect(column, values, displayNames);

        Assert.Contains("<option value=\"US\">United States</option>", html);
        Assert.Contains("<option value=\"CA\">Canada</option>", html);
    }

    [Fact]
    public void GenerateEnumSelect_CurrentValue_MarksSelected()
    {
        var column = CreateColumn("Country", isNullable: true);
        var values = new[] { "US", "CA", "GB" };

        var html = EnumHandler.GenerateEnumSelect(column, values, currentValue: "CA");

        Assert.Contains("<option value=\"CA\" selected>CA</option>", html);
        Assert.DoesNotContain("<option value=\"US\" selected", html);
        Assert.DoesNotContain("<option value=\"GB\" selected", html);
    }

    [Fact]
    public void GenerateEnumSelect_RequiredColumn_HasRequiredAttribute()
    {
        var column = CreateColumn("Country", isNullable: false);
        var values = new[] { "US", "CA" };

        var html = EnumHandler.GenerateEnumSelect(column, values);

        Assert.Contains("required", html);
        Assert.Contains("aria-required=\"true\"", html);
    }

    [Fact]
    public void GenerateEnumSelect_NullableColumn_NoRequiredAttribute()
    {
        var column = CreateColumn("Country", isNullable: true);
        var values = new[] { "US", "CA" };

        var html = EnumHandler.GenerateEnumSelect(column, values);

        Assert.DoesNotContain("required", html);
        Assert.DoesNotContain("aria-required", html);
    }

    [Fact]
    public void GenerateEnumSelect_HtmlEncodesDisplayNames()
    {
        var column = CreateColumn("Country", isNullable: true);
        var values = new[] { "XSS" };
        var displayNames = new Dictionary<string, string>
        {
            ["XSS"] = "R&D <Department>"
        };

        var html = EnumHandler.GenerateEnumSelect(column, values, displayNames);

        Assert.DoesNotContain("<Department>", html);
        Assert.Contains("R&amp;D &lt;Department&gt;", html);
    }

    #endregion

    #region Helper Methods

    private static ColumnDto CreateColumn(string name, bool isNullable = true)
    {
        return new ColumnDto
        {
            ColumnName = name,
            GraphQlName = name.ToLowerInvariant(),
            NormalizedName = name,
            DataType = "nvarchar",
            IsNullable = isNullable
        };
    }

    private static int CountOccurrences(string source, string search)
    {
        var count = 0;
        var idx = 0;
        while ((idx = source.IndexOf(search, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += search.Length;
        }
        return count;
    }

    #endregion
}
