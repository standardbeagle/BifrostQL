using System.Data;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Proves that an <see cref="ComputedColumnKind.Expression"/> computed column is wired end to
/// end into SELECT generation: <see cref="GqlObjectQuery.AddSqlParameterized"/> threads the
/// live <see cref="SqlParameterCollection"/> through <see cref="GqlObjectColumn.ToSelectSql"/>
/// so the expression's Lit/Param values land as BOUND parameters on the query's collection —
/// never interpolated — and the column returns correct values when the emitted SQL executes.
/// </summary>
public sealed class ComputedExpressionSelectEmissionTests
{
    private static IDbModel BuildModel()
        => DbModelTestFixture.Create()
            .WithTable("People", t => t
                .WithPrimaryKey("Id")
                .WithColumn("first_name", "varchar", graphQlName: "firstName")
                .WithColumn("last_name", "varchar", graphQlName: "lastName"))
            .Build();

    // UPPER({firstName}) || ' ' || {lastName}
    private static ComputedColumnDefinition FullNameExpr(string separator = " ")
        => new("fullName", "String", ComputedColumnKind.Expression, "expr", new[] { "firstName", "lastName" })
        {
            Expression = new SqlExpr.Concat(new SqlExpr[]
            {
                new SqlExpr.Fn("UPPER", new SqlExpr[] { new SqlExpr.Col("firstName") }),
                new SqlExpr.Lit(separator),
                new SqlExpr.Col("lastName"),
            }),
        };

    private static GqlObjectQuery QueryWith(IDbTable table, params GqlObjectColumn[] columns)
    {
        var query = GqlObjectQueryBuilder.Create().WithDbTable(table).Build();
        query.ScalarColumns.AddRange(columns);
        return query;
    }

    // --- Acceptance 1: emitted into the SELECT with every literal bound as a parameter --------
    [Fact]
    public void AddSqlParameterized_ExpressionComputedColumn_EmitsBoundParameters_LiteralAbsent()
    {
        var model = BuildModel();
        var table = model.GetTableFromDbName("People");
        var query = QueryWith(table, new GqlObjectColumn(FullNameExpr(), "fullName"));
        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        query.AddSqlParameterized(model, SqliteDialect.Instance, sqls, parameters);

        var sql = sqls.Values.Single().Sql;
        // The lowered expression is projected under its GraphQL alias, with the dependency
        // columns resolved to their DB names and the separator lowered to a bound parameter.
        sql.Should().Contain("UPPER(\"first_name\")").And.Contain("\"last_name\"");
        sql.Should().Contain("@p").And.Contain("\"fullName\"");
        // The literal separator text must NOT appear inline in the SQL — it is a parameter.
        sql.Should().NotContain("' '");
        parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be(" ");
    }

    // --- Acceptance 2: correct values returned end-to-end via a real SQLite query -------------
    [Fact]
    public async Task ExpressionComputedColumn_ReturnsCorrectValues_OnSqlite()
    {
        const string connString = "Data Source=bifrost_computed_expr_select_test;Mode=Memory;Cache=Shared";
        await using var keepAlive = new SqliteConnection(connString);
        await keepAlive.OpenAsync();
        await Exec(keepAlive, "DROP TABLE IF EXISTS People");
        await Exec(keepAlive, "CREATE TABLE People (Id INTEGER PRIMARY KEY, first_name TEXT, last_name TEXT)");
        await Exec(keepAlive, "INSERT INTO People (Id, first_name, last_name) VALUES (1, 'John', 'Doe')");

        var model = BuildModel();
        var table = model.GetTableFromDbName("People");
        var query = QueryWith(table, new GqlObjectColumn(FullNameExpr(), "fullName"));
        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        query.AddSqlParameterized(model, SqliteDialect.Instance, sqls, parameters);

        var baseSql = sqls.Values.Single().Sql;
        await using var cmd = new SqliteCommand(baseSql, keepAlive);
        foreach (var p in parameters.Parameters)
            cmd.Parameters.AddWithValue(p.Name, p.Value ?? (object)DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue();
        reader["fullName"].Should().Be("JOHN Doe");
    }

    private static async Task Exec(SqliteConnection conn, string sql)
    {
        await using var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
