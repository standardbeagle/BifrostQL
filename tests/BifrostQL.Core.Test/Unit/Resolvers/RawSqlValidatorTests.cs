using System.Reflection;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Resolvers;

public sealed class RawSqlValidatorTests
{
    private readonly RawSqlValidator _validator = new();

    // Access internal SchemaGenerator via reflection, matching existing test patterns (StoredProcedureTests)
    private static readonly MethodInfo SchemaTextFromModelMethod = typeof(DbSchema).Assembly
        .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
        .GetMethod("SchemaTextFromModel", BindingFlags.Static | BindingFlags.Public)!;

    private static readonly MethodInfo IsRawSqlEnabledMethod = typeof(DbSchema).Assembly
        .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
        .GetMethod("IsRawSqlEnabled", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static string GetSchemaText(IDbModel model)
        => (string)SchemaTextFromModelMethod.Invoke(null, new object[] { model, true })!;

    private static bool InvokeIsRawSqlEnabled(IDbModel model)
        => (bool)IsRawSqlEnabledMethod.Invoke(null, new object[] { model })!;

    #region Valid SELECT Queries

    [Fact]
    public void Validate_SimpleSelect_ReturnsValid()
    {
        var result = _validator.Validate("SELECT * FROM Users");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_SelectWithWhere_ReturnsValid()
    {
        var result = _validator.Validate("SELECT Id, Name FROM Users WHERE Id = @id");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_SelectWithJoin_ReturnsValid()
    {
        var result = _validator.Validate(
            "SELECT u.Id, o.Total FROM Users u INNER JOIN Orders o ON u.Id = o.UserId WHERE u.Id = @userId");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_SelectWithSubquery_ReturnsValid()
    {
        var result = _validator.Validate(
            "SELECT * FROM Users WHERE Id IN (SELECT UserId FROM Orders WHERE Total > @minTotal)");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_SelectWithGroupBy_ReturnsValid()
    {
        var result = _validator.Validate(
            "SELECT Status, COUNT(*) AS Cnt FROM Orders GROUP BY Status HAVING COUNT(*) > @minCount");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_SelectWithCTE_ReturnsValid()
    {
        var result = _validator.Validate(
            "WITH cte AS (SELECT Id, Name FROM Users WHERE Active = 1) SELECT * FROM cte WHERE Name LIKE @pattern");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_SelectWithTopAndOrderBy_ReturnsValid()
    {
        var result = _validator.Validate("SELECT TOP 10 * FROM Users ORDER BY CreatedAt DESC");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_SelectWithParameters_ReturnsValid()
    {
        var result = _validator.Validate("SELECT * FROM Users WHERE Name = @name AND Age > @minAge");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_LowercaseSelect_ReturnsValid()
    {
        var result = _validator.Validate("select * from Users");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_MixedCaseSelect_ReturnsValid()
    {
        var result = _validator.Validate("SeLeCt Id, Name FROM Users");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_SelectWithLeadingWhitespace_ReturnsValid()
    {
        var result = _validator.Validate("   SELECT * FROM Users");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_SelectWithNewlines_ReturnsValid()
    {
        var result = _validator.Validate("SELECT\n  Id,\n  Name\nFROM\n  Users\nWHERE\n  Id = @id");
        result.IsValid.Should().BeTrue();
    }

    #endregion

    #region Null and Empty Input

    [Fact]
    public void Validate_Null_ReturnsFail()
    {
        var result = _validator.Validate(null);
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("empty");
    }

    [Fact]
    public void Validate_EmptyString_ReturnsFail()
    {
        var result = _validator.Validate("");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("empty");
    }

    [Fact]
    public void Validate_WhitespaceOnly_ReturnsFail()
    {
        var result = _validator.Validate("   \t\n  ");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("empty");
    }

    #endregion

    #region Forbidden DML Statements

    [Fact]
    public void Validate_InsertStatement_ReturnsFail()
    {
        var result = _validator.Validate("INSERT INTO Users (Name) VALUES (@name)");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("INSERT");
    }

    [Fact]
    public void Validate_UpdateStatement_ReturnsFail()
    {
        var result = _validator.Validate("UPDATE Users SET Name = @name WHERE Id = @id");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("UPDATE");
    }

    [Fact]
    public void Validate_DeleteStatement_ReturnsFail()
    {
        var result = _validator.Validate("DELETE FROM Users WHERE Id = @id");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("DELETE");
    }

    [Fact]
    public void Validate_MergeStatement_ReturnsFail()
    {
        var result = _validator.Validate(
            "MERGE INTO Users AS target USING @source AS source ON target.Id = source.Id");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("MERGE");
    }

    #endregion

    #region Forbidden DDL Statements

    [Fact]
    public void Validate_CreateTable_ReturnsFail()
    {
        var result = _validator.Validate("CREATE TABLE Evil (Id INT)");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("CREATE");
    }

    [Fact]
    public void Validate_AlterTable_ReturnsFail()
    {
        var result = _validator.Validate("ALTER TABLE Users ADD Column1 INT");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ALTER");
    }

    [Fact]
    public void Validate_DropTable_ReturnsFail()
    {
        var result = _validator.Validate("DROP TABLE Users");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("DROP");
    }

    [Fact]
    public void Validate_TruncateTable_ReturnsFail()
    {
        var result = _validator.Validate("TRUNCATE TABLE Users");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("TRUNCATE");
    }

    #endregion

    #region Forbidden Execution Statements

    [Fact]
    public void Validate_Exec_ReturnsFail()
    {
        var result = _validator.Validate("EXEC sp_executesql @sql");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("EXEC");
    }

    [Fact]
    public void Validate_Execute_ReturnsFail()
    {
        var result = _validator.Validate("EXECUTE sp_who2");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("EXECUTE");
    }

    #endregion

    #region Forbidden Security Statements

    [Fact]
    public void Validate_Grant_ReturnsFail()
    {
        var result = _validator.Validate("GRANT SELECT ON Users TO PublicRole");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("GRANT");
    }

    [Fact]
    public void Validate_Revoke_ReturnsFail()
    {
        var result = _validator.Validate("REVOKE SELECT ON Users FROM PublicRole");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("REVOKE");
    }

    [Fact]
    public void Validate_Deny_ReturnsFail()
    {
        var result = _validator.Validate("DENY SELECT ON Users TO PublicRole");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("DENY");
    }

    #endregion

    #region Forbidden Backup/Admin Statements

    [Fact]
    public void Validate_Backup_ReturnsFail()
    {
        var result = _validator.Validate("BACKUP DATABASE MyDb TO DISK = @path");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("BACKUP");
    }

    [Fact]
    public void Validate_Shutdown_ReturnsFail()
    {
        var result = _validator.Validate("SHUTDOWN WITH NOWAIT");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("SHUTDOWN");
    }

    #endregion

    #region SQL Injection Attempts

    [Fact]
    public void Validate_SelectThenDropViaMultipleStatements_ReturnsFail()
    {
        var result = _validator.Validate("SELECT * FROM Users; DROP TABLE Users");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Multiple SQL statements");
    }

    [Fact]
    public void Validate_SelectIntoNewTable_ReturnsFail()
    {
        var result = _validator.Validate("SELECT * INTO NewTable FROM Users");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("INTO");
    }

    [Fact]
    public void Validate_InsertHiddenInBlockComment_ReturnsFail()
    {
        var result = _validator.Validate("/* SELECT */ INSERT INTO Users (Name) VALUES ('hacked')");
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_DropHiddenAfterLineComment_ReturnsFail()
    {
        var result = _validator.Validate("SELECT * FROM Users --\n; DROP TABLE Users");
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_ExecInLowerCase_ReturnsFail()
    {
        var result = _validator.Validate("exec xp_cmdshell 'dir'");
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_OpenRowset_ReturnsFail()
    {
        var result = _validator.Validate(
            "SELECT * FROM OPENROWSET('SQLNCLI', 'server=evilserver;', 'SELECT 1')");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("OPENROWSET");
    }

    [Fact]
    public void Validate_BulkInsert_ReturnsFail()
    {
        var result = _validator.Validate("BULK INSERT Users FROM 'data.csv'");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("BULK");
    }

    #endregion

    #region Schema Integration Tests

    [Fact]
    public void SchemaText_DoesNotContainRawQuery_WhenDisabled()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var schemaText = GetSchemaText(model);
        schemaText.Should().NotContain("_rawQuery");
    }

    [Fact]
    public void SchemaText_ContainsRawQuery_WhenEnabled()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("raw-sql", "enabled")
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var schemaText = GetSchemaText(model);
        schemaText.Should().Contain("_rawQuery");
        schemaText.Should().Contain("sql: String!");
        schemaText.Should().Contain("params: JSON");
        schemaText.Should().Contain("timeout: Int");
    }

    [Fact]
    public void SchemaText_DoesNotContainRawQuery_WhenMetadataIsNotEnabled()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("raw-sql", "disabled")
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var schemaText = GetSchemaText(model);
        schemaText.Should().NotContain("_rawQuery");
    }

    [Fact]
    public void DbSchema_BuildsSuccessfully_WhenRawSqlEnabled()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("raw-sql", "enabled")
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var schema = DbSchema.FromModel(model);
        schema.Should().NotBeNull();
    }

    [Fact]
    public void DbSchema_BuildsSuccessfully_WhenRawSqlDisabled()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var schema = DbSchema.FromModel(model);
        schema.Should().NotBeNull();
    }

    #endregion

    #region Resolver Configuration Tests

    [Fact]
    public void RawSqlQueryResolver_DefaultRole_IsBifrostRawSql()
    {
        RawSqlQueryResolver.DefaultRequiredRole.Should().Be("bifrost-raw-sql");
    }

    [Fact]
    public void RawSqlQueryResolver_DefaultTimeout_Is30Seconds()
    {
        RawSqlQueryResolver.DefaultTimeoutSeconds.Should().Be(30);
    }

    [Fact]
    public void RawSqlQueryResolver_DefaultMaxRows_Is1000()
    {
        RawSqlQueryResolver.DefaultMaxRows.Should().Be(1000);
    }

    [Fact]
    public void RawSqlQueryResolver_MetadataKey_IsRawSql()
    {
        RawSqlQueryResolver.MetadataKey.Should().Be("raw-sql");
    }

    #endregion

    #region IsRawSqlEnabled Tests

    [Fact]
    public void IsRawSqlEnabled_ReturnsFalse_WhenNoMetadata()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();

        InvokeIsRawSqlEnabled(model).Should().BeFalse();
    }

    [Fact]
    public void IsRawSqlEnabled_ReturnsTrue_WhenEnabled()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("raw-sql", "enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();

        InvokeIsRawSqlEnabled(model).Should().BeTrue();
    }

    [Fact]
    public void IsRawSqlEnabled_ReturnsFalse_WhenDisabled()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("raw-sql", "disabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();

        InvokeIsRawSqlEnabled(model).Should().BeFalse();
    }

    [Fact]
    public void IsRawSqlEnabled_IsCaseInsensitive()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("raw-sql", "Enabled")
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();

        InvokeIsRawSqlEnabled(model).Should().BeTrue();
    }

    #endregion

    #region Validation Result Struct Tests

    [Fact]
    public void RawSqlValidationResult_Ok_IsValid()
    {
        var result = RawSqlValidationResult.Ok();
        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void RawSqlValidationResult_Fail_IsNotValid()
    {
        var result = RawSqlValidationResult.Fail("some error");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("some error");
    }

    #endregion
}
