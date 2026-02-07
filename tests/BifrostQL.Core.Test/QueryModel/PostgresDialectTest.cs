using BifrostQL.Core.QueryModel;
using BifrostQL.Ngsql;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

public sealed class PostgresDialectTest
{
    private readonly PostgresDialect _sut = PostgresDialect.Instance;

    #region Instance Tests

    [Fact]
    public void Instance_ReturnsSingletonInstance()
    {
        // Act
        var first = PostgresDialect.Instance;
        var second = PostgresDialect.Instance;

        // Assert
        first.Should().BeSameAs(second);
    }

    [Fact]
    public void Instance_ImplementsISqlDialect()
    {
        // Assert
        _sut.Should().BeAssignableTo<ISqlDialect>();
    }

    #endregion

    #region EscapeIdentifier Tests

    [Fact]
    public void EscapeIdentifier_WrapsInDoubleQuotes()
    {
        // Arrange
        const string identifier = "TableName";

        // Act
        var result = _sut.EscapeIdentifier(identifier);

        // Assert
        result.Should().Be("\"TableName\"");
    }

    [Fact]
    public void EscapeIdentifier_WithColumnName_WrapsCorrectly()
    {
        // Arrange
        const string identifier = "ColumnName";

        // Act
        var result = _sut.EscapeIdentifier(identifier);

        // Assert
        result.Should().Be("\"ColumnName\"");
    }

    [Fact]
    public void EscapeIdentifier_WithSpaces_PreservesSpaces()
    {
        // Arrange
        const string identifier = "Table Name";

        // Act
        var result = _sut.EscapeIdentifier(identifier);

        // Assert
        result.Should().Be("\"Table Name\"");
    }

    [Fact]
    public void EscapeIdentifier_WithSpecialCharacters_PreservesCharacters()
    {
        // Arrange
        const string identifier = "Table-Name_123";

        // Act
        var result = _sut.EscapeIdentifier(identifier);

        // Assert
        result.Should().Be("\"Table-Name_123\"");
    }

    [Fact]
    public void EscapeIdentifier_WithReservedWord_WrapsCorrectly()
    {
        // Arrange
        const string identifier = "SELECT";

        // Act
        var result = _sut.EscapeIdentifier(identifier);

        // Assert
        result.Should().Be("\"SELECT\"");
    }

    [Fact]
    public void EscapeIdentifier_WithEmptyString_ReturnsEmptyQuotes()
    {
        // Arrange
        const string identifier = "";

        // Act
        var result = _sut.EscapeIdentifier(identifier);

        // Assert
        result.Should().Be("\"\"");
    }

    #endregion

    #region TableReference Tests

    [Fact]
    public void TableReference_WithSchemaAndTable_ReturnsSchemaQualifiedName()
    {
        // Arrange
        const string schema = "public";
        const string table = "Users";

        // Act
        var result = _sut.TableReference(schema, table);

        // Assert
        result.Should().Be("\"public\".\"Users\"");
    }

    [Fact]
    public void TableReference_WithNullSchema_ReturnsTableOnly()
    {
        // Arrange
        const string table = "Users";

        // Act
        var result = _sut.TableReference(null, table);

        // Assert
        result.Should().Be("\"Users\"");
    }

    [Fact]
    public void TableReference_WithEmptySchema_ReturnsTableOnly()
    {
        // Arrange
        const string table = "Users";

        // Act
        var result = _sut.TableReference("", table);

        // Assert
        result.Should().Be("\"Users\"");
    }

    [Fact]
    public void TableReference_WithWhitespaceSchema_ReturnsTableOnly()
    {
        // Arrange
        const string table = "Users";

        // Act
        var result = _sut.TableReference("   ", table);

        // Assert
        result.Should().Be("\"Users\"");
    }

    [Fact]
    public void TableReference_WithCustomSchema_ReturnsCorrectFormat()
    {
        // Arrange
        const string schema = "sales";
        const string table = "Orders";

        // Act
        var result = _sut.TableReference(schema, table);

        // Assert
        result.Should().Be("\"sales\".\"Orders\"");
    }

    #endregion

    #region Pagination Tests

    [Fact]
    public void Pagination_WithNoSortColumns_OmitsOrderBy()
    {
        // Arrange & Act
        var result = _sut.Pagination(null, 0, 10);

        // Assert
        result.Should().NotContain("ORDER BY");
    }

    [Fact]
    public void Pagination_WithEmptySortColumns_OmitsOrderBy()
    {
        // Arrange & Act
        var result = _sut.Pagination(Array.Empty<string>(), 0, 10);

        // Assert
        result.Should().NotContain("ORDER BY");
    }

    [Fact]
    public void Pagination_WithSingleSortColumn_UsesColumn()
    {
        // Arrange
        var sortColumns = new[] { "\"Name\" ASC" };

        // Act
        var result = _sut.Pagination(sortColumns, 0, 10);

        // Assert
        result.Should().Contain("ORDER BY \"Name\" ASC");
    }

    [Fact]
    public void Pagination_WithMultipleSortColumns_JoinsWithComma()
    {
        // Arrange
        var sortColumns = new[] { "\"Name\" ASC", "\"Age\" DESC" };

        // Act
        var result = _sut.Pagination(sortColumns, 0, 10);

        // Assert
        result.Should().Contain("ORDER BY \"Name\" ASC, \"Age\" DESC");
    }

    [Fact]
    public void Pagination_WithZeroOffset_OmitsOffsetClause()
    {
        // Arrange & Act
        var result = _sut.Pagination(null, 0, 10);

        // Assert
        result.Should().NotContain("OFFSET");
    }

    [Fact]
    public void Pagination_WithNullOffset_OmitsOffsetClause()
    {
        // Arrange & Act
        var result = _sut.Pagination(null, null, 10);

        // Assert
        result.Should().NotContain("OFFSET");
    }

    [Fact]
    public void Pagination_WithOffset_UsesOffset()
    {
        // Arrange & Act
        var result = _sut.Pagination(null, 50, 10);

        // Assert
        result.Should().Contain("OFFSET 50");
    }

    [Fact]
    public void Pagination_WithLimit_UsesLimitClause()
    {
        // Arrange & Act
        var result = _sut.Pagination(null, 0, 25);

        // Assert
        result.Should().Contain("LIMIT 25");
    }

    [Fact]
    public void Pagination_WithNullLimit_DefaultsTo100()
    {
        // Arrange & Act
        var result = _sut.Pagination(null, 0, null);

        // Assert
        result.Should().Contain("LIMIT 100");
    }

    [Fact]
    public void Pagination_WithLimitMinusOne_NoLimitClause()
    {
        // Arrange & Act
        var result = _sut.Pagination(null, 0, -1);

        // Assert
        result.Should().NotContain("LIMIT");
    }

    [Fact]
    public void Pagination_FullExample_GeneratesCorrectSql()
    {
        // Arrange
        var sortColumns = new[] { "\"CreatedAt\" DESC", "\"Id\" ASC" };

        // Act
        var result = _sut.Pagination(sortColumns, 20, 10);

        // Assert
        result.Should().Be(" ORDER BY \"CreatedAt\" DESC, \"Id\" ASC LIMIT 10 OFFSET 20");
    }

    [Fact]
    public void Pagination_WithLargeOffset_HandlesCorrectly()
    {
        // Arrange & Act
        var result = _sut.Pagination(null, 1000000, 50);

        // Assert
        result.Should().Contain("OFFSET 1000000");
        result.Should().Contain("LIMIT 50");
    }

    [Fact]
    public void Pagination_LimitBeforeOffset_InPostgresConvention()
    {
        // PostgreSQL convention: LIMIT comes before OFFSET
        // Arrange
        var sortColumns = new[] { "\"Id\" ASC" };

        // Act
        var result = _sut.Pagination(sortColumns, 10, 5);

        // Assert
        var limitIndex = result.IndexOf("LIMIT", StringComparison.Ordinal);
        var offsetIndex = result.IndexOf("OFFSET", StringComparison.Ordinal);
        limitIndex.Should().BeLessThan(offsetIndex);
    }

    #endregion

    #region ParameterPrefix Tests

    [Fact]
    public void ParameterPrefix_ReturnsAtSymbol()
    {
        // Act
        var result = _sut.ParameterPrefix;

        // Assert
        result.Should().Be("@");
    }

    #endregion

    #region LastInsertedIdentity Tests

    [Fact]
    public void LastInsertedIdentity_ReturnsLastval()
    {
        // Act
        var result = _sut.LastInsertedIdentity;

        // Assert
        result.Should().Be("lastval()");
    }

    #endregion

    #region LikePattern Tests

    [Fact]
    public void LikePattern_Contains_UsesConcatenationOperator()
    {
        // Arrange
        const string paramName = "@p0";

        // Act
        var result = _sut.LikePattern(paramName, LikePatternType.Contains);

        // Assert
        result.Should().Be("'%' || @p0 || '%'");
    }

    [Fact]
    public void LikePattern_StartsWith_AppendWildcard()
    {
        // Arrange
        const string paramName = "@p0";

        // Act
        var result = _sut.LikePattern(paramName, LikePatternType.StartsWith);

        // Assert
        result.Should().Be("@p0 || '%'");
    }

    [Fact]
    public void LikePattern_EndsWith_PrependWildcard()
    {
        // Arrange
        const string paramName = "@p0";

        // Act
        var result = _sut.LikePattern(paramName, LikePatternType.EndsWith);

        // Assert
        result.Should().Be("'%' || @p0");
    }

    [Fact]
    public void LikePattern_WithDifferentParamNames_SubstitutesCorrectly()
    {
        // Arrange
        const string paramName = "@searchTerm";

        // Act
        var contains = _sut.LikePattern(paramName, LikePatternType.Contains);
        var startsWith = _sut.LikePattern(paramName, LikePatternType.StartsWith);
        var endsWith = _sut.LikePattern(paramName, LikePatternType.EndsWith);

        // Assert
        contains.Should().Be("'%' || @searchTerm || '%'");
        startsWith.Should().Be("@searchTerm || '%'");
        endsWith.Should().Be("'%' || @searchTerm");
    }

    [Theory]
    [InlineData(LikePatternType.Contains, "'%' || @p || '%'")]
    [InlineData(LikePatternType.StartsWith, "@p || '%'")]
    [InlineData(LikePatternType.EndsWith, "'%' || @p")]
    public void LikePattern_AllTypes_GenerateCorrectPatterns(LikePatternType patternType, string expected)
    {
        // Act
        var result = _sut.LikePattern("@p", patternType);

        // Assert
        result.Should().Be(expected);
    }

    #endregion

    #region GetOperator Tests

    [Theory]
    [InlineData("_eq", "=")]
    [InlineData("_neq", "!=")]
    [InlineData("_lt", "<")]
    [InlineData("_lte", "<=")]
    [InlineData("_gt", ">")]
    [InlineData("_gte", ">=")]
    public void GetOperator_ComparisonOperators_ReturnCorrectSql(string op, string expected)
    {
        // Act
        var result = _sut.GetOperator(op);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("_contains", "LIKE")]
    [InlineData("_starts_with", "LIKE")]
    [InlineData("_ends_with", "LIKE")]
    [InlineData("_like", "LIKE")]
    public void GetOperator_LikeOperators_ReturnLike(string op, string expected)
    {
        // Act
        var result = _sut.GetOperator(op);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("_ncontains", "NOT LIKE")]
    [InlineData("_nstarts_with", "NOT LIKE")]
    [InlineData("_nends_with", "NOT LIKE")]
    [InlineData("_nlike", "NOT LIKE")]
    public void GetOperator_NotLikeOperators_ReturnNotLike(string op, string expected)
    {
        // Act
        var result = _sut.GetOperator(op);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("_in", "IN")]
    [InlineData("_nin", "NOT IN")]
    public void GetOperator_InOperators_ReturnCorrectSql(string op, string expected)
    {
        // Act
        var result = _sut.GetOperator(op);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("_between", "BETWEEN")]
    [InlineData("_nbetween", "NOT BETWEEN")]
    public void GetOperator_BetweenOperators_ReturnCorrectSql(string op, string expected)
    {
        // Act
        var result = _sut.GetOperator(op);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void GetOperator_UnknownOperator_DefaultsToEquals()
    {
        // Arrange
        const string unknownOp = "_unknown";

        // Act
        var result = _sut.GetOperator(unknownOp);

        // Assert
        result.Should().Be("=");
    }

    [Fact]
    public void GetOperator_EmptyString_DefaultsToEquals()
    {
        // Act
        var result = _sut.GetOperator("");

        // Assert
        result.Should().Be("=");
    }

    [Fact]
    public void GetOperator_CaseSensitive_MustMatchExactly()
    {
        // Act
        var lower = _sut.GetOperator("_eq");
        var upper = _sut.GetOperator("_EQ");

        // Assert
        lower.Should().Be("=");
        upper.Should().Be("=", "unknown operators default to =");
    }

    #endregion

    #region Integration / Usage Pattern Tests

    [Fact]
    public void BuildingSelectQuery_WithDialect_GeneratesValidSql()
    {
        // Arrange
        var dialect = PostgresDialect.Instance;
        var tableName = dialect.TableReference("public", "Users");
        var columnId = dialect.EscapeIdentifier("Id");
        var columnName = dialect.EscapeIdentifier("Name");

        // Act
        var sql = $"SELECT {columnId}, {columnName} FROM {tableName}";

        // Assert
        sql.Should().Be("SELECT \"Id\", \"Name\" FROM \"public\".\"Users\"");
    }

    [Fact]
    public void BuildingWhereClause_WithOperators_GeneratesValidSql()
    {
        // Arrange
        var dialect = PostgresDialect.Instance;
        var eq = dialect.GetOperator("_eq");
        var gt = dialect.GetOperator("_gt");
        var like = dialect.GetOperator("_contains");

        // Act
        var whereClause = $"WHERE Status {eq} @p0 AND Age {gt} @p1 AND Name {like} {dialect.LikePattern("@p2", LikePatternType.Contains)}";

        // Assert
        whereClause.Should().Be("WHERE Status = @p0 AND Age > @p1 AND Name LIKE '%' || @p2 || '%'");
    }

    [Fact]
    public void BuildingPaginatedQuery_WithDialect_GeneratesValidSql()
    {
        // Arrange
        var dialect = PostgresDialect.Instance;
        var table = dialect.TableReference(null, "Products");
        var pagination = dialect.Pagination(new[] { "\"Price\" DESC" }, 10, 5);

        // Act
        var sql = $"SELECT * FROM {table}{pagination}";

        // Assert
        sql.Should().Be("SELECT * FROM \"Products\" ORDER BY \"Price\" DESC LIMIT 5 OFFSET 10");
    }

    [Fact]
    public void BuildingInsertQuery_WithIdentity_GeneratesValidSql()
    {
        // Arrange
        var dialect = PostgresDialect.Instance;
        var table = dialect.TableReference("public", "Users");
        var identity = dialect.LastInsertedIdentity;

        // Act
        var sql = $"INSERT INTO {table} (Name) VALUES (@p0); SELECT {identity};";

        // Assert
        sql.Should().Be("INSERT INTO \"public\".\"Users\" (Name) VALUES (@p0); SELECT lastval();");
    }

    #endregion

    #region ISqlDialect Interface Compliance Tests

    [Fact]
    public void AllInterfaceMethods_AreImplemented()
    {
        // Arrange
        ISqlDialect dialect = PostgresDialect.Instance;

        // Act & Assert - all methods should be callable
        dialect.EscapeIdentifier("test").Should().NotBeNull();
        dialect.TableReference("public", "test").Should().NotBeNull();
        dialect.Pagination(null, 0, 10).Should().NotBeNull();
        dialect.ParameterPrefix.Should().NotBeNull();
        dialect.LastInsertedIdentity.Should().NotBeNull();
        dialect.LikePattern("@p", LikePatternType.Contains).Should().NotBeNull();
        dialect.GetOperator("_eq").Should().NotBeNull();
    }

    #endregion

    #region PostgreSQL-Specific Behavior Tests

    [Fact]
    public void UsesDoubleQuotes_NotSquareBrackets()
    {
        // Verify PostgreSQL uses double-quote identifiers, not SQL Server square brackets
        var result = _sut.EscapeIdentifier("test");

        result.Should().StartWith("\"").And.EndWith("\"");
        result.Should().NotContain("[").And.NotContain("]");
    }

    [Fact]
    public void UsesLimitOffset_NotFetchNext()
    {
        // Verify PostgreSQL uses LIMIT/OFFSET, not OFFSET/FETCH NEXT
        var result = _sut.Pagination(new[] { "\"Id\" ASC" }, 10, 5);

        result.Should().Contain("LIMIT");
        result.Should().Contain("OFFSET");
        result.Should().NotContain("FETCH NEXT");
        result.Should().NotContain("ROWS ONLY");
    }

    [Fact]
    public void UsesConcatenation_NotPlusOperator_ForLikePatterns()
    {
        // Verify PostgreSQL uses || for string concatenation, not +
        var result = _sut.LikePattern("@p0", LikePatternType.Contains);

        result.Should().Contain("||");
        result.Should().NotContain(" + ");
    }

    [Fact]
    public void UsesLastval_NotScopeIdentity()
    {
        // Verify PostgreSQL uses lastval(), not SCOPE_IDENTITY()
        _sut.LastInsertedIdentity.Should().Be("lastval()");
        _sut.LastInsertedIdentity.Should().NotBe("SCOPE_IDENTITY()");
    }

    #endregion
}
