using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

public sealed class ParameterizedSqlTest
{
    #region Constructor Tests

    [Fact]
    public void Constructor_WithValidSqlAndEmptyParameters_CreatesInstance()
    {
        // Arrange
        const string sql = "SELECT * FROM Users";
        var parameters = Array.Empty<SqlParameterInfo>();

        // Act
        var sut = new ParameterizedSql(sql, parameters);

        // Assert
        sut.Sql.Should().Be(sql);
        sut.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WithValidSqlAndParameters_CreatesInstance()
    {
        // Arrange
        const string sql = "SELECT * FROM Users WHERE Id = @p0";
        var parameters = new List<SqlParameterInfo>
        {
            new("@p0", 42, "int")
        };

        // Act
        var sut = new ParameterizedSql(sql, parameters);

        // Assert
        sut.Sql.Should().Be(sql);
        sut.Parameters.Should().HaveCount(1);
        sut.Parameters[0].Name.Should().Be("@p0");
        sut.Parameters[0].Value.Should().Be(42);
        sut.Parameters[0].DbType.Should().Be("int");
    }

    [Fact]
    public void Constructor_WithNullSql_ThrowsArgumentNullException()
    {
        // Arrange & Act
        var action = () => new ParameterizedSql(null!, Array.Empty<SqlParameterInfo>());

        // Assert
        action.Should().Throw<ArgumentNullException>()
            .WithParameterName("sql");
    }

    [Fact]
    public void Constructor_WithEmptyString_CreatesInstance()
    {
        // Arrange & Act
        var sut = new ParameterizedSql(string.Empty, Array.Empty<SqlParameterInfo>());

        // Assert
        sut.Sql.Should().BeEmpty();
        sut.Parameters.Should().BeEmpty();
    }

    #endregion

    #region Empty Static Field Tests

    [Fact]
    public void Empty_ReturnsInstanceWithEmptySqlAndNoParameters()
    {
        // Act
        var sut = ParameterizedSql.Empty;

        // Assert
        sut.Sql.Should().BeEmpty();
        sut.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Empty_ReturnsSameInstanceOnMultipleCalls()
    {
        // Act
        var first = ParameterizedSql.Empty;
        var second = ParameterizedSql.Empty;

        // Assert
        first.Should().BeSameAs(second);
    }

    #endregion

    #region Append(ParameterizedSql) Tests

    [Fact]
    public void AppendParameterizedSql_ConcatenatesSqlStrings()
    {
        // Arrange
        var first = new ParameterizedSql("SELECT * FROM Users", Array.Empty<SqlParameterInfo>());
        var second = new ParameterizedSql(" WHERE Active = 1", Array.Empty<SqlParameterInfo>());

        // Act
        var result = first.Append(second);

        // Assert
        result.Sql.Should().Be("SELECT * FROM Users WHERE Active = 1");
    }

    [Fact]
    public void AppendParameterizedSql_CombinesParameters()
    {
        // Arrange
        var firstParams = new List<SqlParameterInfo> { new("@p0", "John", "nvarchar") };
        var secondParams = new List<SqlParameterInfo> { new("@p1", 25, "int") };
        var first = new ParameterizedSql("SELECT * FROM Users WHERE Name = @p0", firstParams);
        var second = new ParameterizedSql(" AND Age > @p1", secondParams);

        // Act
        var result = first.Append(second);

        // Assert
        result.Sql.Should().Be("SELECT * FROM Users WHERE Name = @p0 AND Age > @p1");
        result.Parameters.Should().HaveCount(2);
        result.Parameters[0].Name.Should().Be("@p0");
        result.Parameters[0].Value.Should().Be("John");
        result.Parameters[1].Name.Should().Be("@p1");
        result.Parameters[1].Value.Should().Be(25);
    }

    [Fact]
    public void AppendParameterizedSql_ReturnsNewInstance()
    {
        // Arrange
        var first = new ParameterizedSql("SELECT", Array.Empty<SqlParameterInfo>());
        var second = new ParameterizedSql(" *", Array.Empty<SqlParameterInfo>());

        // Act
        var result = first.Append(second);

        // Assert
        result.Should().NotBeSameAs(first);
        result.Should().NotBeSameAs(second);
    }

    [Fact]
    public void AppendParameterizedSql_DoesNotModifyOriginal()
    {
        // Arrange
        var originalSql = "SELECT * FROM Users";
        var originalParams = new List<SqlParameterInfo> { new("@p0", 1, null) };
        var first = new ParameterizedSql(originalSql, originalParams);
        var second = new ParameterizedSql(" WHERE Id = @p1", new List<SqlParameterInfo> { new("@p1", 2, null) });

        // Act
        _ = first.Append(second);

        // Assert
        first.Sql.Should().Be(originalSql);
        first.Parameters.Should().HaveCount(1);
    }

    [Fact]
    public void AppendParameterizedSql_WithEmpty_ReturnsEquivalentResult()
    {
        // Arrange
        var original = new ParameterizedSql("SELECT * FROM Users", new List<SqlParameterInfo> { new("@p0", 1, null) });

        // Act
        var result = original.Append(ParameterizedSql.Empty);

        // Assert
        result.Sql.Should().Be(original.Sql);
        result.Parameters.Should().BeEquivalentTo(original.Parameters);
    }

    [Fact]
    public void AppendParameterizedSql_ToEmpty_ReturnsAppendedContent()
    {
        // Arrange
        var toAppend = new ParameterizedSql("SELECT * FROM Users", new List<SqlParameterInfo> { new("@p0", 1, null) });

        // Act
        var result = ParameterizedSql.Empty.Append(toAppend);

        // Assert
        result.Sql.Should().Be(toAppend.Sql);
        result.Parameters.Should().BeEquivalentTo(toAppend.Parameters);
    }

    [Fact]
    public void AppendParameterizedSql_MultipleAppends_ChainCorrectly()
    {
        // Arrange
        var select = new ParameterizedSql("SELECT * FROM Users", Array.Empty<SqlParameterInfo>());
        var where = new ParameterizedSql(" WHERE Id = @p0", new List<SqlParameterInfo> { new("@p0", 1, null) });
        var and = new ParameterizedSql(" AND Active = @p1", new List<SqlParameterInfo> { new("@p1", true, null) });
        var orderBy = new ParameterizedSql(" ORDER BY Name", Array.Empty<SqlParameterInfo>());

        // Act
        var result = select.Append(where).Append(and).Append(orderBy);

        // Assert
        result.Sql.Should().Be("SELECT * FROM Users WHERE Id = @p0 AND Active = @p1 ORDER BY Name");
        result.Parameters.Should().HaveCount(2);
    }

    #endregion

    #region Append(string) Tests

    [Fact]
    public void AppendString_ConcatenatesSql()
    {
        // Arrange
        var original = new ParameterizedSql("SELECT * FROM Users", Array.Empty<SqlParameterInfo>());

        // Act
        var result = original.Append(" WHERE Active = 1");

        // Assert
        result.Sql.Should().Be("SELECT * FROM Users WHERE Active = 1");
    }

    [Fact]
    public void AppendString_PreservesParameters()
    {
        // Arrange
        var parameters = new List<SqlParameterInfo> { new("@p0", 42, "int") };
        var original = new ParameterizedSql("SELECT * FROM Users WHERE Id = @p0", parameters);

        // Act
        var result = original.Append(" ORDER BY Name");

        // Assert
        result.Parameters.Should().BeSameAs(parameters);
    }

    [Fact]
    public void AppendString_ReturnsNewInstance()
    {
        // Arrange
        var original = new ParameterizedSql("SELECT", Array.Empty<SqlParameterInfo>());

        // Act
        var result = original.Append(" * FROM Users");

        // Assert
        result.Should().NotBeSameAs(original);
    }

    [Fact]
    public void AppendString_WithEmptyString_ReturnsEquivalent()
    {
        // Arrange
        var original = new ParameterizedSql("SELECT * FROM Users", Array.Empty<SqlParameterInfo>());

        // Act
        var result = original.Append(string.Empty);

        // Assert
        result.Sql.Should().Be(original.Sql);
    }

    [Fact]
    public void AppendString_DoesNotModifyOriginal()
    {
        // Arrange
        const string originalSql = "SELECT * FROM Users";
        var original = new ParameterizedSql(originalSql, Array.Empty<SqlParameterInfo>());

        // Act
        _ = original.Append(" WHERE Id = 1");

        // Assert
        original.Sql.Should().Be(originalSql);
    }

    #endregion

    #region Prepend Tests

    [Fact]
    public void Prepend_AddsPrefixToSql()
    {
        // Arrange
        var original = new ParameterizedSql("Id = @p0", new List<SqlParameterInfo> { new("@p0", 1, null) });

        // Act
        var result = original.Prepend("WHERE ");

        // Assert
        result.Sql.Should().Be("WHERE Id = @p0");
    }

    [Fact]
    public void Prepend_PreservesParameters()
    {
        // Arrange
        var parameters = new List<SqlParameterInfo> { new("@p0", 42, "int") };
        var original = new ParameterizedSql("WHERE Id = @p0", parameters);

        // Act
        var result = original.Prepend("SELECT * FROM Users ");

        // Assert
        result.Parameters.Should().BeSameAs(parameters);
    }

    [Fact]
    public void Prepend_ReturnsNewInstance()
    {
        // Arrange
        var original = new ParameterizedSql("FROM Users", Array.Empty<SqlParameterInfo>());

        // Act
        var result = original.Prepend("SELECT * ");

        // Assert
        result.Should().NotBeSameAs(original);
    }

    [Fact]
    public void Prepend_WithEmptyString_ReturnsEquivalent()
    {
        // Arrange
        var original = new ParameterizedSql("SELECT * FROM Users", Array.Empty<SqlParameterInfo>());

        // Act
        var result = original.Prepend(string.Empty);

        // Assert
        result.Sql.Should().Be(original.Sql);
    }

    [Fact]
    public void Prepend_DoesNotModifyOriginal()
    {
        // Arrange
        const string originalSql = "WHERE Id = 1";
        var original = new ParameterizedSql(originalSql, Array.Empty<SqlParameterInfo>());

        // Act
        _ = original.Prepend("SELECT * FROM Users ");

        // Assert
        original.Sql.Should().Be(originalSql);
    }

    [Fact]
    public void Prepend_ChainedWithAppend_BuildsComplexSql()
    {
        // Arrange
        var whereClause = new ParameterizedSql("Id = @p0", new List<SqlParameterInfo> { new("@p0", 1, null) });

        // Act
        var result = whereClause
            .Prepend("SELECT * FROM Users WHERE ")
            .Append(" AND Active = 1")
            .Append(" ORDER BY Name");

        // Assert
        result.Sql.Should().Be("SELECT * FROM Users WHERE Id = @p0 AND Active = 1 ORDER BY Name");
        result.Parameters.Should().HaveCount(1);
    }

    #endregion

    #region Immutability Tests

    [Fact]
    public void ParameterizedSql_IsImmutable_AllOperationsReturnNewInstances()
    {
        // Arrange
        var original = new ParameterizedSql("SELECT", new List<SqlParameterInfo> { new("@p0", 1, null) });
        var originalSql = original.Sql;
        var originalParamCount = original.Parameters.Count;

        // Act
        var appended = original.Append(" * FROM Users");
        var appendedSql = original.Append(new ParameterizedSql(" WHERE", Array.Empty<SqlParameterInfo>()));
        var prepended = original.Prepend("-- Comment\n");

        // Assert
        original.Sql.Should().Be(originalSql);
        original.Parameters.Should().HaveCount(originalParamCount);
        appended.Should().NotBeSameAs(original);
        appendedSql.Should().NotBeSameAs(original);
        prepended.Should().NotBeSameAs(original);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void AppendParameterizedSql_WithNull_ThrowsArgumentNullException()
    {
        // Arrange
        var sut = new ParameterizedSql("SELECT", Array.Empty<SqlParameterInfo>());

        // Act
        var action = () => sut.Append((ParameterizedSql)null!);

        // Assert
        action.Should().Throw<ArgumentNullException>()
            .WithParameterName("other");
    }

    [Fact]
    public void AppendParameterizedSql_WithManyParameters_CombinesAll()
    {
        // Arrange
        var params1 = Enumerable.Range(0, 50).Select(i => new SqlParameterInfo($"@a{i}", i, null)).ToList();
        var params2 = Enumerable.Range(0, 50).Select(i => new SqlParameterInfo($"@b{i}", i + 100, null)).ToList();
        var first = new ParameterizedSql("SQL1", params1);
        var second = new ParameterizedSql("SQL2", params2);

        // Act
        var result = first.Append(second);

        // Assert
        result.Parameters.Should().HaveCount(100);
        result.Parameters.Take(50).Should().BeEquivalentTo(params1);
        result.Parameters.Skip(50).Should().BeEquivalentTo(params2);
    }

    [Fact]
    public void Constructor_WithNullParameters_ThrowsArgumentNullException()
    {
        // Arrange & Act
        var action = () => new ParameterizedSql("SELECT", null!);

        // Assert
        action.Should().Throw<ArgumentNullException>()
            .WithParameterName("parameters");
    }

    [Fact]
    public void Append_WithSpecialCharacters_PreservesSql()
    {
        // Arrange
        var original = new ParameterizedSql("SELECT * FROM Users WHERE Name LIKE '%", Array.Empty<SqlParameterInfo>());

        // Act
        var result = original.Append("test%'");

        // Assert
        result.Sql.Should().Be("SELECT * FROM Users WHERE Name LIKE '%test%'");
    }

    [Fact]
    public void ParameterizedSql_WithNullParameterValue_Preserved()
    {
        // Arrange
        var parameters = new List<SqlParameterInfo> { new("@p0", null, "nvarchar") };

        // Act
        var sut = new ParameterizedSql("SELECT * FROM Users WHERE Name = @p0", parameters);

        // Assert
        sut.Parameters[0].Value.Should().BeNull();
        sut.Parameters[0].DbType.Should().Be("nvarchar");
    }

    #endregion
}
