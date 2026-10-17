using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Pins the SQL of the state-machine current-row read that gates a transition. It was a
/// plain SELECT, so under READ COMMITTED two concurrent transitions of one row both read
/// the same from-state and both passed validation. The read now takes the dialect's update
/// lock in the same native forms as the before-image capture
/// (<see cref="BeforeImageUpdateLockSqlTests"/>): SQL Server's <c>WITH (UPDLOCK)</c> after
/// the table reference, Postgres/MySQL <c>FOR UPDATE</c> after the WHERE — AFTER the row
/// scope suffix, which is part of the WHERE — and nothing on SQLite. Asserted on the
/// builder's own output, whole statement, and parsed by each dialect's real grammar.
/// </summary>
public sealed class StateMachineLoadLockSqlTests
{
    private const string Scope = " AND (\"tenant_id\" = @p0)";

    private static IDbTable Table(string schema = "")
    {
        var fixture = DbModelTestFixture.Create()
            .WithTable("tickets", t =>
            {
                t.WithPrimaryKey("id");
                t.WithColumn("status", "nvarchar");
                if (schema.Length > 0) t.WithSchema(schema);
            });
        return fixture.Build().GetTableFromDbName("tickets");
    }

    private static string Sql(ISqlDialect dialect, IDbTable table, string scope = "") =>
        MutationCommandExecutor.BuildStateMachineLoadSql(dialect, table, "status", new[] { "id" }, scope);

    [Fact]
    public void SqlServer_TakesUpdlockTableHint_AfterTheFromTable()
    {
        var sql = Sql(SqlServerDialect.Instance, Table("dbo"), " AND ([tenant_id] = @p0)");

        sql.Should().Be("SELECT [status] FROM [dbo].[tickets] WITH (UPDLOCK) WHERE [id]=@id AND ([tenant_id] = @p0);");
        SqlSyntax.AssertValid(sql, SqlFlavor.SqlServer);
    }

    [Fact]
    public void Postgres_AppendsForUpdate_AfterTheRowScope()
    {
        var sql = Sql(Ngsql.PostgresDialect.Instance, Table(), Scope);

        sql.Should().Be("SELECT \"status\" FROM \"tickets\" WHERE \"id\"=@id AND (\"tenant_id\" = @p0) FOR UPDATE;");
        SqlSyntax.AssertValid(sql, SqlFlavor.Postgres);
    }

    [Fact]
    public void MySql_AppendsForUpdate_AfterTheRowScope()
    {
        var sql = Sql(MySql.MySqlDialect.Instance, Table(), " AND (`tenant_id` = @p0)");

        sql.Should().Be("SELECT `status` FROM `tickets` WHERE `id`=@id AND (`tenant_id` = @p0) FOR UPDATE;");
        SqlSyntax.AssertValid(sql, SqlFlavor.MySql);
    }

    [Fact]
    public void Sqlite_EmitsNoLockSyntax_WholeDatabaseWriteLockingSerializes()
    {
        var sql = Sql(SqliteDialect.Instance, Table(), Scope);

        sql.Should().Be("SELECT \"status\" FROM \"tickets\" WHERE \"id\"=@id AND (\"tenant_id\" = @p0);");
        SqlSyntax.AssertValid(sql, SqlFlavor.Sqlite);
    }
}
