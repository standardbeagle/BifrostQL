using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using BifrostQL.Testing;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Verifies the date-arithmetic (<see cref="SqlExpr.DateAdd"/> / <see cref="SqlExpr.DateDiff"/> /
/// <see cref="SqlExpr.DatePart"/>) and JSON (<see cref="SqlExpr.JsonGet"/>) nodes lower to each
/// dialect's NATIVE form, that the shared node matrix is syntactically valid on every engine, that
/// a dialect which cannot lower a node fails fast naming the node + dialect, and that a
/// <see cref="JsonPath"/> segment cannot inject SQL or JSON-path syntax.
/// </summary>
public sealed class SqlExprDateJsonLoweringTest
{
    private static IDbTable EventsTable()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Events", t => t
                .WithPrimaryKey("Id", "int")
                .WithColumn("StartAt", "datetime")
                .WithColumn("EndAt", "datetime")
                .WithColumn("Payload", "nvarchar"))
            .Build();
        return model.GetTableFromDbName("Events");
    }

    private static SqlFlavor FlavorOf(ISqlDialect dialect) => dialect switch
    {
        SqlServerDialect => SqlFlavor.SqlServer,
        PostgresDialect => SqlFlavor.Postgres,
        MySqlDialect => SqlFlavor.MySql,
        SqliteDialect => SqlFlavor.Sqlite,
        _ => throw new ArgumentException($"Unmapped dialect {dialect.GetType().Name}")
    };

    // --- Criterion 1 & 3: every node lowers on every dialect and passes syntax validation ------
    // DateDiff uses Day (epoch/Julian-day computable everywhere) so the shared matrix is uniform.

    public static IEnumerable<object[]> MatrixExpressions()
    {
        yield return new object[] { "DateAdd", new SqlExpr.DateAdd(new SqlExpr.Col("StartAt"), DateUnit.Day, new SqlExpr.Lit(3)) };
        yield return new object[] { "DateDiff", new SqlExpr.DateDiff(DateUnit.Day, new SqlExpr.Col("StartAt"), new SqlExpr.Col("EndAt")) };
        yield return new object[] { "DatePart", new SqlExpr.DatePart(DateUnit.Year, new SqlExpr.Col("StartAt")) };
        yield return new object[] { "JsonGet", new SqlExpr.JsonGet(new SqlExpr.Col("Payload"), new JsonPath("user", "name")) };
    }

    public static IEnumerable<object[]> MatrixByDialect()
    {
        foreach (var dialect in CrossDialectTest.AllDialects)
            foreach (var row in MatrixExpressions())
                yield return new[] { dialect, row[0], row[1] };
    }

    [Theory]
    [MemberData(nameof(MatrixByDialect))]
    public void Node_LowersToValidSql_OnEveryDialect(ISqlDialect dialect, string label, SqlExpr expr)
    {
        var parameters = new SqlParameterCollection();

        var sql = dialect.LowerExpression(expr, EventsTable(), parameters);

        sql.Should().NotBeNullOrWhiteSpace($"{label} must lower to a non-empty fragment on {dialect.GetType().Name}");
        SqlSyntax.AssertValid($"SELECT {sql} AS Expr", FlavorOf(dialect),
            $"lowered {label} on {dialect.GetType().Name} must be valid SQL");
    }

    // --- Criterion 1: each dialect emits its NATIVE spelling ------------------------------------

    public static IEnumerable<object[]> NativeFormData()
    {
        // dialect, node, substrings that must appear in the native lowering
        yield return new object[] { SqlServerDialect.Instance, new SqlExpr.DateAdd(new SqlExpr.Col("StartAt"), DateUnit.Day, new SqlExpr.Lit(3)), new[] { "DATEADD(day," } };
        yield return new object[] { SqlServerDialect.Instance, new SqlExpr.DateDiff(DateUnit.Day, new SqlExpr.Col("StartAt"), new SqlExpr.Col("EndAt")), new[] { "DATEDIFF(day," } };
        yield return new object[] { SqlServerDialect.Instance, new SqlExpr.DatePart(DateUnit.Year, new SqlExpr.Col("StartAt")), new[] { "DATEPART(year," } };
        yield return new object[] { SqlServerDialect.Instance, new SqlExpr.JsonGet(new SqlExpr.Col("Payload"), new JsonPath("user", "name")), new[] { "JSON_VALUE(", "'$.user.name'" } };

        yield return new object[] { PostgresDialect.Instance, new SqlExpr.DateAdd(new SqlExpr.Col("StartAt"), DateUnit.Day, new SqlExpr.Lit(3)), new[] { "INTERVAL '1 day'" } };
        yield return new object[] { PostgresDialect.Instance, new SqlExpr.DatePart(DateUnit.Year, new SqlExpr.Col("StartAt")), new[] { "EXTRACT(YEAR FROM " } };
        yield return new object[] { PostgresDialect.Instance, new SqlExpr.JsonGet(new SqlExpr.Col("Payload"), new JsonPath("user", "name")), new[] { "-> 'user'", "->> 'name'" } };

        yield return new object[] { MySqlDialect.Instance, new SqlExpr.DateAdd(new SqlExpr.Col("StartAt"), DateUnit.Day, new SqlExpr.Lit(3)), new[] { "DATE_ADD(", "INTERVAL", "DAY" } };
        yield return new object[] { MySqlDialect.Instance, new SqlExpr.DateDiff(DateUnit.Month, new SqlExpr.Col("StartAt"), new SqlExpr.Col("EndAt")), new[] { "TIMESTAMPDIFF(MONTH," } };
        yield return new object[] { MySqlDialect.Instance, new SqlExpr.JsonGet(new SqlExpr.Col("Payload"), new JsonPath("user", "name")), new[] { "JSON_UNQUOTE(JSON_EXTRACT(", "'$.user.name'" } };

        yield return new object[] { SqliteDialect.Instance, new SqlExpr.DateAdd(new SqlExpr.Col("StartAt"), DateUnit.Day, new SqlExpr.Lit(3)), new[] { "datetime(", "|| ' days'" } };
        yield return new object[] { SqliteDialect.Instance, new SqlExpr.DatePart(DateUnit.Year, new SqlExpr.Col("StartAt")), new[] { "strftime('%Y'," } };
        yield return new object[] { SqliteDialect.Instance, new SqlExpr.JsonGet(new SqlExpr.Col("Payload"), new JsonPath("user", "name")), new[] { "json_extract(", "'$.user.name'" } };
    }

    [Theory]
    [MemberData(nameof(NativeFormData))]
    public void Node_EmitsDialectNativeForm(ISqlDialect dialect, SqlExpr expr, string[] expectedSubstrings)
    {
        var sql = dialect.LowerExpression(expr, EventsTable(), new SqlParameterCollection());

        foreach (var expected in expectedSubstrings)
            sql.Should().Contain(expected, $"{dialect.GetType().Name} native form");
    }

    // --- Criterion 3 (params): the numeric amount binds as a parameter, never interpolated ------

    [Theory]
    [MemberData(nameof(CrossDialectTest.AllDialectData), MemberType = typeof(CrossDialectTest))]
    public void DateAdd_AmountBindsAsParameter_NeverInterpolated(ISqlDialect dialect)
    {
        var parameters = new SqlParameterCollection();
        // A distinctive amount that cannot be confused with a unit multiplier in the SQL text.
        var expr = new SqlExpr.DateAdd(new SqlExpr.Col("StartAt"), DateUnit.Day, new SqlExpr.Lit(987654));

        var sql = dialect.LowerExpression(expr, EventsTable(), parameters);

        sql.Should().NotContain("987654", "the amount must lower to a bound parameter, not literal text");
        sql.Should().Contain("@p", "the amount lowers to a parameter placeholder");
        parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be(987654);
    }

    // --- Criterion 4: a dialect that cannot lower a unit throws a typed NotSupported error -------

    public static IEnumerable<object[]> EpochOnlyDialects()
    {
        yield return new object[] { PostgresDialect.Instance, "PostgreSQL" };
        yield return new object[] { SqliteDialect.Instance, "SQLite" };
    }

    [Theory]
    [MemberData(nameof(EpochOnlyDialects))]
    public void DateDiff_WholeMonth_FailsFast_NamingNodeAndDialect(ISqlDialect dialect, string dialectName)
    {
        // Postgres/SQLite can only difference via epoch/Julian-day math, which cannot count
        // calendar months exactly — so this must fail fast, not emit a wrong approximation.
        var expr = new SqlExpr.DateDiff(DateUnit.Month, new SqlExpr.Col("StartAt"), new SqlExpr.Col("EndAt"));

        var act = () => dialect.LowerExpression(expr, EventsTable(), new SqlParameterCollection());

        act.Should().Throw<SqlExprLoweringNotSupportedException>()
            .Which.Should().Match<SqlExprLoweringNotSupportedException>(e =>
                e.NodeType == "DateDiff" && e.Dialect == dialectName)
            .And.Match<SqlExprLoweringNotSupportedException>(e =>
                e.Message.Contains("DateDiff") && e.Message.Contains(dialectName));
    }

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("MySql")]
    public void DateDiff_WholeMonth_SupportedNatively_OnCalendarAwareDialects(string dialectName)
    {
        // SQL Server DATEDIFF and MySQL TIMESTAMPDIFF count calendar boundaries natively.
        ISqlDialect dialect = dialectName == "SqlServer"
            ? SqlServerDialect.Instance
            : MySqlDialect.Instance;
        var expr = new SqlExpr.DateDiff(DateUnit.Month, new SqlExpr.Col("StartAt"), new SqlExpr.Col("EndAt"));

        var act = () => dialect.LowerExpression(expr, EventsTable(), new SqlParameterCollection());

        act.Should().NotThrow("whole-month difference is native on this engine");
    }

    // --- Criterion 5: a JsonPath segment cannot inject SQL or JSON-path syntax ------------------
    // These are REVERT-PROOF: with the SafeSegment guard removed from JsonPath, each of these
    // constructions succeeds and the malicious text flows into the emitted SQL literal — so each
    // assertion below fails (turns RED) exactly when the guard is gone. See
    // .claude/rules/regression-test-non-vacuous.md.

    [Theory]
    [InlineData("name'); DROP TABLE Events;--")] // SQL statement break-out
    [InlineData("name' OR '1'='1")]               // SQL boolean break-out
    [InlineData("a.b")]                           // nested-path injection (extra path level)
    [InlineData("$")]                             // JSON-path root metacharacter
    [InlineData("a')].b")]                        // path/paren break-out
    [InlineData("a b")]                           // whitespace
    [InlineData("")]                              // empty segment
    [InlineData("2legit")]                        // leading digit (not an identifier)
    public void JsonPath_UnsafeSegment_RejectedAtConstruction(string maliciousSegment)
    {
        var act = () => new JsonPath("user", maliciousSegment);

        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*Unsafe JSON path segment*");
    }

    [Fact]
    public void JsonPath_EmptyPath_Rejected()
    {
        var act = () => new JsonPath(Array.Empty<string>());

        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*at least one segment*");
    }

    [Theory]
    [MemberData(nameof(CrossDialectTest.AllDialectData), MemberType = typeof(CrossDialectTest))]
    public void JsonGet_ValidPath_LowersToSyntaxValidSqlWithoutRawInjection(ISqlDialect dialect)
    {
        // A path whose segments are legal identifiers but whose lowering must still be safe SQL.
        var expr = new SqlExpr.JsonGet(new SqlExpr.Col("Payload"), new JsonPath("account", "user_id"));

        var sql = dialect.LowerExpression(expr, EventsTable(), new SqlParameterCollection());

        // No stray quote/semicolon structure — the path only ever contributes safe identifier text.
        sql.Should().NotContain(";");
        sql.Should().NotContain("--");
        SqlSyntax.AssertValid($"SELECT {sql} AS Expr", FlavorOf(dialect),
            $"lowered JsonGet on {dialect.GetType().Name} must be valid SQL");
    }
}
