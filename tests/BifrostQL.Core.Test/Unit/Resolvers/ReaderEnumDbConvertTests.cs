using BifrostQL.Core.Resolvers;
using FluentAssertions;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Tests for <see cref="ReaderEnum.DbConvert"/> scalar normalization.
/// SQL `time`/`date` columns map to the GraphQL String scalar, but ADO
/// providers return TimeSpan/TimeOnly/DateOnly. GraphQL.NET's String scalar
/// throws on non-string values, so DbConvert must coerce these to ISO strings.
/// </summary>
public class ReaderEnumDbConvertTests
{
    [Fact]
    public void DbConvert_DBNull_ReturnsNull()
    {
        ReaderEnum.DbConvert(DBNull.Value).Should().BeNull();
    }

    [Fact]
    public void DbConvert_Null_ReturnsNull()
    {
        ReaderEnum.DbConvert(null).Should().BeNull();
    }

    [Fact]
    public void DbConvert_TimeSpan_ReturnsRoundTrippableString()
    {
        // SQL Server `time` -> TimeSpan
        var value = new TimeSpan(0, 13, 45, 30, 123);

        var result = ReaderEnum.DbConvert(value);

        result.Should().Be("13:45:30.1230000");
    }

    [Fact]
    public void DbConvert_TimeOnly_ReturnsIsoString()
    {
        // Npgsql `time` -> TimeOnly
        var value = new TimeOnly(9, 5, 7);

        var result = ReaderEnum.DbConvert(value);

        result.Should().Be("09:05:07.0000000");
    }

    [Fact]
    public void DbConvert_DateOnly_ReturnsIsoString()
    {
        // Npgsql `date` -> DateOnly
        var value = new DateOnly(2026, 6, 22);

        var result = ReaderEnum.DbConvert(value);

        result.Should().Be("2026-06-22");
    }

    [Fact]
    public void DbConvert_String_PassesThrough()
    {
        ReaderEnum.DbConvert("hello").Should().Be("hello");
    }

    [Fact]
    public void DbConvert_Int_PassesThrough()
    {
        ReaderEnum.DbConvert(42).Should().Be(42);
    }

    [Fact]
    public void DbConvert_DateTime_PassesThrough()
    {
        // datetime/datetime2 map to the DateTime scalar, which handles DateTime
        // natively — must NOT be stringified.
        var value = new DateTime(2026, 6, 22, 10, 30, 0, DateTimeKind.Utc);

        ReaderEnum.DbConvert(value).Should().Be(value);
    }
}
