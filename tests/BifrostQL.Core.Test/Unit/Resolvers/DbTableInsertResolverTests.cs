using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Tests for DbTableMutateResolver insert operations with RETURNING/OUTPUT clause support.
/// </summary>
public sealed class DbTableInsertResolverTests
{
    #region RETURNING Clause Support Tests

    [Fact]
    public void PostgresDialect_HasReturningIdentityClause()
    {
        // Arrange & Act
        var clause = PostgresDialect.Instance.ReturningIdentityClause;

        // Assert
        clause.Should().NotBeNull();
        clause.Should().Be(" RETURNING id AS ID");
    }

    [Fact]
    public void SqliteDialect_HasReturningIdentityClause()
    {
        // Arrange & Act
        var clause = SqliteDialect.Instance.ReturningIdentityClause;

        // Assert
        clause.Should().NotBeNull();
        clause.Should().Be(" RETURNING rowid AS ID");
    }

    [Fact]
    public void SqlServerDialect_HasReturningIdentityClause_Output()
    {
        // Arrange & Act
        var clause = SqlServerDialect.Instance.ReturningIdentityClause;

        // Assert
        clause.Should().NotBeNull();
        clause.Should().Be(" OUTPUT INSERTED.id AS ID");
    }

    [Fact]
    public void MySqlDialect_ReturningIdentityClause_IsNull()
    {
        // Arrange & Act
        ISqlDialect dialect = MySqlDialect.Instance;
        var clause = dialect.ReturningIdentityClause;

        // Assert
        clause.Should().BeNull("MySQL doesn't support RETURNING clause, uses LAST_INSERT_ID() instead");
    }

    #endregion

    #region Insert SQL Generation Tests

    [Fact]
    public void BuildInsertSql_WithPostgres_ReturningClause_Appended()
    {
        // Arrange
        var dialect = PostgresDialect.Instance;
        var tableRef = dialect.TableReference("public", "Users");
        var columns = "\"Name\", \"Email\"";
        var values = "@Name, @Email";
        var returning = dialect.ReturningIdentityClause;

        // Act
        var sql = $"INSERT INTO {tableRef}({columns}) VALUES({values}){returning};";

        // Assert
        sql.Should().Be("INSERT INTO \"public\".\"Users\"(\"Name\", \"Email\") VALUES(@Name, @Email) RETURNING id AS ID;");
    }

    [Fact]
    public void BuildInsertSql_WithSqlServer_OutputClause_Appended()
    {
        // Arrange
        var dialect = SqlServerDialect.Instance;
        var tableRef = dialect.TableReference("dbo", "Users");
        var columns = "[Name], [Email]";
        var values = "@Name, @Email";
        var returning = dialect.ReturningIdentityClause;

        // Act
        // OUTPUT clause goes between INSERT and VALUES in SQL Server
        var sql = $"INSERT INTO {tableRef}{returning} ({columns}) VALUES({values});";

        // Assert
        sql.Should().Be("INSERT INTO [dbo].[Users] OUTPUT INSERTED.id AS ID ([Name], [Email]) VALUES(@Name, @Email);");
    }

    [Fact]
    public void BuildInsertSql_WithSqlite_ReturningClause_Appended()
    {
        // Arrange
        var dialect = SqliteDialect.Instance;
        var tableRef = dialect.TableReference(null, "Users");
        var columns = "\"Name\", \"Email\"";
        var values = "@Name, @Email";
        var returning = dialect.ReturningIdentityClause;

        // Act
        var sql = $"INSERT INTO {tableRef}({columns}) VALUES({values}){returning};";

        // Assert
        sql.Should().Be("INSERT INTO \"Users\"(\"Name\", \"Email\") VALUES(@Name, @Email) RETURNING rowid AS ID;");
    }

    [Fact]
    public void BuildInsertSql_WithMySql_FallbackToLastInsertId()
    {
        // Arrange
        ISqlDialect dialect = MySqlDialect.Instance;
        var tableRef = dialect.TableReference(null, "Users");
        var columns = "`Name`, `Email`";
        var values = "@Name, @Email";
        var returning = dialect.ReturningIdentityClause;

        // Act - MySQL doesn't support RETURNING, so we use the fallback
        var sql = returning != null
            ? $"INSERT INTO {tableRef}({columns}) VALUES({values}){returning};"
            : $"INSERT INTO {tableRef}({columns}) VALUES({values}); SELECT {dialect.LastInsertedIdentity} ID;";

        // Assert
        sql.Should().Be("INSERT INTO `Users`(`Name`, `Email`) VALUES(@Name, @Email); SELECT LAST_INSERT_ID() ID;");
    }

    #endregion

    #region Module Optional Tests

    [Fact]
    public void ModulesWrap_WithEmptyModules_ReturnsEmptyArrays()
    {
        // Arrange
        var wrap = new ModulesWrap
        {
            Modules = Array.Empty<IMutationModule>()
        };
        var model = StandardTestFixtures.SimpleUsers();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        // Act
        var insertErrors = wrap.Insert(data, table, new Dictionary<string, object?>(), model);
        var updateErrors = wrap.Update(data, table, new Dictionary<string, object?>(), model);
        var deleteErrors = wrap.Delete(data, table, new Dictionary<string, object?>(), model);

        // Assert
        insertErrors.Should().BeEmpty();
        updateErrors.Should().BeEmpty();
        deleteErrors.Should().BeEmpty();
    }

    [Fact]
    public void ModulesWrap_WithNoModules_DoesNotModifyData()
    {
        // Arrange
        var wrap = new ModulesWrap
        {
            Modules = Array.Empty<IMutationModule>()
        };
        var model = StandardTestFixtures.SimpleUsers();
        var table = model.GetTableFromDbName("Users");
        var originalData = new Dictionary<string, object?> { ["Name"] = "Alice" };
        var data = new Dictionary<string, object?>(originalData);

        // Act
        wrap.Insert(data, table, new Dictionary<string, object?>(), model);

        // Assert
        data.Should().Equal(originalData);
    }

    #endregion

    #region Cross-Dialect Insert Tests

    [Theory]
    [InlineData(typeof(PostgresDialect), " RETURNING id AS ID")]
    [InlineData(typeof(SqliteDialect), " RETURNING rowid AS ID")]
    [InlineData(typeof(SqlServerDialect), " OUTPUT INSERTED.id AS ID")]
    public void AllDialects_ExceptMySql_SupportReturningIdentityClause(Type dialectType, string expectedClause)
    {
        // Arrange
        var dialect = (ISqlDialect)Activator.CreateInstance(dialectType, true)!;

        // Act
        var clause = dialect.ReturningIdentityClause;

        // Assert
        clause.Should().Be(expectedClause);
    }

    [Fact]
    public void MySql_UsesLastInsertedIdentity_Fallback()
    {
        // Arrange
        ISqlDialect dialect = MySqlDialect.Instance;

        // Act
        var clause = dialect.ReturningIdentityClause;
        var lastIdentity = dialect.LastInsertedIdentity;

        // Assert
        clause.Should().BeNull();
        lastIdentity.Should().Be("LAST_INSERT_ID()");
    }

    #endregion
}
