using BifrostQL.Core.Forms;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;

namespace BifrostQL.Core.Test.Forms;

public class BifrostFormValidatorTests
{
    #region Required Validation

    [Fact]
    public void Validate_RequiredFieldMissing_ReturnsError()
    {
        var table = CreateUsersTable();
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Email"] = "test@example.com" };

        var result = validator.Validate(form, table, FormMode.Insert);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.FieldName == "Name" && e.Message.Contains("required"));
    }

    [Fact]
    public void Validate_RequiredFieldPresent_NoError()
    {
        var table = CreateUsersTable();
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?>
        {
            ["Name"] = "Alice",
            ["Email"] = "alice@example.com"
        };

        var result = validator.Validate(form, table, FormMode.Insert);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_NullableFieldMissing_NoError()
    {
        var table = CreateUsersTableWithNullableField();
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?>
        {
            ["Name"] = "Alice",
            ["Email"] = "alice@example.com"
        };

        var result = validator.Validate(form, table, FormMode.Insert);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RequiredFieldEmpty_ReturnsError()
    {
        var table = CreateUsersTable();
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?>
        {
            ["Name"] = "",
            ["Email"] = "test@example.com"
        };

        var result = validator.Validate(form, table, FormMode.Insert);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.FieldName == "Name");
    }

    #endregion

    #region Identity Column Handling

    [Fact]
    public void Validate_IdentityColumn_SkippedDuringInsert()
    {
        var table = CreateTableWithIdentity();
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?>
        {
            ["Name"] = "Alice"
        };

        var result = validator.Validate(form, table, FormMode.Insert);

        Assert.True(result.IsValid);
    }

    #endregion

    #region Type Validation - Numeric

    [Theory]
    [InlineData("123", true)]
    [InlineData("-42", true)]
    [InlineData("3.14", true)]
    [InlineData("abc", false)]
    [InlineData("12.34.56", false)]
    public void Validate_IntColumn_ValidatesNumeric(string value, bool shouldBeValid)
    {
        var table = CreateTableWithNumericColumn();
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["Age"] = value };

        var result = validator.Validate(form, table, FormMode.Insert);

        Assert.Equal(shouldBeValid, result.IsValid);
    }

    #endregion

    #region Type Validation - Boolean

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("1", true)]
    [InlineData("0", true)]
    [InlineData("on", true)]
    [InlineData("off", true)]
    [InlineData("yes", true)]
    [InlineData("no", true)]
    [InlineData("maybe", false)]
    [InlineData("2", false)]
    public void Validate_BitColumn_ValidatesBoolean(string value, bool shouldBeValid)
    {
        var table = CreateTableWithBoolColumn();
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["IsActive"] = value };

        var result = validator.Validate(form, table, FormMode.Insert);

        Assert.Equal(shouldBeValid, result.IsValid);
    }

    #endregion

    #region Type Validation - DateTime

    [Theory]
    [InlineData("2024-01-15", true)]
    [InlineData("2024-01-15T10:30:00", true)]
    [InlineData("not-a-date", false)]
    [InlineData("32-13-2024", false)]
    public void Validate_DateTimeColumn_ValidatesDateTime(string value, bool shouldBeValid)
    {
        var table = CreateTableWithDateColumn();
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?> { ["CreatedAt"] = value };

        var result = validator.Validate(form, table, FormMode.Insert);

        Assert.Equal(shouldBeValid, result.IsValid);
    }

    #endregion

    #region Delete Mode

    [Fact]
    public void Validate_DeleteMode_OnlyValidatesPrimaryKey()
    {
        var table = CreateUsersTable();
        var validator = new BifrostFormValidator();
        // Delete mode: only the PK matters, non-PK fields are irrelevant
        var form = new Dictionary<string, string?>();

        var result = validator.Validate(form, table, FormMode.Delete);

        Assert.True(result.IsValid);
    }

    #endregion

    #region Audit Column Handling

    [Fact]
    public void Validate_AuditColumns_NotRequired()
    {
        var table = CreateTableWithAuditColumns();
        var validator = new BifrostFormValidator();
        // Audit columns should not be required even though they're NOT NULL
        var form = new Dictionary<string, string?>
        {
            ["Name"] = "Alice"
        };

        var result = validator.Validate(form, table, FormMode.Insert);

        Assert.True(result.IsValid);
    }

    #endregion

    #region Multiple Errors

    [Fact]
    public void Validate_MultipleErrors_ReturnsAll()
    {
        var table = CreateUsersTable();
        var validator = new BifrostFormValidator();
        var form = new Dictionary<string, string?>();

        var result = validator.Validate(form, table, FormMode.Insert);

        Assert.True(result.Errors.Count >= 2);
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

    private static IDbTable CreateUsersTableWithNullableField()
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

    private static IDbTable CreateTableWithIdentity()
    {
        // Use a raw DbTable to set IsIdentity since the test fixture builder doesn't support it
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
        return new DbTable
        {
            DbName = "Users", GraphQlName = "users", NormalizedName = "User",
            TableSchema = "dbo",
            ColumnLookup = columns,
            GraphQlLookup = columns.Values.ToDictionary(c => c.GraphQlName, c => c),
        };
    }

    private static IDbTable CreateTableWithNumericColumn()
    {
        // Nullable int column so we only test type validation, not required validation
        return DbModelTestFixture.Create()
            .WithTable("People", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Age", "int", isNullable: true))
            .Build()
            .GetTableFromDbName("People");
    }

    private static IDbTable CreateTableWithBoolColumn()
    {
        return DbModelTestFixture.Create()
            .WithTable("Settings", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("IsActive", "bit", isNullable: true))
            .Build()
            .GetTableFromDbName("Settings");
    }

    private static IDbTable CreateTableWithDateColumn()
    {
        return DbModelTestFixture.Create()
            .WithTable("Events", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("CreatedAt", "datetime2", isNullable: true))
            .Build()
            .GetTableFromDbName("Events");
    }

    private static IDbTable CreateTableWithAuditColumns()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar")
                .WithColumn("created_at", "datetime2")
                .WithColumnMetadata("created_at", "populate", "created-on")
                .WithColumn("updated_at", "datetime2")
                .WithColumnMetadata("updated_at", "populate", "updated-on"))
            .Build()
            .GetTableFromDbName("Users");
    }

    #endregion
}
