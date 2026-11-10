using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.SqlServer;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// An insert with no columns fills every column from its default. The empty column list
/// <c>() VALUES()</c> is MySQL's spelling only; the other engines need <c>DEFAULT VALUES</c>.
/// </summary>
public sealed class EmptyInsertSqlTests
{
    public static TheoryData<string, string> Dialects => new()
    {
        { "sqlserver", "INSERT INTO [dbo].[labels] DEFAULT VALUES" },
        { "postgres", "INSERT INTO \"dbo\".\"labels\" DEFAULT VALUES" },
        { "sqlite", "INSERT INTO \"dbo\".\"labels\" DEFAULT VALUES" },
        { "mysql", "INSERT INTO `dbo`.`labels`() VALUES()" },
    };

    [Theory]
    [MemberData(nameof(Dialects))]
    public void NoColumns_RendersTheDialectsDefaultValuesForm(string name, string expected)
    {
        var dialect = Dialect(name);
        var table = DbModelTestFixture.Create()
            .WithTable("labels", t => t.WithSchema("dbo").WithPrimaryKey("id"))
            .Build().GetTableFromDbName("labels");
        var tableRef = dialect.TableReference(table.TableSchema, table.DbName);

        MutationCommandExecutor.BuildInsertInto(dialect, table, tableRef, Array.Empty<string>())
            .Should().Be(expected);
    }

    private static ISqlDialect Dialect(string name) => name switch
    {
        "sqlserver" => SqlServerDialect.Instance,
        "postgres" => PostgresDialect.Instance,
        "sqlite" => SqliteDialect.Instance,
        "mysql" => MySqlDialect.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
