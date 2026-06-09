using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Tests for <see cref="ISqlDialect.AssignmentPlaceholder"/> — the INSERT VALUES / UPDATE SET
/// placeholder that casts a bound parameter to the column's real type. Postgres needs this
/// because Npgsql binds a CLR string as an explicit <c>text</c> type and Postgres applies no
/// implicit assignment cast to a text-typed parameter (unlike an unknown-typed literal), so
/// "SET started_at = $1" with a string value otherwise fails:
/// "column ... is of type timestamp with time zone but expression is of type text".
/// </summary>
public sealed class AssignmentPlaceholderTests
{
    [Theory]
    [InlineData("started_at", "timestamp with time zone", "@started_at::timestamp with time zone")]
    [InlineData("born_on", "date", "@born_on::date")]
    [InlineData("id", "uuid", "@id::uuid")]
    [InlineData("payload", "jsonb", "@payload::jsonb")]
    [InlineData("doc", "json", "@doc::json")]
    [InlineData("amount", "numeric", "@amount::numeric")]
    [InlineData("count", "integer", "@count::integer")]
    [InlineData("active", "boolean", "@active::boolean")]
    public void Postgres_CastsNonStringColumns(string column, string dataType, string expected)
    {
        PostgresDialect.Instance.AssignmentPlaceholder(column, dataType).Should().Be(expected);
    }

    [Theory]
    [InlineData("name", "character varying")]
    [InlineData("name", "varchar")]
    [InlineData("name", "text")]
    [InlineData("code", "char")]
    public void Postgres_LeavesNativeStringColumnsBare(string column, string dataType)
    {
        // A text-bound parameter already matches a text column — no cast needed.
        PostgresDialect.Instance.AssignmentPlaceholder(column, dataType).Should().Be($"@{column}");
    }

    [Theory]
    [InlineData("props", "USER-DEFINED")] // Apache AGE agtype etc. — no plain ::text cast path
    [InlineData("tags", "ARRAY")]
    public void Postgres_LeavesUserDefinedAndArrayBare(string column, string dataType)
    {
        PostgresDialect.Instance.AssignmentPlaceholder(column, dataType).Should().Be($"@{column}");
    }

    [Fact]
    public void Postgres_UnknownTypeBare()
    {
        PostgresDialect.Instance.AssignmentPlaceholder("x", null).Should().Be("@x");
        PostgresDialect.Instance.AssignmentPlaceholder("x", "").Should().Be("@x");
    }

    [Theory]
    [InlineData("started_at", "datetime")]
    [InlineData("id", "uniqueidentifier")]
    [InlineData("name", "nvarchar")]
    public void OtherDialects_NeverCast(string column, string dataType)
    {
        // SQL Server / MySQL / SQLite drivers don't need the cast; the base default
        // returns the bare placeholder for every type.
        SqliteDialect.Instance.AssignmentPlaceholder(column, dataType).Should().Be($"@{column}");
        MySqlDialect.Instance.AssignmentPlaceholder(column, dataType).Should().Be($"@{column}");
    }
}
