using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Test.TestSupport;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Pins the SQL emitted for the change-history before-image capture read. Under
/// READ COMMITTED a plain SELECT releases its share lock immediately, so a
/// concurrent transaction can commit between the pre-image read and the UPDATE it
/// precedes — the trail would then attribute the other writer's changes to this
/// actor. The capture read therefore takes an update (write-intent) lock in the
/// dialect's native form: SQL Server's WITH (UPDLOCK) table hint after the FROM
/// table reference, PostgreSQL/MySQL's trailing FOR UPDATE, and nothing on SQLite
/// (whole-database write locking — the transaction already serializes writers).
/// Only the before-image read locks; ordinary reads are unchanged.
/// </summary>
public sealed class BeforeImageUpdateLockSqlTests
{
    private static IDbTable Table(string schema = "")
    {
        var fixture = DbModelTestFixture.Create()
            .WithTable("orders", t =>
            {
                t.WithPrimaryKey("id");
                t.WithColumn("status", "nvarchar");
                if (schema.Length > 0) t.WithSchema(schema);
            });
        return fixture.Build().GetTableFromDbName("orders");
    }

    private static string Sql(ISqlDialect dialect, IDbTable table, bool forUpdate) =>
        MutationCommandExecutor.BuildSelectRowByKeySql(
            dialect, table, new[] { "id", "status" }, new[] { "id" }, forUpdate);

    [Fact]
    public void SqlServer_ForUpdate_TakesUpdlockTableHint_AfterTheFromTable()
    {
        var sql = Sql(SqlServerDialect.Instance, Table("dbo"), forUpdate: true);

        sql.Should().Be("SELECT [id],[status] FROM [dbo].[orders] WITH (UPDLOCK) WHERE [id]=@id;");
        SqlSyntax.AssertValid(sql, SqlFlavor.SqlServer, "the UPDLOCK hint must sit after the FROM table, not at the end");
    }

    [Fact]
    public void Postgres_ForUpdate_AppendsForUpdate()
    {
        var sql = Sql(Ngsql.PostgresDialect.Instance, Table(), forUpdate: true);

        sql.Should().Be("SELECT \"id\",\"status\" FROM \"orders\" WHERE \"id\"=@id FOR UPDATE;");
        SqlSyntax.AssertValid(sql, SqlFlavor.Postgres);
    }

    [Fact]
    public void MySql_ForUpdate_AppendsForUpdate()
    {
        var sql = Sql(MySql.MySqlDialect.Instance, Table(), forUpdate: true);

        sql.Should().Be("SELECT `id`,`status` FROM `orders` WHERE `id`=@id FOR UPDATE;");
        SqlSyntax.AssertValid(sql, SqlFlavor.MySql);
    }

    [Fact]
    public void Sqlite_ForUpdate_IsANoOp_WholeDatabaseWriteLockingAlreadySerializes()
    {
        var sql = Sql(SqliteDialect.Instance, Table(), forUpdate: true);

        sql.Should().Be("SELECT \"id\",\"status\" FROM \"orders\" WHERE \"id\"=@id;");
        SqlSyntax.AssertValid(sql, SqlFlavor.Sqlite);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithoutForUpdate_NoDialectEmitsAnyLockSyntax(bool sqlServer)
    {
        // Ordinary reads (after-image read-back included) stay lock-hint free.
        var dialect = sqlServer ? (ISqlDialect)SqlServerDialect.Instance : Ngsql.PostgresDialect.Instance;
        var sql = Sql(dialect, sqlServer ? Table("dbo") : Table(), forUpdate: false);

        sql.Should().NotContain("UPDLOCK").And.NotContain("FOR UPDATE");
    }
}
