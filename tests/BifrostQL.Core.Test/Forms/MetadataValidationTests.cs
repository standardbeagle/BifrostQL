using BifrostQL.Core.Forms;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;

namespace BifrostQL.Core.Test.Forms;

public class MetadataValidationTests
{
    #region Required Override

    [Fact]
    public void Validate_MetadataRequiredTrue_OverridesNullableColumn()
    {
        var table = CreateTableWithNullableField();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Bio", col => col.Required = true);
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Name"] = "Alice", ["Email"] = "a@b.com" };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.FieldName == "Bio" && e.Message.Contains("required"));
    }

    [Fact]
    public void Validate_MetadataRequiredFalse_OverridesNotNullColumn()
    {
        var table = CreateUsersTable();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col => col.Required = false);
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Email"] = "a@b.com" };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_NoMetadata_FallsBackToSchemaRequired()
    {
        var table = CreateUsersTable();
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Email"] = "a@b.com" };

        var result = validator.Validate(form, table, FormMode.Insert);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.FieldName == "Name" && e.Message.Contains("required"));
    }

    #endregion

    #region MinLength Validation

    [Theory]
    [InlineData("ab", false)]
    [InlineData("abc", true)]
    [InlineData("abcdef", true)]
    public void Validate_MinLength_EnforcesMinimumCharacters(string value, bool shouldBeValid)
    {
        var table = CreateTableWithNullableField();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Bio", col => col.MinLength = 3);
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Name"] = "Alice", ["Email"] = "a@b.com", ["Bio"] = value };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.Equal(shouldBeValid, result.IsValid);
        if (!shouldBeValid)
            Assert.Contains(result.Errors, e => e.FieldName == "Bio" && e.Message.Contains("at least 3 characters"));
    }

    [Fact]
    public void Validate_MinLength_EmptyOptionalField_NoError()
    {
        var table = CreateTableWithNullableField();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Bio", col => col.MinLength = 3);
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Name"] = "Alice", ["Email"] = "a@b.com" };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.True(result.IsValid);
    }

    #endregion

    #region MaxLength Validation

    [Theory]
    [InlineData("abc", true)]
    [InlineData("abcde", true)]
    [InlineData("abcdef", false)]
    public void Validate_MaxLength_EnforcesMaximumCharacters(string value, bool shouldBeValid)
    {
        var table = CreateTableWithNullableField();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Bio", col => col.MaxLength = 5);
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Name"] = "Alice", ["Email"] = "a@b.com", ["Bio"] = value };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.Equal(shouldBeValid, result.IsValid);
        if (!shouldBeValid)
            Assert.Contains(result.Errors, e => e.FieldName == "Bio" && e.Message.Contains("at most 5 characters"));
    }

    #endregion

    #region Min/Max Numeric Validation

    [Theory]
    [InlineData("5", true)]
    [InlineData("0", true)]
    [InlineData("-1", false)]
    [InlineData("121", false)]
    [InlineData("120", true)]
    public void Validate_MinMaxNumeric_EnforcesRange(string value, bool shouldBeValid)
    {
        var table = CreateTableWithNumericColumn();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("People", "Age", col =>
            {
                col.Min = 0;
                col.Max = 120;
            });
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Age"] = value };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.Equal(shouldBeValid, result.IsValid);
    }

    [Fact]
    public void Validate_MinNumeric_ReturnsDescriptiveError()
    {
        var table = CreateTableWithNumericColumn();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("People", "Age", col => col.Min = 0);
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Age"] = "-1" };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.Contains(result.Errors, e => e.FieldName == "Age" && e.Message.Contains("at least 0"));
    }

    [Fact]
    public void Validate_MaxNumeric_ReturnsDescriptiveError()
    {
        var table = CreateTableWithNumericColumn();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("People", "Age", col => col.Max = 120);
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Age"] = "200" };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.Contains(result.Errors, e => e.FieldName == "Age" && e.Message.Contains("at most 120"));
    }

    [Fact]
    public void Validate_MinMaxNumeric_NonNumericColumn_SkipsRangeCheck()
    {
        var table = CreateTableWithNullableField();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Bio", col =>
            {
                col.Min = 0;
                col.Max = 100;
            });
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Name"] = "Alice", ["Email"] = "a@b.com", ["Bio"] = "hello" };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.True(result.IsValid);
    }

    #endregion

    #region Pattern Validation

    [Theory]
    [InlineData("ABC-1234", true)]
    [InlineData("XYZ-9999", true)]
    [InlineData("abc-1234", false)]
    [InlineData("ABCD-1234", false)]
    [InlineData("AB-1234", false)]
    public void Validate_Pattern_EnforcesRegex(string value, bool shouldBeValid)
    {
        var table = CreateTableWithSkuColumn();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Products", "Sku", col =>
            {
                col.Pattern = "^[A-Z]{3}-[0-9]{4}$";
                col.Title = "Format: XXX-0000";
            });
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Sku"] = value };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.Equal(shouldBeValid, result.IsValid);
    }

    [Fact]
    public void Validate_PatternWithTitle_UsesCustomErrorMessage()
    {
        var table = CreateTableWithSkuColumn();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Products", "Sku", col =>
            {
                col.Pattern = "^[A-Z]{3}-[0-9]{4}$";
                col.Title = "Format: XXX-0000";
            });
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Sku"] = "bad" };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.Contains(result.Errors, e => e.FieldName == "Sku" && e.Message == "Format: XXX-0000");
    }

    [Fact]
    public void Validate_PatternWithoutTitle_UsesDefaultErrorMessage()
    {
        var table = CreateTableWithSkuColumn();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Products", "Sku", col => col.Pattern = "^[A-Z]+$");
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Sku"] = "123" };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.Contains(result.Errors, e => e.FieldName == "Sku" && e.Message.Contains("format is invalid"));
    }

    #endregion

    #region Email Validation

    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData("test@test.co.uk", true)]
    [InlineData("not-an-email", false)]
    [InlineData("@missing-local.com", false)]
    [InlineData("missing-domain@", false)]
    public void Validate_EmailType_EnforcesEmailFormat(string value, bool shouldBeValid)
    {
        var table = CreateUsersTable();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Email", col => col.InputType = "email");
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Name"] = "Alice", ["Email"] = value };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.Equal(shouldBeValid, result.IsValid);
        if (!shouldBeValid)
            Assert.Contains(result.Errors, e => e.FieldName == "Email" && e.Message.Contains("email"));
    }

    #endregion

    #region URL Validation

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://test.com/path", true)]
    [InlineData("not-a-url", false)]
    [InlineData("ftp://other.com", false)]
    [InlineData("", true)] // empty on nullable field is fine
    public void Validate_UrlType_EnforcesUrlFormat(string value, bool shouldBeValid)
    {
        var table = CreateTableWithUrlColumn();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Links", "Website", col => col.InputType = "url");
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Website"] = string.IsNullOrEmpty(value) ? null : value };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.Equal(shouldBeValid, result.IsValid);
        if (!shouldBeValid)
            Assert.Contains(result.Errors, e => e.FieldName == "Website" && e.Message.Contains("URL"));
    }

    #endregion

    #region Combined Validations

    [Fact]
    public void Validate_MultipleMetadataRules_ReportsAllErrors()
    {
        var table = CreateTableWithNullableField();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Bio", col =>
            {
                col.Required = true;
                col.MinLength = 10;
                col.MaxLength = 500;
            });
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Name"] = "Alice", ["Email"] = "a@b.com" };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.FieldName == "Bio" && e.Message.Contains("required"));
    }

    [Fact]
    public void Validate_MultipleMetadataRules_ValueTooShort()
    {
        var table = CreateTableWithNullableField();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Bio", col =>
            {
                col.Required = true;
                col.MinLength = 10;
                col.MaxLength = 500;
            });
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Name"] = "Alice", ["Email"] = "a@b.com", ["Bio"] = "short" };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.FieldName == "Bio" && e.Message.Contains("at least 10"));
    }

    [Fact]
    public void Validate_MultipleMetadataRules_ValidValue_Passes()
    {
        var table = CreateTableWithNullableField();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Bio", col =>
            {
                col.Required = true;
                col.MinLength = 10;
                col.MaxLength = 500;
            });
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Name"] = "Alice", ["Email"] = "a@b.com", ["Bio"] = "This is a perfectly valid bio text." };

        var result = validator.Validate(form, table, FormMode.Insert, metadataConfig);

        Assert.True(result.IsValid);
    }

    #endregion

    #region Backward Compatibility

    [Fact]
    public void Validate_NullMetadataConfig_BehavesLikeOriginal()
    {
        var table = CreateUsersTable();
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Email"] = "test@example.com" };

        var resultWithNull = validator.Validate(form, table, FormMode.Insert, metadataConfig: null);
        var resultWithout = validator.Validate(form, table, FormMode.Insert);

        Assert.Equal(resultWithNull.IsValid, resultWithout.IsValid);
        Assert.Equal(resultWithNull.Errors.Count, resultWithout.Errors.Count);
    }

    [Fact]
    public void Validate_EmptyMetadataConfig_BehavesLikeOriginal()
    {
        var table = CreateUsersTable();
        var emptyConfig = new FormsMetadataConfiguration();
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Email"] = "test@example.com" };

        var result = validator.Validate(form, table, FormMode.Insert, emptyConfig);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.FieldName == "Name" && e.Message.Contains("required"));
    }

    #endregion

    #region Delete Mode

    [Fact]
    public void Validate_DeleteMode_SkipsMetadataValidation()
    {
        var table = CreateUsersTable();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col =>
            {
                col.Required = true;
                col.MinLength = 5;
            });
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?>();

        var result = validator.Validate(form, table, FormMode.Delete, metadataConfig);

        Assert.True(result.IsValid);
    }

    #endregion

    #region Client-Server Parity

    [Fact]
    public void FormBuilder_And_Validator_UseIdenticalRequiredLogic()
    {
        // Nullable column with metadata Required=true should be required in both
        var column = new ColumnDto
        {
            ColumnName = "Bio", GraphQlName = "bio", NormalizedName = "Bio",
            DataType = "nvarchar", IsNullable = true
        };
        var metadata = new ColumnMetadata { Required = true };

        var isRequired = BifrostFormBuilder.IsFieldRequired(column, metadata);

        Assert.True(isRequired);
    }

    [Fact]
    public void FormBuilder_And_Validator_RequiredFalse_OverridesNotNull()
    {
        // NOT NULL column with metadata Required=false should not be required
        var column = new ColumnDto
        {
            ColumnName = "Name", GraphQlName = "name", NormalizedName = "Name",
            DataType = "nvarchar", IsNullable = false
        };
        var metadata = new ColumnMetadata { Required = false };

        var isRequired = BifrostFormBuilder.IsFieldRequired(column, metadata);

        Assert.False(isRequired);
    }

    [Fact]
    public void FormBuilder_And_Validator_NullMetadata_FallsBackToSchema()
    {
        var notNullColumn = new ColumnDto
        {
            ColumnName = "Name", GraphQlName = "name", NormalizedName = "Name",
            DataType = "nvarchar", IsNullable = false
        };
        var nullableColumn = new ColumnDto
        {
            ColumnName = "Bio", GraphQlName = "bio", NormalizedName = "Bio",
            DataType = "nvarchar", IsNullable = true
        };

        Assert.True(BifrostFormBuilder.IsFieldRequired(notNullColumn, null));
        Assert.False(BifrostFormBuilder.IsFieldRequired(nullableColumn, null));
    }

    #endregion

    #region HTML5 Attribute Parity Tests

    [Fact]
    public void FormBuilder_EmitsMinLengthAttribute_FromMetadata()
    {
        var model = CreateModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col => col.MinLength = 3);
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("minlength=\"3\"", html);
    }

    [Fact]
    public void FormBuilder_EmitsMaxLengthAttribute_FromMetadata()
    {
        var model = CreateModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col => col.MaxLength = 20);
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("maxlength=\"20\"", html);
    }

    [Fact]
    public void FormBuilder_EmitsTitleAttribute_WithPattern()
    {
        var model = CreateModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col =>
            {
                col.Pattern = "^[a-zA-Z]+$";
                col.Title = "Letters only";
            });
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.Contains("pattern=", html);
        Assert.Contains("title=\"Letters only\"", html);
    }

    [Fact]
    public void FormBuilder_TitleNotEmitted_WithoutPattern()
    {
        var model = CreateModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col => col.Title = "Some title");
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        Assert.DoesNotContain("title=", html);
    }

    [Fact]
    public void FormBuilder_RequiredTrue_AddsAttributeToNullableField()
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
    public void FormBuilder_RequiredFalse_RemovesAttributeFromNotNullField()
    {
        var model = CreateModel();
        var metadataConfig = new FormsMetadataConfiguration()
            .ConfigureColumn("Users", "Name", col => col.Required = false);
        var builder = new BifrostFormBuilder(model, metadataConfiguration: metadataConfig);

        var html = builder.GenerateForm("Users", FormMode.Insert);

        var nameSection = ExtractFormGroup(html, "Name");
        Assert.DoesNotContain("required", nameSection);
    }

    #endregion

    #region Helper Methods

    private static IDbTable CreateUsersTable()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar"))
            .Build()
            .GetTableFromDbName("Users");
    }

    private static IDbTable CreateTableWithNullableField()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar")
                .WithColumn("Bio", "nvarchar", isNullable: true))
            .Build()
            .GetTableFromDbName("Users");
    }

    private static IDbTable CreateTableWithNumericColumn()
    {
        return DbModelTestFixture.Create()
            .WithTable("People", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Age", "int", isNullable: true))
            .Build()
            .GetTableFromDbName("People");
    }

    private static IDbTable CreateTableWithSkuColumn()
    {
        return DbModelTestFixture.Create()
            .WithTable("Products", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Sku", "nvarchar", isNullable: true))
            .Build()
            .GetTableFromDbName("Products");
    }

    private static IDbTable CreateTableWithUrlColumn()
    {
        return DbModelTestFixture.Create()
            .WithTable("Links", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Website", "nvarchar", isNullable: true))
            .Build()
            .GetTableFromDbName("Links");
    }

    private static IDbModel CreateModel()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar"))
            .Build();
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

    private static string ExtractFormGroup(string html, string fieldName)
    {
        var nameAttr = $"name=\"{fieldName}\"";
        var idx = html.IndexOf(nameAttr, StringComparison.Ordinal);
        if (idx < 0) return "";

        var groupStart = html.LastIndexOf("<div class=\"form-group", idx, StringComparison.Ordinal);
        if (groupStart < 0) return "";

        var groupEnd = html.IndexOf("</div>", idx, StringComparison.Ordinal);
        if (groupEnd < 0) return "";

        return html.Substring(groupStart, groupEnd - groupStart + 6);
    }

    #endregion
}
