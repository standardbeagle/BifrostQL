using BifrostQL.Ngsql;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

public sealed class ArrayParameterCastTests
{
    [Theory]
    [InlineData("tags", "text[]", "@tags::text[]")]
    [InlineData("values", "integer[]", "@values::integer[]")]
    public void Postgres_CastsKnownArrayColumns(string column, string dataType, string expected)
    {
        PostgresDialect.Instance.AssignmentPlaceholder(column, dataType).Should().Be(expected,
            "array literals bound as strings need an explicit PostgreSQL array type");
    }

    [Theory]
    [InlineData("text[]", "@p0::text[]")]
    [InlineData("integer[]", "@p0::integer[]")]
    public void Postgres_CastsKnownArrayFilterParameters(string dataType, string expected)
    {
        PostgresDialect.Instance.CastParameterReference("@p0", dataType).Should().Be(expected);
    }

    [Theory]
    [InlineData("ARRAY")]
    [InlineData("USER-DEFINED[]")]
    [InlineData("_agtype[]")]
    public void Postgres_LeavesUnknownAndUserDefinedArraysBare(string dataType)
    {
        PostgresDialect.Instance.CastParameterReference("@p0", dataType).Should().Be("@p0");
    }
}
