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
    // Dialect-neutral facts hold under every engine's lexicon: a payload is refused
    // when ANY engine would run it as code, and accepted only when every engine agrees.
    private readonly AllLexicons _validator = new();

    private sealed class AllLexicons
    {
        private readonly RawSqlValidator[] _validators = Enum.GetValues<RawSqlLexicon>()
            .Select(l => new RawSqlValidator(l)).ToArray();

        public RawSqlValidationResult Validate(string? sql)
        {
            foreach (var validator in _validators)
            {
                var result = validator.Validate(sql);
                if (!result.IsValid)
                    return result;
            }
            return RawSqlValidationResult.Ok();
        }
    }

    // Access internal SchemaGenerator via reflection, matching existing test patterns
    private static readonly MethodInfo SchemaTextFromModelMethod = typeof(DbSchema).Assembly
        .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
        .GetMethod("SchemaTextFromModel", BindingFlags.Static | BindingFlags.Public)!;

    private static readonly MethodInfo IsRawSqlEnabledMethod = typeof(DbSchema).Assembly
        .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
        .GetMethod("IsRawSqlEnabled", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static string GetSchemaText(IDbModel model)
        => (string)SchemaTextFromModelMethod.Invoke(null, new object[] { model })!;

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

    #region Executable Comments (MySQL /*! ... */)

    [Fact]
    public void Validate_MySqlExecutableComment_ReturnsFail()
    {
        var result = _validator.Validate("SELECT 1 /*! ; DROP TABLE Users */");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Executable comment");
    }

    [Fact]
    public void Validate_MySqlVersionConditionalComment_ReturnsFail()
    {
        var result = _validator.Validate("/*!50000 DROP TABLE Users */ SELECT 1");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Executable comment");
    }

    [Fact]
    public void Validate_OrdinaryBlockComment_ReturnsValid()
    {
        var result = _validator.Validate("SELECT * FROM Users /* fetch everything */");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_KeywordInsideOrdinaryComment_ReturnsValid()
    {
        var result = _validator.Validate("SELECT * FROM Users -- update this later\nWHERE Id = @id");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_UnterminatedBlockComment_ReturnsFail()
    {
        var result = _validator.Validate("SELECT 1 /* trailing");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unterminated block comment");
    }

    #endregion

    #region Statement Boundary and Escape Confusion

    [Fact]
    public void Validate_SelectThenDrop_ReturnsFail()
    {
        var result = _validator.Validate("SELECT 1; DROP TABLE x");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Multiple SQL statements");
    }

    [Fact]
    public void Validate_TrailingSemicolon_ReturnsValid()
    {
        var result = _validator.Validate("SELECT * FROM Users;");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_SemicolonInsideStringLiteral_ReturnsValid()
    {
        var result = _validator.Validate("SELECT * FROM Users WHERE Name = 'a;b'");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ForbiddenKeywordInsideStringLiteral_ReturnsValid()
    {
        var result = _validator.Validate("SELECT * FROM Users WHERE Name = 'DROP TABLE'");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_DoubledQuoteEscapeInString_ReturnsValid()
    {
        var result = _validator.Validate("SELECT 'it''s fine' FROM Users");
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(RawSqlLexicon.SqlServer)]
    [InlineData(RawSqlLexicon.Sqlite)]
    public void Validate_BracketQuotedIdentifierWithKeyword_ReturnsValid(RawSqlLexicon lexicon)
    {
        var result = new RawSqlValidator(lexicon).Validate("SELECT [Into] FROM Users");
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(RawSqlLexicon.Postgres)]
    [InlineData(RawSqlLexicon.MySql)]
    public void Validate_BracketIsCodeWhereTheEngineHasNoBracketIdentifiers_ReturnsFail(RawSqlLexicon lexicon)
    {
        var result = new RawSqlValidator(lexicon).Validate("SELECT [Into] FROM Users");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("INTO");
    }

    [Fact]
    public void Validate_BackslashEscapeSmugglesStatement_ReturnsFail()
    {
        // Under ANSI/SQL Server semantics the string ends at the second quote,
        // exposing "; DROP TABLE Users" as code. Under MySQL backslash semantics
        // it is one string. Must be rejected because it is code in at least one dialect.
        var result = _validator.Validate("SELECT '\\'; DROP TABLE Users; SELECT '1'");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Multiple SQL statements");
    }

    [Fact]
    public void Validate_BackslashBeforeClosingQuote_ReturnsFail()
    {
        // Ambiguous across dialects: MySQL treats \' as an escaped quote and the
        // string never terminates; other engines terminate it. Fail-safe reject.
        var result = _validator.Validate("SELECT '\\'; DROP TABLE Users --'");
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_UnterminatedStringLiteral_ReturnsFail()
    {
        var result = _validator.Validate("SELECT 'abc FROM Users");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unterminated string literal");
    }

    [Fact]
    public void Validate_DashDashWithoutWhitespaceHidesSemicolon_ReturnsFail()
    {
        // MySQL only treats -- as a comment when followed by whitespace, so
        // "--x; DROP ..." executes there. The scanner treats it as code.
        var result = _validator.Validate("SELECT 1 --x; DROP TABLE Users");
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_HashCommentHidesSemicolon_ReturnsFail()
    {
        // '#' starts a comment on MySQL only; the scanner treats it as code so
        // the semicolon and DROP are seen.
        var result = _validator.Validate("SELECT 1 # ; DROP TABLE Users");
        result.IsValid.Should().BeFalse();
    }

    #endregion

    #region Dialect Lexing Gaps

    // Each payload reads as one harmless statement to a lexer that does not know the
    // engine's own quoting or comment rules, while the engine runs the hidden write.
    [Theory]
    [InlineData("SELECT $a$ ' $a$; DELETE FROM t; --'")]                      // Postgres dollar quote
    [InlineData("SELECT $$ ' $$; DELETE FROM t; --'")]                        // Postgres empty-tag dollar quote
    [InlineData("SELECT \"\\\"\" ; DELETE FROM t; -- \"")]                    // MySQL backslash in "..."
    [InlineData("SELECT ('{}'::jsonb)[']'] ; DELETE FROM t; --']")]            // Postgres: [ is code
    [InlineData("SELECT 1 /* /* */ ' */ ; DELETE FROM t; --'")]               // nested block comment (Postgres, SQL Server)
    [InlineData("SELECT 1 --' \n ; DELETE FROM t --'")]                       // -- without space still a comment (SQL Server, Postgres, SQLite)
    [InlineData("SELECT 1 # ' \n ; DELETE FROM t; -- '")]                     // MySQL # comment hides an opening quote
    [InlineData("SELECT 1 -- x\r; DELETE FROM t")]                             // Postgres ends a line comment at CR
    [InlineData("SELECT 1 /*M! ; DELETE FROM t */")]                          // MariaDB executable comment
    public void Validate_PayloadHiddenByDialectQuoting_ReturnsFail(string sql)
    {
        var result = _validator.Validate(sql);
        result.IsValid.Should().BeFalse();
    }

    // T-SQL needs no semicolon between statements, and these verbs write or change the session.
    [Theory]
    [InlineData("SELECT 1 DECLARE @p varbinary(16) SELECT @p=TEXTPTR(body) FROM dbo.docs WHERE id=1 WRITETEXT dbo.docs.body @p 'x'", "DECLARE")]
    [InlineData("SELECT 1 UPDATETEXT dbo.docs.body @p 0 NULL 'x'", "UPDATETEXT")]
    [InlineData("SELECT 1 DISABLE TRIGGER ALL ON dbo.audit", "DISABLE")]
    [InlineData("SELECT 1 KILL 57", "KILL")]
    [InlineData("SELECT 1 USE otherdb", "USE")]
    [InlineData("SELECT 1 WAITFOR DELAY '00:01'", "WAITFOR")]
    [InlineData("SELECT 1 RECONFIGURE", "RECONFIGURE")]
    [InlineData("SELECT 1 COMMIT", "COMMIT")]
    [InlineData("SELECT 1 SET IMPLICIT_TRANSACTIONS OFF", "SET")]
    [InlineData("SELECT pg_terminate_backend(57)", "PG_TERMINATE_BACKEND")]
    [InlineData("SELECT set_config('role', 'admin', false)", "SET_CONFIG")]
    [InlineData("SELECT dblink_exec('dbname=x', 'DROP TABLE t')", "DBLINK_EXEC")]
    [InlineData("SELECT LOAD_FILE('/etc/passwd')", "LOAD_FILE")]
    [InlineData("SELECT load_extension('evil')", "LOAD_EXTENSION")]
    public void Validate_SessionOrWriteVerbWithoutSemicolon_ReturnsFail(string sql, string verb)
    {
        var result = _validator.Validate(sql);
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain(verb);
    }

    // Each engine's own quoting, accepted under that engine's lexicon.
    [Theory]
    [InlineData(RawSqlLexicon.Postgres, "SELECT $$a;b DELETE$$ AS t")]
    [InlineData(RawSqlLexicon.Postgres, "SELECT $body$ it's; $body$ AS t")]
    [InlineData(RawSqlLexicon.Postgres, "SELECT 1 /* outer /* inner */ still comment; DELETE */")]
    [InlineData(RawSqlLexicon.Postgres, "SELECT $1::int")]
    [InlineData(RawSqlLexicon.SqlServer, "SELECT 1 --; DELETE FROM t")]
    [InlineData(RawSqlLexicon.SqlServer, "SELECT [a]]; DELETE] FROM t")]
    [InlineData(RawSqlLexicon.MySql, "SELECT `a``; DELETE` FROM t")]
    [InlineData(RawSqlLexicon.MySql, "SELECT 1 # ; DELETE FROM t")]
    [InlineData(RawSqlLexicon.Sqlite, "SELECT `delete` FROM t")]
    public void Validate_EngineQuoting_ReturnsValid(RawSqlLexicon lexicon, string sql)
    {
        var result = new RawSqlValidator(lexicon).Validate(sql);
        result.IsValid.Should().BeTrue(result.ErrorMessage);
    }

    // The same text under an engine that reads it differently runs the hidden statement.
    [Theory]
    [InlineData(RawSqlLexicon.SqlServer, "SELECT $$a;b DELETE$$ AS t")]
    [InlineData(RawSqlLexicon.Sqlite, "SELECT 1 /* outer /* inner */ still comment; DELETE */")]
    [InlineData(RawSqlLexicon.MySql, "SELECT 1 --; DELETE FROM t")]
    [InlineData(RawSqlLexicon.Sqlite, "SELECT [a]]; DELETE] FROM t")]
    [InlineData(RawSqlLexicon.Postgres, "SELECT 1 # ; DELETE FROM t")]
    [InlineData(RawSqlLexicon.SqlServer, "SELECT `delete` FROM t")]
    public void Validate_OtherEngineQuoting_ReturnsFail(RawSqlLexicon lexicon, string sql)
    {
        var result = new RawSqlValidator(lexicon).Validate(sql);
        result.IsValid.Should().BeFalse();
    }

    // T-SQL needs no separator between statements, so a second statement after a SELECT
    // carries no `;` for the lexer to find. SQL Server's dialect also parses the text and
    // requires exactly one SELECT. Each payload passes the T-SQL lexicon on its own.
    [Theory]
    [InlineData("SELECT 1 SELECT 2")]
    [InlineData("SELECT 1 CHECKPOINT")]
    [InlineData("SELECT 1 x FROM t SELECT 2")]
    public void SqlServerDialect_SecondStatementWithoutSeparator_ReturnsFail(string sql)
    {
        new RawSqlValidator(RawSqlLexicon.SqlServer).Validate(sql).IsValid
            .Should().BeTrue("the lexer alone cannot see this statement boundary");

        var result = BifrostQL.SqlServer.SqlServerDialect.Instance.ValidateReadOnlySql(sql);

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT id, name FROM [dbo].[Users] WHERE id = @id ORDER BY name")]
    [InlineData("WITH c AS (SELECT 1 AS x) SELECT x FROM c")]
    [InlineData("SELECT 1 UNION ALL SELECT 2")]
    public void SqlServerDialect_SingleSelect_ReturnsValid(string sql)
    {
        var result = BifrostQL.SqlServer.SqlServerDialect.Instance.ValidateReadOnlySql(sql);

        result.IsValid.Should().BeTrue(result.ErrorMessage);
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
