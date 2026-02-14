using BifrostQL.Core.Forms;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;

namespace BifrostQL.Core.Test.Forms;

public class BifrostFormBuilderTests
{
    #region Insert Mode - Basic Structure

    [Fact]
    public void GenerateForm_Insert_ContainsFormTag()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("<form method=\"POST\"", html);
        Assert.Contains("action=\"/bifrost/form/Users/insert\"", html);
        Assert.Contains("</form>", html);
    }

    [Fact]
    public void GenerateForm_Insert_ContainsSubmitButton()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("<button type=\"submit\"", html);
        Assert.Contains("Create", html);
    }

    [Fact]
    public void GenerateForm_Insert_ContainsCancelLink()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("class=\"btn-secondary\"", html);
        Assert.Contains("Cancel", html);
    }

    [Fact]
    public void GenerateForm_Insert_ContainsFormGroups()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("class=\"form-group\"", html);
    }

    [Fact]
    public void GenerateForm_Insert_ContainsLabels()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("<label for=\"name\">Name</label>", html);
        Assert.Contains("<label for=\"email\">Email</label>", html);
    }

    [Fact]
    public void GenerateForm_Insert_ContainsInputFields()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("name=\"Name\"", html);
        Assert.Contains("name=\"Email\"", html);
    }

    #endregion

    #region Insert Mode - Identity Column Exclusion

    [Fact]
    public void GenerateForm_Insert_ExcludesIdentityColumn()
    {
        var model = CreateModelWithIdentity();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Products", FormMode.Insert);

        // The identity column should not appear as a visible input
        Assert.DoesNotContain("name=\"Id\"", html);
        Assert.Contains("name=\"Name\"", html);
    }

    #endregion

    #region Insert Mode - Type Mapping

    [Fact]
    public void GenerateForm_Insert_NumericColumn_HasNumberInput()
    {
        var model = CreateModelWithTypes();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Products", FormMode.Insert);

        Assert.Contains("type=\"number\"", html);
        Assert.Contains("step=\"0.01\"", html);
    }

    [Fact]
    public void GenerateForm_Insert_BoolColumn_HasCheckboxInput()
    {
        var model = CreateModelWithTypes();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Products", FormMode.Insert);

        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("name=\"InStock\"", html);
    }

    [Fact]
    public void GenerateForm_Insert_DateColumn_HasDateTimeLocalInput()
    {
        var model = CreateModelWithTypes();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Products", FormMode.Insert);

        Assert.Contains("type=\"datetime-local\"", html);
    }

    [Fact]
    public void GenerateForm_Insert_TextColumn_HasTextarea()
    {
        var model = CreateModelWithTypes();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Products", FormMode.Insert);

        Assert.Contains("<textarea", html);
        Assert.Contains("rows=\"5\"", html);
    }

    #endregion

    #region Insert Mode - Required Attributes

    [Fact]
    public void GenerateForm_Insert_RequiredColumn_HasRequiredAttribute()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("required", html);
        Assert.Contains("aria-required=\"true\"", html);
    }

    [Fact]
    public void GenerateForm_Insert_NullableColumn_NoRequiredAttribute()
    {
        var model = CreateModelWithNullable();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        // The Bio field is nullable so should not have required
        // Count occurrences - we need to verify Bio input does not have required
        var bioSection = ExtractFormGroup(html, "Bio");
        Assert.DoesNotContain("required", bioSection);
    }

    #endregion

    #region Update Mode

    [Fact]
    public void GenerateForm_Update_ContainsUpdateAction()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?> { ["Id"] = "42", ["Name"] = "Alice", ["Email"] = "alice@example.com" };

        var html = builder.GenerateForm("Users", FormMode.Update, values);

        Assert.Contains("action=\"/bifrost/form/Users/update/42\"", html);
    }

    [Fact]
    public void GenerateForm_Update_PrePopulatesValues()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?> { ["Id"] = "42", ["Name"] = "Alice", ["Email"] = "alice@example.com" };

        var html = builder.GenerateForm("Users", FormMode.Update, values);

        Assert.Contains("value=\"Alice\"", html);
        Assert.Contains("value=\"alice@example.com\"", html);
    }

    [Fact]
    public void GenerateForm_Update_PrimaryKeyAsHiddenField()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?> { ["Id"] = "42", ["Name"] = "Alice", ["Email"] = "alice@example.com" };

        var html = builder.GenerateForm("Users", FormMode.Update, values);

        Assert.Contains("type=\"hidden\"", html);
        Assert.Contains("name=\"Id\"", html);
        Assert.Contains("value=\"42\"", html);
    }

    [Fact]
    public void GenerateForm_Update_SubmitLabelIsUpdate()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?> { ["Id"] = "42", ["Name"] = "Alice", ["Email"] = "alice@example.com" };

        var html = builder.GenerateForm("Users", FormMode.Update, values);

        Assert.Contains(">Update</button>", html);
    }

    [Fact]
    public void GenerateForm_Update_BoolColumn_CheckedWhenTrue()
    {
        var model = CreateModelWithTypes();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?> { ["Id"] = "1", ["Name"] = "Widget", ["Description"] = "", ["Price"] = "9.99", ["InStock"] = "true", ["CreatedAt"] = "2024-01-01" };

        var html = builder.GenerateForm("Products", FormMode.Update, values);

        Assert.Contains("checked", html);
    }

    #endregion

    #region Delete Mode

    [Fact]
    public void GenerateForm_Delete_ContainsDeleteAction()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?> { ["Id"] = "42", ["Name"] = "Alice", ["Email"] = "alice@example.com" };

        var html = builder.GenerateForm("Users", FormMode.Delete, values);

        Assert.Contains("action=\"/bifrost/form/Users/delete/42\"", html);
    }

    [Fact]
    public void GenerateForm_Delete_ContainsConfirmationText()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?> { ["Id"] = "42", ["Name"] = "Alice", ["Email"] = "alice@example.com" };

        var html = builder.GenerateForm("Users", FormMode.Delete, values);

        Assert.Contains("Are you sure you want to delete", html);
    }

    [Fact]
    public void GenerateForm_Delete_ContainsHiddenPrimaryKey()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?> { ["Id"] = "42", ["Name"] = "Alice", ["Email"] = "alice@example.com" };

        var html = builder.GenerateForm("Users", FormMode.Delete, values);

        Assert.Contains("type=\"hidden\"", html);
        Assert.Contains("name=\"Id\"", html);
        Assert.Contains("value=\"42\"", html);
    }

    [Fact]
    public void GenerateForm_Delete_DisplaysRecordSummary()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?> { ["Id"] = "42", ["Name"] = "Alice", ["Email"] = "alice@example.com" };

        var html = builder.GenerateForm("Users", FormMode.Delete, values);

        Assert.Contains("<dl", html);
        Assert.Contains("<dt>", html);
        Assert.Contains("<dd>Alice</dd>", html);
        Assert.Contains("<dd>alice@example.com</dd>", html);
    }

    [Fact]
    public void GenerateForm_Delete_ConfirmButton()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?> { ["Id"] = "42", ["Name"] = "Alice", ["Email"] = "alice@example.com" };

        var html = builder.GenerateForm("Users", FormMode.Delete, values);

        Assert.Contains("name=\"confirm\"", html);
        Assert.Contains("value=\"yes\"", html);
    }

    #endregion

    #region Validation Errors Display

    [Fact]
    public void GenerateForm_WithErrors_DisplaysErrorMessages()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);
        var errors = new List<ValidationError>
        {
            new ValidationError("Name", "Name is required")
        };

        var html = builder.GenerateForm("Users", FormMode.Insert, errors: errors);

        Assert.Contains("class=\"error-message\"", html);
        Assert.Contains("Name is required", html);
    }

    [Fact]
    public void GenerateForm_WithErrors_MarksFieldAsInvalid()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);
        var errors = new List<ValidationError>
        {
            new ValidationError("Name", "Name is required")
        };

        var html = builder.GenerateForm("Users", FormMode.Insert, errors: errors);

        Assert.Contains("aria-invalid=\"true\"", html);
        Assert.Contains("aria-describedby=", html);
    }

    [Fact]
    public void GenerateForm_WithErrors_AddsErrorClassToFormGroup()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);
        var errors = new List<ValidationError>
        {
            new ValidationError("Name", "Name is required")
        };

        var html = builder.GenerateForm("Users", FormMode.Insert, errors: errors);

        Assert.Contains("class=\"form-group error\"", html);
    }

    #endregion

    #region Audit Column Exclusion

    [Fact]
    public void GenerateForm_Insert_ExcludesAuditColumns()
    {
        var model = CreateModelWithAuditColumns();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.DoesNotContain("name=\"created_at\"", html);
        Assert.DoesNotContain("name=\"updated_at\"", html);
        Assert.Contains("name=\"Name\"", html);
    }

    [Fact]
    public void GenerateForm_Update_ExcludesAuditColumns()
    {
        var model = CreateModelWithAuditColumns();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?> { ["Id"] = "1", ["Name"] = "Alice", ["created_at"] = "2024-01-01", ["updated_at"] = "2024-01-02" };

        var html = builder.GenerateForm("Users", FormMode.Update, values);

        Assert.DoesNotContain("name=\"created_at\"", html);
        Assert.DoesNotContain("name=\"updated_at\"", html);
    }

    #endregion

    #region Custom Base Path

    [Fact]
    public void GenerateForm_CustomBasePath_UsedInAction()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model, "/admin/db");

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("action=\"/admin/db/form/Users/insert\"", html);
    }

    [Fact]
    public void GenerateForm_CustomBasePath_TrailingSlashTrimmed()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model, "/admin/db/");

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("action=\"/admin/db/form/Users/insert\"", html);
    }

    #endregion

    #region XSS Protection

    [Fact]
    public void GenerateForm_Update_HtmlEncodesValues()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?> { ["Id"] = "1", ["Name"] = "<script>alert('xss')</script>", ["Email"] = "test@test.com" };

        var html = builder.GenerateForm("Users", FormMode.Update, values);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    #endregion

    #region GenerateFormControl

    [Fact]
    public void GenerateFormControl_ReturnsFormGroup()
    {
        var column = new ColumnDto
        {
            ColumnName = "Name", GraphQlName = "name", NormalizedName = "Name",
            DataType = "nvarchar", IsNullable = false
        };
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateFormControl(column, FormMode.Insert);

        Assert.Contains("class=\"form-group\"", html);
        Assert.Contains("<label", html);
        Assert.Contains("type=\"text\"", html);
    }

    #endregion

    #region Label Formatting

    [Fact]
    public void GenerateForm_UnderscoreColumns_FormatsLabels()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("first_name", "nvarchar"))
            .Build();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("First name", html);
    }

    #endregion

    #region Foreign Key - Select Generation

    [Fact]
    public void GenerateForm_Insert_ForeignKeyColumn_RendersSelect()
    {
        var model = CreateModelWithForeignKey();
        var builder = new BifrostFormBuilder(model);
        var fkOptions = new Dictionary<string, IReadOnlyList<(string value, string displayText)>>
        {
            ["DepartmentId"] = new List<(string, string)>
            {
                ("1", "Engineering"),
                ("2", "Sales")
            }
        };

        var html = builder.GenerateForm("Employees", FormMode.Insert, foreignKeyOptions: fkOptions);

        Assert.Contains("<select", html);
        Assert.Contains("name=\"DepartmentId\"", html);
        Assert.Contains("<option value=\"1\">Engineering</option>", html);
        Assert.Contains("<option value=\"2\">Sales</option>", html);
    }

    [Fact]
    public void GenerateForm_Insert_ForeignKeyColumn_DoesNotRenderTextInput()
    {
        var model = CreateModelWithForeignKey();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Employees", FormMode.Insert);

        var deptSection = ExtractFormGroup(html, "DepartmentId");
        Assert.DoesNotContain("type=\"number\"", deptSection);
        Assert.DoesNotContain("type=\"text\"", deptSection);
        Assert.Contains("<select", deptSection);
    }

    [Fact]
    public void GenerateForm_Insert_ForeignKeyColumn_RequiredWhenNotNullable()
    {
        var model = CreateModelWithForeignKey();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Employees", FormMode.Insert);

        var deptSection = ExtractFormGroup(html, "DepartmentId");
        Assert.Contains("required", deptSection);
    }

    [Fact]
    public void GenerateForm_Insert_ForeignKeyColumn_NotRequiredWhenNullable()
    {
        var model = CreateModelWithNullableForeignKey();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Employees", FormMode.Insert);

        var deptSection = ExtractFormGroup(html, "DepartmentId");
        Assert.DoesNotContain("required", deptSection);
    }

    [Fact]
    public void GenerateForm_Insert_ForeignKeyColumn_HasPlaceholderOption()
    {
        var model = CreateModelWithForeignKey();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Employees", FormMode.Insert);

        Assert.Contains("-- Select --", html);
    }

    [Fact]
    public void GenerateForm_Insert_ForeignKeyColumn_HasLabel()
    {
        var model = CreateModelWithForeignKey();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Employees", FormMode.Insert);

        Assert.Contains("<label for=\"departmentid\">Department Id</label>", html);
    }

    [Fact]
    public void GenerateForm_Update_ForeignKeyColumn_SelectsCurrentValue()
    {
        var model = CreateModelWithForeignKey();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?>
        {
            ["Id"] = "1", ["Name"] = "Alice", ["DepartmentId"] = "2"
        };
        var fkOptions = new Dictionary<string, IReadOnlyList<(string value, string displayText)>>
        {
            ["DepartmentId"] = new List<(string, string)>
            {
                ("1", "Engineering"),
                ("2", "Sales"),
                ("3", "Marketing")
            }
        };

        var html = builder.GenerateForm("Employees", FormMode.Update, values, foreignKeyOptions: fkOptions);

        Assert.Contains("<option value=\"2\" selected>Sales</option>", html);
        Assert.DoesNotContain("<option value=\"1\" selected", html);
    }

    [Fact]
    public void GenerateForm_Insert_NonForeignKeyColumn_StillRendersInput()
    {
        var model = CreateModelWithForeignKey();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Employees", FormMode.Insert);

        // Name column should still be a text input, not a select
        var nameSection = ExtractFormGroup(html, "Name");
        Assert.Contains("type=\"text\"", nameSection);
        Assert.DoesNotContain("<select", nameSection);
    }

    [Fact]
    public void GenerateForm_Insert_ForeignKeyWithNoOptions_RendersEmptySelect()
    {
        var model = CreateModelWithForeignKey();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Employees", FormMode.Insert);

        var deptSection = ExtractFormGroup(html, "DepartmentId");
        Assert.Contains("<select", deptSection);
        Assert.Contains("-- Select --", deptSection);
    }

    #endregion

    #region File Upload Integration

    [Fact]
    public void GenerateForm_Insert_BinaryColumn_RendersFileInput()
    {
        var model = CreateModelWithBinaryColumn();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Products", FormMode.Insert);

        Assert.Contains("type=\"file\"", html);
        Assert.Contains("name=\"Image\"", html);
    }

    [Fact]
    public void GenerateForm_Insert_BinaryColumn_AddsEnctype()
    {
        var model = CreateModelWithBinaryColumn();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Products", FormMode.Insert);

        Assert.Contains("enctype=\"multipart/form-data\"", html);
    }

    [Fact]
    public void GenerateForm_Insert_NoBinaryColumn_NoEnctype()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.DoesNotContain("enctype", html);
    }

    [Fact]
    public void GenerateForm_Delete_BinaryColumn_NoEnctype()
    {
        var model = CreateModelWithBinaryColumn();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?> { ["Id"] = "1", ["Name"] = "Widget", ["Image"] = "base64data" };

        var html = builder.GenerateForm("Products", FormMode.Delete, values);

        Assert.DoesNotContain("enctype", html);
    }

    [Fact]
    public void GenerateForm_Update_BinaryColumn_WithValue_ShowsHelpText()
    {
        var model = CreateModelWithBinaryColumn();
        var builder = new BifrostFormBuilder(model);
        var values = new Dictionary<string, string?> { ["Id"] = "1", ["Name"] = "Widget", ["Image"] = "base64data" };

        var html = builder.GenerateForm("Products", FormMode.Update, values);

        Assert.Contains("Leave empty to keep current file", html);
    }

    [Fact]
    public void GenerateForm_Insert_BinaryColumn_NoHelpText()
    {
        var model = CreateModelWithBinaryColumn();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Products", FormMode.Insert);

        Assert.DoesNotContain("Leave empty", html);
    }

    [Fact]
    public void GenerateForm_Insert_BinaryColumn_HasLabel()
    {
        var model = CreateModelWithBinaryColumn();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Products", FormMode.Insert);

        Assert.Contains("<label for=\"image\">Image</label>", html);
    }

    [Fact]
    public void GenerateForm_Insert_BinaryColumn_CustomAccept()
    {
        var model = CreateModelWithBinaryColumn();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Products", "Image", col => col.Accept = "application/pdf,.doc");
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Products", FormMode.Insert);

        Assert.Contains("accept=\"application/pdf,.doc\"", html);
    }

    #endregion

    #region Enum Controls Integration

    [Fact]
    public void GenerateForm_Insert_SmallEnum_RendersRadioButtons()
    {
        var model = CreateSimpleModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col =>
            {
                col.EnumValues = new[] { "active", "inactive", "pending" };
            });
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("type=\"radio\"", html);
        Assert.Contains("<fieldset>", html);
        Assert.Contains("<legend>", html);
    }

    [Fact]
    public void GenerateForm_Insert_LargeEnum_RendersSelect()
    {
        var model = CreateSimpleModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col =>
            {
                col.EnumValues = new[] { "US", "CA", "GB", "DE", "FR" };
            });
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("<select", html);
        Assert.Contains("name=\"Name\"", html);
        Assert.DoesNotContain("type=\"radio\"", html);
    }

    [Fact]
    public void GenerateForm_Insert_EnumWithDisplayNames_UsesDisplayText()
    {
        var model = CreateSimpleModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col =>
            {
                col.EnumValues = new[] { "active", "inactive" };
                col.EnumDisplayNames = new Dictionary<string, string>
                {
                    ["active"] = "Active",
                    ["inactive"] = "Inactive"
                };
            });
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("Active", html);
        Assert.Contains("Inactive", html);
    }

    [Fact]
    public void GenerateForm_Update_Enum_SelectsCurrentValue()
    {
        var model = CreateSimpleModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col =>
            {
                col.EnumValues = new[] { "active", "inactive", "pending" };
            });
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);
        var values = new Dictionary<string, string?> { ["Id"] = "1", ["Name"] = "inactive", ["Email"] = "test@test.com" };

        var html = builder.GenerateForm("Users", FormMode.Update, values);

        Assert.Contains("checked", html);
    }

    #endregion

    #region Metadata Configuration Integration

    [Fact]
    public void GenerateForm_WithMetadata_OverridesInputType()
    {
        var model = CreateSimpleModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Email", col => col.InputType = "email");
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        var emailSection = ExtractFormGroup(html, "Email");
        Assert.Contains("type=\"email\"", emailSection);
    }

    [Fact]
    public void GenerateForm_WithMetadata_AddsPlaceholder()
    {
        var model = CreateSimpleModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Email", col => col.Placeholder = "user@example.com");
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("placeholder=\"user@example.com\"", html);
    }

    [Fact]
    public void GenerateForm_WithMetadata_AddsPattern()
    {
        var model = CreateSimpleModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Email", col => col.Pattern = "[a-z]+@[a-z]+\\.[a-z]+");
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("pattern=", html);
    }

    [Fact]
    public void GenerateForm_WithMetadata_AddsMinMax()
    {
        var model = CreateModelWithTypes();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Products", "Price", col =>
            {
                col.Min = 0;
                col.Max = 999999.99;
            });
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Products", FormMode.Insert);

        Assert.Contains("min=\"0\"", html);
        Assert.Contains("max=\"999999.99\"", html);
    }

    [Fact]
    public void GenerateForm_WithMetadata_StepOverridesTypeDefault()
    {
        var model = CreateModelWithTypes();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Products", "Price", col => col.Step = 1);
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Products", FormMode.Insert);

        var priceSection = ExtractFormGroup(html, "Price");
        Assert.Contains("step=\"1\"", priceSection);
        // Default step="0.01" from decimal type should be overridden
        Assert.DoesNotContain("step=\"0.01\"", priceSection);
    }

    [Fact]
    public void GenerateForm_WithoutMetadata_WorksBackwardCompatible()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("<form method=\"POST\"", html);
        Assert.Contains("name=\"Name\"", html);
        Assert.Contains("name=\"Email\"", html);
    }

    [Fact]
    public void GenerateForm_NullMetadataConfig_WorksBackwardCompatible()
    {
        var model = CreateSimpleModel();
        var builder = new BifrostFormBuilder(model, metadataConfiguration: null);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("<form method=\"POST\"", html);
        Assert.Contains("name=\"Name\"", html);
    }

    [Fact]
    public void GenerateForm_WithMetadata_TextareaGetsPlaceholder()
    {
        var model = CreateModelWithTypes();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Products", "Description", col => col.Placeholder = "Enter description...");
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Products", FormMode.Insert);

        Assert.Contains("placeholder=\"Enter description...\"", html);
    }

    #endregion

    #region Metadata Validation Attributes

    [Fact]
    public void GenerateForm_WithMetadata_AddsMinLength()
    {
        var model = CreateSimpleModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col => col.MinLength = 3);
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        var nameSection = ExtractFormGroup(html, "Name");
        Assert.Contains("minlength=\"3\"", nameSection);
    }

    [Fact]
    public void GenerateForm_WithMetadata_AddsMaxLength()
    {
        var model = CreateSimpleModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col => col.MaxLength = 50);
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        var nameSection = ExtractFormGroup(html, "Name");
        Assert.Contains("maxlength=\"50\"", nameSection);
    }

    [Fact]
    public void GenerateForm_WithMetadata_AddsTitleWithPattern()
    {
        var model = CreateSimpleModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col =>
            {
                col.Pattern = "^[a-zA-Z]+$";
                col.Title = "Letters only";
            });
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        var nameSection = ExtractFormGroup(html, "Name");
        Assert.Contains("pattern=", nameSection);
        Assert.Contains("title=\"Letters only\"", nameSection);
    }

    [Fact]
    public void GenerateForm_WithMetadata_NoTitleWithoutPattern()
    {
        var model = CreateSimpleModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col => col.Title = "Some hint");
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        var nameSection = ExtractFormGroup(html, "Name");
        Assert.DoesNotContain("title=", nameSection);
    }

    [Fact]
    public void GenerateForm_WithMetadata_RequiredTrueOnNullable()
    {
        var model = CreateModelWithNullable();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Bio", col => col.Required = true);
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        var bioSection = ExtractFormGroup(html, "Bio");
        Assert.Contains("required", bioSection);
        Assert.Contains("aria-required=\"true\"", bioSection);
    }

    [Fact]
    public void GenerateForm_WithMetadata_RequiredFalseOnNotNull()
    {
        var model = CreateSimpleModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col => col.Required = false);
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        var nameSection = ExtractFormGroup(html, "Name");
        Assert.DoesNotContain("required", nameSection);
        Assert.DoesNotContain("aria-required", nameSection);
    }

    [Fact]
    public void GenerateForm_WithMetadata_TextareaGetsMinMaxLength()
    {
        var model = CreateModelWithTypes();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Products", "Description", col =>
            {
                col.MinLength = 10;
                col.MaxLength = 500;
            });
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Products", FormMode.Insert);

        Assert.Contains("minlength=\"10\"", html);
        Assert.Contains("maxlength=\"500\"", html);
    }

    [Fact]
    public void GenerateForm_WithMetadata_CombinesMultipleAttributes()
    {
        var model = CreateSimpleModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Email", col =>
            {
                col.InputType = "email";
                col.Placeholder = "user@example.com";
                col.MinLength = 5;
                col.MaxLength = 100;
                col.Pattern = ".+@.+\\..+";
                col.Title = "Enter a valid email";
            });
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        var emailSection = ExtractFormGroup(html, "Email");
        Assert.Contains("type=\"email\"", emailSection);
        Assert.Contains("placeholder=\"user@example.com\"", emailSection);
        Assert.Contains("minlength=\"5\"", emailSection);
        Assert.Contains("maxlength=\"100\"", emailSection);
        Assert.Contains("pattern=", emailSection);
        Assert.Contains("title=\"Enter a valid email\"", emailSection);
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

    private static IDbModel CreateModelWithIdentity()
    {
        var columns = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = new ColumnDto
            {
                ColumnName = "Id", GraphQlName = "id", NormalizedName = "id",
                DataType = "int", IsPrimaryKey = true, IsIdentity = true
            },
            ["Name"] = new ColumnDto
            {
                ColumnName = "Name", GraphQlName = "name", NormalizedName = "Name",
                DataType = "nvarchar", IsNullable = false
            }
        };
        return BuildSingleTableModel("Products", columns);
    }

    private static IDbModel CreateModelWithTypes()
    {
        var columns = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = new ColumnDto
            {
                ColumnName = "Id", GraphQlName = "id", NormalizedName = "id",
                DataType = "int", IsPrimaryKey = true, IsIdentity = true
            },
            ["Name"] = new ColumnDto
            {
                ColumnName = "Name", GraphQlName = "name", NormalizedName = "Name",
                DataType = "nvarchar", IsNullable = false
            },
            ["Description"] = new ColumnDto
            {
                ColumnName = "Description", GraphQlName = "description", NormalizedName = "Description",
                DataType = "ntext", IsNullable = true
            },
            ["Price"] = new ColumnDto
            {
                ColumnName = "Price", GraphQlName = "price", NormalizedName = "Price",
                DataType = "decimal", IsNullable = false
            },
            ["InStock"] = new ColumnDto
            {
                ColumnName = "InStock", GraphQlName = "inStock", NormalizedName = "InStock",
                DataType = "bit", IsNullable = false
            },
            ["CreatedAt"] = new ColumnDto
            {
                ColumnName = "CreatedAt", GraphQlName = "createdAt", NormalizedName = "CreatedAt",
                DataType = "datetime2", IsNullable = false
            }
        };
        return BuildSingleTableModel("Products", columns);
    }

    private static IDbModel CreateModelWithNullable()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Bio", "nvarchar", isNullable: true))
            .Build();
    }

    private static IDbModel CreateModelWithAuditColumns()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("created_at", "datetime2")
                .WithColumnMetadata("created_at", "populate", "created-on")
                .WithColumn("updated_at", "datetime2")
                .WithColumnMetadata("updated_at", "populate", "updated-on"))
            .Build();
    }

    private static IDbModel CreateModelWithBinaryColumn()
    {
        var columns = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = new ColumnDto
            {
                ColumnName = "Id", GraphQlName = "id", NormalizedName = "id",
                DataType = "int", IsPrimaryKey = true, IsIdentity = true
            },
            ["Name"] = new ColumnDto
            {
                ColumnName = "Name", GraphQlName = "name", NormalizedName = "Name",
                DataType = "nvarchar", IsNullable = false
            },
            ["Image"] = new ColumnDto
            {
                ColumnName = "Image", GraphQlName = "image", NormalizedName = "Image",
                DataType = "varbinary", IsNullable = true
            }
        };
        return BuildSingleTableModel("Products", columns);
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

    private static IDbModel BuildSingleTableModel(string tableName, Dictionary<string, ColumnDto> columns)
    {
        var table = new DbTable
        {
            DbName = tableName, GraphQlName = tableName.ToLowerInvariant(), NormalizedName = tableName,
            TableSchema = "dbo",
            ColumnLookup = columns,
            GraphQlLookup = columns.Values.ToDictionary(c => c.GraphQlName, c => c),
        };

        // Use a minimal TestDbModel wrapper
        return new SingleTableModel(table);
    }

    /// <summary>
    /// Extracts the form-group div for a specific field from the generated HTML.
    /// </summary>
    private static string ExtractFormGroup(string html, string fieldName)
    {
        var nameAttr = $"name=\"{fieldName}\"";
        var idx = html.IndexOf(nameAttr, StringComparison.Ordinal);
        if (idx < 0) return "";

        // Walk backwards to find the enclosing form-group div
        var groupStart = html.LastIndexOf("<div class=\"form-group", idx, StringComparison.Ordinal);
        if (groupStart < 0) return "";

        // Find the closing </div> after the name attribute
        var groupEnd = html.IndexOf("</div>", idx, StringComparison.Ordinal);
        if (groupEnd < 0) return "";

        return html.Substring(groupStart, groupEnd - groupStart + 6);
    }

    /// <summary>
    /// Minimal IDbModel that wraps a single table for testing.
    /// </summary>
    private sealed class SingleTableModel : IDbModel
    {
        private readonly IDbTable _table;

        public SingleTableModel(IDbTable table)
        {
            _table = table;
            Tables = new[] { table };
        }

        public IReadOnlyCollection<IDbTable> Tables { get; }
        public IReadOnlyCollection<DbStoredProcedure> StoredProcedures { get; } = Array.Empty<DbStoredProcedure>();
        public IDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
        public string? GetMetadataValue(string property) => Metadata.TryGetValue(property, out var v) ? v?.ToString() : null;
        public bool GetMetadataBool(string property, bool defaultValue) => (Metadata.TryGetValue(property, out var v) && v?.ToString() == null) ? defaultValue : v?.ToString() == "true";

        public IDbTable GetTableByFullGraphQlName(string fullName) =>
            _table.MatchName(fullName) ? _table : throw new KeyNotFoundException(fullName);

        public IDbTable GetTableFromDbName(string tableName) =>
            string.Equals(_table.DbName, tableName, StringComparison.OrdinalIgnoreCase)
                ? _table
                : throw new KeyNotFoundException(tableName);
    }

    #endregion
}
