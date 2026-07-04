using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Tests that WHERE-clause filter parameters are cast to the column's type the same way
/// INSERT/UPDATE assignments are. Without it Postgres rejects e.g.
/// <c>{ week_of: { _eq: "2026-06-08" } }</c> with "operator does not exist: date = text",
/// because Npgsql binds the string value as text and Postgres applies no implicit cast to a
/// text-typed comparison parameter (only to an unknown-typed literal).
/// </summary>
public sealed class FilterParameterCastTests
{
    [Theory]
    [InlineData("date", "@p0::date")]
    [InlineData("timestamp with time zone", "@p0::timestamp with time zone")]
    [InlineData("uuid", "@p0::uuid")]
    [InlineData("jsonb", "@p0::jsonb")]
    [InlineData("numeric", "@p0::numeric")]
    [InlineData("boolean", "@p0::boolean")]
    public void Postgres_CastParameterReference_CastsNonStringTypes(string dataType, string expected)
    {
        PostgresDialect.Instance.CastParameterReference("@p0", dataType).Should().Be(expected);
    }

    [Theory]
    [InlineData("character varying")]
    [InlineData("text")]
    [InlineData("USER-DEFINED")]
    [InlineData("ARRAY")]
    // Regression: a model may carry a SqlServer-style type name for a column that is really
    // `text` in PG. Casting to ::nvarchar raised 42704 "type does not exist" and broke every
    // string filter. Unknown type names must stay bare.
    [InlineData("nvarchar")]
    [InlineData("nchar")]
    public void Postgres_CastParameterReference_LeavesUncastableBare(string dataType)
    {
        PostgresDialect.Instance.CastParameterReference("@p0", dataType).Should().Be("@p0");
    }

    [Theory]
    [InlineData("integer", "@p0::integer")]
    [InlineData("bigint", "@p0::bigint")]
    public void Postgres_CastParameterReference_CastsIntegerTypes(string dataType, string expected)
    {
        PostgresDialect.Instance.CastParameterReference("@p0", dataType).Should().Be(expected);
    }

    [Fact]
    public void Postgres_CastParameterReference_NullTypeBare()
    {
        PostgresDialect.Instance.CastParameterReference("@p0", null).Should().Be("@p0");
    }

    [Fact]
    public void OtherDialects_NeverCastParameterReference()
    {
        SqliteDialect.Instance.CastParameterReference("@p0", "date").Should().Be("@p0");
        MySqlDialect.Instance.CastParameterReference("@p0", "datetime").Should().Be("@p0");
    }

    [Fact]
    public void EqualityFilterOnDateColumn_CastsParameter_Postgres()
    {
        var parameters = new SqlParameterCollection();
        var result = TableFilter.GetSingleFilterParameterized(
            PostgresDialect.Instance, parameters, "meetings", "week_of", "_eq", "2026-06-08", "date");

        result.Sql.Should().Be("\"meetings\".\"week_of\" = @p0::date");
        result.Parameters.Should().ContainSingle().Which.Value.Should().Be("2026-06-08");
    }

    [Fact]
    public void EqualityFilterOnStringColumn_NoCast_Postgres()
    {
        var parameters = new SqlParameterCollection();
        var result = TableFilter.GetSingleFilterParameterized(
            PostgresDialect.Instance, parameters, "users", "name", "_eq", "ann", "character varying");

        result.Sql.Should().Be("\"users\".\"name\" = @p0");
    }

    [Fact]
    public void InFilterOnDateColumn_CastsEachParameter_Postgres()
    {
        var parameters = new SqlParameterCollection();
        var result = TableFilter.GetSingleFilterParameterized(
            PostgresDialect.Instance, parameters, "meetings", "week_of", "_in",
            new object?[] { "2026-06-01", "2026-06-08" }, "date");

        result.Sql.Should().Be("\"meetings\".\"week_of\" IN (@p0::date,@p1::date)");
        result.Parameters.Should().HaveCount(2);
    }

    [Fact]
    public void InFilterWithEmptyList_EmitsAlwaysFalse_NoParameters()
    {
        // "col IN ()" is a syntax error in every dialect; a client-supplied
        // empty array must degrade to an always-false predicate, not a 500.
        var parameters = new SqlParameterCollection();
        var result = TableFilter.GetSingleFilterParameterized(
            PostgresDialect.Instance, parameters, "meetings", "week_of", "_in",
            Array.Empty<object?>(), "date");

        result.Sql.Should().Be("1 = 0");
        result.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void NotInFilterWithEmptyList_EmitsAlwaysTrue_NoParameters()
    {
        var parameters = new SqlParameterCollection();
        var result = TableFilter.GetSingleFilterParameterized(
            PostgresDialect.Instance, parameters, "meetings", "week_of", "_nin",
            Array.Empty<object?>(), "date");

        result.Sql.Should().Be("1 = 1");
        result.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void BetweenFilterOnDateColumn_CastsBothParameters_Postgres()
    {
        var parameters = new SqlParameterCollection();
        var result = TableFilter.GetSingleFilterParameterized(
            PostgresDialect.Instance, parameters, "meetings", "week_of", "_between",
            new object?[] { "2026-06-01", "2026-06-08" }, "date");

        result.Sql.Should().Be("\"meetings\".\"week_of\" BETWEEN @p0::date AND @p1::date");
    }

    [Fact]
    public void EqualityFilterOnDateColumn_NoCast_SqlServer()
    {
        // SQL Server's driver doesn't need the cast; the base dialect leaves it bare.
        var parameters = new SqlParameterCollection();
        var result = TableFilter.GetSingleFilterParameterized(
            SqlServerDialect.Instance, parameters, "meetings", "week_of", "_eq", "2026-06-08", "date");

        result.Sql.Should().Be("[meetings].[week_of] = @p0");
    }

    [Fact]
    public void UnknownColumnType_NoCast_Postgres()
    {
        // Callers that can't resolve a column type (synthetic join "value") pass null → bare.
        var parameters = new SqlParameterCollection();
        var result = TableFilter.GetSingleFilterParameterized(
            PostgresDialect.Instance, parameters, "j", "value", "_eq", "x", null);

        result.Sql.Should().Be("\"j\".\"value\" = @p0");
    }
}
