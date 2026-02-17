using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Cross-dialect parameterized tests that verify all ISqlDialect implementations
/// produce correct SQL for the same operations. These tests exercise the interface
/// contract uniformly across SQL Server, PostgreSQL, MySQL, and SQLite.
/// </summary>
public sealed class CrossDialectTest
{
    public static readonly ISqlDialect[] AllDialects =
    {
        SqlServerDialect.Instance,
        PostgresDialect.Instance,
        MySqlDialect.Instance,
        SqliteDialect.Instance,
    };

    public static IEnumerable<object[]> AllDialectData =>
        AllDialects.Select(d => new object[] { d });

    #region Identifier Escaping

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void EscapeIdentifier_SimpleIdentifier_WrapsWithDialectDelimiters(ISqlDialect dialect)
    {
        var result = dialect.EscapeIdentifier("Users");

        result.Should().Contain("Users");
        result.Length.Should().BeGreaterThan("Users".Length, "identifier must be wrapped");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void EscapeIdentifier_ReservedWord_WrapsCorrectly(ISqlDialect dialect)
    {
        var result = dialect.EscapeIdentifier("SELECT");

        result.Should().Contain("SELECT");
        result.Length.Should().BeGreaterThan("SELECT".Length);
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void EscapeIdentifier_WithSpaces_PreservesSpaces(ISqlDialect dialect)
    {
        var result = dialect.EscapeIdentifier("My Table");

        result.Should().Contain("My Table");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void EscapeIdentifier_EmptyString_ReturnsWrappedEmpty(ISqlDialect dialect)
    {
        var result = dialect.EscapeIdentifier("");

        result.Length.Should().BeGreaterThanOrEqualTo(2, "at least the delimiters");
    }

    public static IEnumerable<object[]> IdentifierEscapingData()
    {
        yield return new object[] { SqlServerDialect.Instance, "Users", "[Users]" };
        yield return new object[] { PostgresDialect.Instance, "Users", "\"Users\"" };
        yield return new object[] { MySqlDialect.Instance, "Users", "`Users`" };
        yield return new object[] { SqliteDialect.Instance, "Users", "\"Users\"" };
    }

    [Theory]
    [MemberData(nameof(IdentifierEscapingData))]
    public void EscapeIdentifier_VerifyExactFormat(ISqlDialect dialect, string identifier, string expected)
    {
        dialect.EscapeIdentifier(identifier).Should().Be(expected);
    }

    #endregion

    #region Table Reference

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void TableReference_WithSchema_ContainsBothParts(ISqlDialect dialect)
    {
        var result = dialect.TableReference("myschema", "Users");

        result.Should().Contain("myschema");
        result.Should().Contain("Users");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void TableReference_NullSchema_ContainsOnlyTable(ISqlDialect dialect)
    {
        var result = dialect.TableReference(null, "Users");

        result.Should().Contain("Users");
        result.Should().NotContain(".");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void TableReference_EmptySchema_ContainsOnlyTable(ISqlDialect dialect)
    {
        var result = dialect.TableReference("", "Users");

        result.Should().Contain("Users");
        result.Should().NotContain(".");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void TableReference_WhitespaceSchema_ContainsOnlyTable(ISqlDialect dialect)
    {
        var result = dialect.TableReference("   ", "Users");

        result.Should().Contain("Users");
        result.Should().NotContain(".");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void TableReference_WithSchema_ContainsSeparator(ISqlDialect dialect)
    {
        var result = dialect.TableReference("dbo", "Users");

        result.Should().Contain(".");
    }

    public static IEnumerable<object[]> TableReferenceData()
    {
        yield return new object[] { SqlServerDialect.Instance, "dbo", "Users", "[dbo].[Users]" };
        yield return new object[] { PostgresDialect.Instance, "public", "Users", "\"public\".\"Users\"" };
        yield return new object[] { MySqlDialect.Instance, "mydb", "Users", "`mydb`.`Users`" };
        yield return new object[] { SqliteDialect.Instance, "main", "Users", "\"main\".\"Users\"" };
    }

    [Theory]
    [MemberData(nameof(TableReferenceData))]
    public void TableReference_VerifyExactFormat(ISqlDialect dialect, string schema, string table, string expected)
    {
        dialect.TableReference(schema, table).Should().Be(expected);
    }

    public static IEnumerable<object[]> TableReferenceNoSchemaData()
    {
        yield return new object[] { SqlServerDialect.Instance, "Users", "[Users]" };
        yield return new object[] { PostgresDialect.Instance, "Users", "\"Users\"" };
        yield return new object[] { MySqlDialect.Instance, "Users", "`Users`" };
        yield return new object[] { SqliteDialect.Instance, "Users", "\"Users\"" };
    }

    [Theory]
    [MemberData(nameof(TableReferenceNoSchemaData))]
    public void TableReference_NoSchema_VerifyExactFormat(ISqlDialect dialect, string table, string expected)
    {
        dialect.TableReference(null, table).Should().Be(expected);
    }

    #endregion

    #region Pagination

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void Pagination_NullLimit_DefaultsTo100(ISqlDialect dialect)
    {
        var result = dialect.Pagination(null, 0, null);

        result.Should().Contain("100");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void Pagination_MinusOneLimit_NoRowLimit(ISqlDialect dialect)
    {
        var result = dialect.Pagination(null, 0, -1);

        result.Should().NotContain("FETCH NEXT");
        result.Should().NotContain("LIMIT");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void Pagination_ExplicitLimit_IncludesValue(ISqlDialect dialect)
    {
        var result = dialect.Pagination(null, 0, 25);

        result.Should().Contain("25");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void Pagination_WithOffset_IncludesOffsetValue(ISqlDialect dialect)
    {
        var result = dialect.Pagination(null, 50, 10);

        result.Should().Contain("50");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void Pagination_WithSortColumns_IncludesOrderBy(ISqlDialect dialect)
    {
        var result = dialect.Pagination(new[] { "col1 ASC" }, 0, 10);

        result.Should().Contain("ORDER BY");
        result.Should().Contain("col1 ASC");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void Pagination_WithMultipleSortColumns_JoinsWithComma(ISqlDialect dialect)
    {
        var result = dialect.Pagination(new[] { "col1 ASC", "col2 DESC" }, 0, 10);

        result.Should().Contain("col1 ASC, col2 DESC");
    }

    [Fact]
    public void Pagination_SqlServer_RequiresOrderBy_AlwaysPresent()
    {
        var result = SqlServerDialect.Instance.Pagination(null, 0, 10);

        result.Should().Contain("ORDER BY (SELECT NULL)");
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    [InlineData("SQLite")]
    public void Pagination_LimitOffsetDialects_OmitOrderByWhenNoSortColumns(string dialectName)
    {
        var dialect = dialectName switch
        {
            "PostgreSQL" => (ISqlDialect)PostgresDialect.Instance,
            "MySQL" => MySqlDialect.Instance,
            "SQLite" => SqliteDialect.Instance,
            _ => throw new ArgumentException(dialectName)
        };

        var result = dialect.Pagination(null, 0, 10);

        result.Should().NotContain("ORDER BY");
    }

    [Fact]
    public void Pagination_SqlServer_UsesOffsetFetch()
    {
        var result = SqlServerDialect.Instance.Pagination(null, 10, 5);

        result.Should().Contain("OFFSET 10 ROWS");
        result.Should().Contain("FETCH NEXT 5 ROWS ONLY");
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    [InlineData("SQLite")]
    public void Pagination_LimitOffsetDialects_UseLimitOffset(string dialectName)
    {
        var dialect = dialectName switch
        {
            "PostgreSQL" => (ISqlDialect)PostgresDialect.Instance,
            "MySQL" => MySqlDialect.Instance,
            "SQLite" => SqliteDialect.Instance,
            _ => throw new ArgumentException(dialectName)
        };

        var result = dialect.Pagination(new[] { "id ASC" }, 10, 5);

        result.Should().Contain("LIMIT 5");
        result.Should().Contain("OFFSET 10");
        result.Should().NotContain("FETCH NEXT");
        result.Should().NotContain("ROWS ONLY");
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    [InlineData("SQLite")]
    public void Pagination_LimitOffsetDialects_LimitBeforeOffset(string dialectName)
    {
        var dialect = dialectName switch
        {
            "PostgreSQL" => (ISqlDialect)PostgresDialect.Instance,
            "MySQL" => MySqlDialect.Instance,
            "SQLite" => SqliteDialect.Instance,
            _ => throw new ArgumentException(dialectName)
        };

        var result = dialect.Pagination(new[] { "id ASC" }, 10, 5);

        var limitIndex = result.IndexOf("LIMIT", StringComparison.Ordinal);
        var offsetIndex = result.IndexOf("OFFSET", StringComparison.Ordinal);
        limitIndex.Should().BeLessThan(offsetIndex);
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    [InlineData("SQLite")]
    public void Pagination_LimitOffsetDialects_ZeroOffset_OmitsOffsetClause(string dialectName)
    {
        var dialect = dialectName switch
        {
            "PostgreSQL" => (ISqlDialect)PostgresDialect.Instance,
            "MySQL" => MySqlDialect.Instance,
            "SQLite" => SqliteDialect.Instance,
            _ => throw new ArgumentException(dialectName)
        };

        var result = dialect.Pagination(null, 0, 10);

        result.Should().NotContain("OFFSET");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void Pagination_LargeOffset_HandlesCorrectly(ISqlDialect dialect)
    {
        var result = dialect.Pagination(null, 1000000, 50);

        result.Should().Contain("1000000");
        result.Should().Contain("50");
    }

    #endregion

    #region Parameter Prefix

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void ParameterPrefix_AllDialects_UseAtSymbol(ISqlDialect dialect)
    {
        dialect.ParameterPrefix.Should().Be("@");
    }

    #endregion

    #region Last Inserted Identity

    public static IEnumerable<object[]> IdentityData()
    {
        yield return new object[] { SqlServerDialect.Instance, "SCOPE_IDENTITY()" };
        yield return new object[] { PostgresDialect.Instance, "lastval()" };
        yield return new object[] { MySqlDialect.Instance, "LAST_INSERT_ID()" };
        yield return new object[] { SqliteDialect.Instance, "last_insert_rowid()" };
    }

    [Theory]
    [MemberData(nameof(IdentityData))]
    public void LastInsertedIdentity_ReturnsDialectSpecificExpression(ISqlDialect dialect, string expected)
    {
        dialect.LastInsertedIdentity.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void LastInsertedIdentity_IsNotEmpty(ISqlDialect dialect)
    {
        dialect.LastInsertedIdentity.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void LastInsertedIdentity_ContainsParentheses(ISqlDialect dialect)
    {
        dialect.LastInsertedIdentity.Should().Contain("(").And.Contain(")");
    }

    [Fact]
    public void LastInsertedIdentity_AllDialects_AreUnique()
    {
        var identities = AllDialects.Select(d => d.LastInsertedIdentity).ToList();

        identities.Distinct().Count().Should().Be(AllDialects.Length,
            "each dialect should have a unique identity expression");
    }

    #endregion

    #region LIKE Patterns

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void LikePattern_Contains_IncludesParamAndWildcards(ISqlDialect dialect)
    {
        var result = dialect.LikePattern("@p0", LikePatternType.Contains);

        result.Should().Contain("@p0");
        result.Should().Contain("%");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void LikePattern_StartsWith_IncludesParamWithTrailingWildcard(ISqlDialect dialect)
    {
        var result = dialect.LikePattern("@p0", LikePatternType.StartsWith);

        result.Should().Contain("@p0");
        result.Should().Contain("%");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void LikePattern_EndsWith_IncludesParamWithLeadingWildcard(ISqlDialect dialect)
    {
        var result = dialect.LikePattern("@p0", LikePatternType.EndsWith);

        result.Should().Contain("@p0");
        result.Should().Contain("%");
    }

    public static IEnumerable<object[]> LikeContainsData()
    {
        yield return new object[] { SqlServerDialect.Instance, "'%' + @p0 + '%'" };
        yield return new object[] { PostgresDialect.Instance, "'%' || @p0 || '%'" };
        yield return new object[] { MySqlDialect.Instance, "CONCAT('%', @p0, '%')" };
        yield return new object[] { SqliteDialect.Instance, "'%' || @p0 || '%'" };
    }

    [Theory]
    [MemberData(nameof(LikeContainsData))]
    public void LikePattern_Contains_VerifyExactFormat(ISqlDialect dialect, string expected)
    {
        dialect.LikePattern("@p0", LikePatternType.Contains).Should().Be(expected);
    }

    public static IEnumerable<object[]> LikeStartsWithData()
    {
        yield return new object[] { SqlServerDialect.Instance, "@p0 + '%'" };
        yield return new object[] { PostgresDialect.Instance, "@p0 || '%'" };
        yield return new object[] { MySqlDialect.Instance, "CONCAT(@p0, '%')" };
        yield return new object[] { SqliteDialect.Instance, "@p0 || '%'" };
    }

    [Theory]
    [MemberData(nameof(LikeStartsWithData))]
    public void LikePattern_StartsWith_VerifyExactFormat(ISqlDialect dialect, string expected)
    {
        dialect.LikePattern("@p0", LikePatternType.StartsWith).Should().Be(expected);
    }

    public static IEnumerable<object[]> LikeEndsWithData()
    {
        yield return new object[] { SqlServerDialect.Instance, "'%' + @p0" };
        yield return new object[] { PostgresDialect.Instance, "'%' || @p0" };
        yield return new object[] { MySqlDialect.Instance, "CONCAT('%', @p0)" };
        yield return new object[] { SqliteDialect.Instance, "'%' || @p0" };
    }

    [Theory]
    [MemberData(nameof(LikeEndsWithData))]
    public void LikePattern_EndsWith_VerifyExactFormat(ISqlDialect dialect, string expected)
    {
        dialect.LikePattern("@p0", LikePatternType.EndsWith).Should().Be(expected);
    }

    [Fact]
    public void LikePattern_SqlServer_UsesPlusForConcatenation()
    {
        var result = SqlServerDialect.Instance.LikePattern("@p0", LikePatternType.Contains);

        result.Should().Contain(" + ");
        result.Should().NotContain("||");
        result.Should().NotContain("CONCAT(");
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("SQLite")]
    public void LikePattern_PipeDialects_UseDoublePipeForConcatenation(string dialectName)
    {
        var dialect = dialectName switch
        {
            "PostgreSQL" => (ISqlDialect)PostgresDialect.Instance,
            "SQLite" => SqliteDialect.Instance,
            _ => throw new ArgumentException(dialectName)
        };

        var result = dialect.LikePattern("@p0", LikePatternType.Contains);

        result.Should().Contain("||");
        result.Should().NotContain(" + ");
        result.Should().NotContain("CONCAT(");
    }

    [Fact]
    public void LikePattern_MySql_UsesConcatFunction()
    {
        var result = MySqlDialect.Instance.LikePattern("@p0", LikePatternType.Contains);

        result.Should().Contain("CONCAT(");
        result.Should().NotContain("||");
        result.Should().NotContain(" + ");
    }

    #endregion

    #region Filter Operators

    private static readonly string[] LikeOperators = { "_contains", "_starts_with", "_ends_with", "_like" };
    private static readonly string[] NotLikeOperators = { "_ncontains", "_nstarts_with", "_nends_with", "_nlike" };

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void GetOperator_AllComparisonOperators_ConsistentAcrossDialects(ISqlDialect dialect)
    {
        dialect.GetOperator("_eq").Should().Be("=");
        dialect.GetOperator("_neq").Should().Be("!=");
        dialect.GetOperator("_lt").Should().Be("<");
        dialect.GetOperator("_lte").Should().Be("<=");
        dialect.GetOperator("_gt").Should().Be(">");
        dialect.GetOperator("_gte").Should().Be(">=");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void GetOperator_AllLikeOperators_ConsistentAcrossDialects(ISqlDialect dialect)
    {
        foreach (var op in LikeOperators)
            dialect.GetOperator(op).Should().Be("LIKE", $"operator {op}");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void GetOperator_AllNotLikeOperators_ConsistentAcrossDialects(ISqlDialect dialect)
    {
        foreach (var op in NotLikeOperators)
            dialect.GetOperator(op).Should().Be("NOT LIKE", $"operator {op}");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void GetOperator_SetOperators_ConsistentAcrossDialects(ISqlDialect dialect)
    {
        dialect.GetOperator("_in").Should().Be("IN");
        dialect.GetOperator("_nin").Should().Be("NOT IN");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void GetOperator_RangeOperators_ConsistentAcrossDialects(ISqlDialect dialect)
    {
        dialect.GetOperator("_between").Should().Be("BETWEEN");
        dialect.GetOperator("_nbetween").Should().Be("NOT BETWEEN");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void GetOperator_UnknownOperator_DefaultsToEquals(ISqlDialect dialect)
    {
        dialect.GetOperator("_unknown").Should().Be("=");
        dialect.GetOperator("").Should().Be("=");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void GetOperator_CaseSensitive_UppercaseDefaultsToEquals(ISqlDialect dialect)
    {
        dialect.GetOperator("_EQ").Should().Be("=", "unrecognized case defaults to =");
    }

    #endregion

    #region Composed SQL Generation

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void ComposedSelect_AllDialects_GenerateValidStructure(ISqlDialect dialect)
    {
        var table = dialect.TableReference(null, "Users");
        var col = dialect.EscapeIdentifier("Name");

        var sql = $"SELECT {col} FROM {table}";

        sql.Should().StartWith("SELECT ");
        sql.Should().Contain("FROM ");
        sql.Should().Contain("Name");
        sql.Should().Contain("Users");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void ComposedInsertWithIdentity_AllDialects_GenerateValidStructure(ISqlDialect dialect)
    {
        var table = dialect.TableReference(null, "Users");
        var identity = dialect.LastInsertedIdentity;

        var sql = $"INSERT INTO {table} (Name) VALUES (@p0); SELECT {identity};";

        sql.Should().StartWith("INSERT INTO ");
        sql.Should().Contain("VALUES (@p0)");
        sql.Should().Contain("SELECT ");
        sql.Should().Contain(identity);
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void ComposedWhereWithLike_AllDialects_GenerateValidStructure(ISqlDialect dialect)
    {
        var op = dialect.GetOperator("_contains");
        var pattern = dialect.LikePattern("@p0", LikePatternType.Contains);

        var where = $"Name {op} {pattern}";

        where.Should().Contain("Name");
        where.Should().Contain("LIKE");
        where.Should().Contain("@p0");
        where.Should().Contain("%");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void ComposedPaginatedQuery_AllDialects_IncludeLimitingClause(ISqlDialect dialect)
    {
        var table = dialect.TableReference(null, "Products");
        var pagination = dialect.Pagination(null, 0, 10);

        var sql = $"SELECT * FROM {table}{pagination}";

        sql.Should().Contain("Products");
        sql.Should().Contain("10");
    }

    #endregion

    #region Singleton Consistency

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void Singleton_MultipleAccesses_ReturnSameInstance(ISqlDialect dialect)
    {
        var instance1 = dialect switch
        {
            SqlServerDialect => (ISqlDialect)SqlServerDialect.Instance,
            PostgresDialect => PostgresDialect.Instance,
            MySqlDialect => MySqlDialect.Instance,
            SqliteDialect => SqliteDialect.Instance,
            _ => throw new InvalidOperationException()
        };

        var instance2 = dialect switch
        {
            SqlServerDialect => (ISqlDialect)SqlServerDialect.Instance,
            PostgresDialect => PostgresDialect.Instance,
            MySqlDialect => MySqlDialect.Instance,
            SqliteDialect => SqliteDialect.Instance,
            _ => throw new InvalidOperationException()
        };

        instance1.Should().BeSameAs(instance2);
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void AllDialects_ImplementISqlDialect(ISqlDialect dialect)
    {
        dialect.Should().BeAssignableTo<ISqlDialect>();
    }

    #endregion

    #region Cross-Dialect Differentiation

    [Fact]
    public void EscapeIdentifier_EachDialect_UsesDifferentOrSharedDelimiters()
    {
        var results = AllDialects.Select(d => d.EscapeIdentifier("Test")).ToList();

        // SQL Server uses [] while Postgres/SQLite use "" and MySQL uses ``
        results[0].Should().Be("[Test]");      // SQL Server
        results[1].Should().Be("\"Test\"");     // PostgreSQL
        results[2].Should().Be("`Test`");       // MySQL
        results[3].Should().Be("\"Test\"");     // SQLite (same as PostgreSQL)
    }

    [Fact]
    public void Pagination_SqlServerVsOthers_DifferentSyntax()
    {
        var sqlServer = SqlServerDialect.Instance.Pagination(new[] { "Id" }, 10, 5);
        var postgres = PostgresDialect.Instance.Pagination(new[] { "Id" }, 10, 5);
        var mysql = MySqlDialect.Instance.Pagination(new[] { "Id" }, 10, 5);
        var sqlite = SqliteDialect.Instance.Pagination(new[] { "Id" }, 10, 5);

        // SQL Server uses OFFSET/FETCH
        sqlServer.Should().Contain("FETCH NEXT");
        sqlServer.Should().Contain("ROWS ONLY");

        // Others use LIMIT/OFFSET
        postgres.Should().Contain("LIMIT").And.NotContain("FETCH NEXT");
        mysql.Should().Contain("LIMIT").And.NotContain("FETCH NEXT");
        sqlite.Should().Contain("LIMIT").And.NotContain("FETCH NEXT");
    }

    [Fact]
    public void LikePattern_ThreeConcatenationStyles()
    {
        var sqlServer = SqlServerDialect.Instance.LikePattern("@p", LikePatternType.Contains);
        var postgres = PostgresDialect.Instance.LikePattern("@p", LikePatternType.Contains);
        var mysql = MySqlDialect.Instance.LikePattern("@p", LikePatternType.Contains);
        var sqlite = SqliteDialect.Instance.LikePattern("@p", LikePatternType.Contains);

        // SQL Server: + concatenation
        sqlServer.Should().Contain(" + ");

        // PostgreSQL and SQLite: || concatenation
        postgres.Should().Contain("||");
        sqlite.Should().Contain("||");

        // MySQL: CONCAT() function
        mysql.Should().StartWith("CONCAT(");

        // PostgreSQL and SQLite should produce identical results
        postgres.Should().Be(sqlite);
    }

    #endregion
}
