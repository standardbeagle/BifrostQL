using FluentAssertions;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Tests for PivotQueryConfig validation and PivotSqlGenerator SQL generation.
/// </summary>
public sealed class PivotQueryTests
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    #region PivotQueryConfig Validation

    [Fact]
    public void Create_WithValidInput_ReturnsConfig()
    {
        var config = PivotQueryConfig.Create(
            "Status", "Id", "COUNT",
            new[] { "Region" });

        config.PivotColumn.Should().Be("Status");
        config.ValueColumn.Should().Be("Id");
        config.AggregateFunction.Should().Be(PivotAggregateFunction.Count);
        config.GroupByColumns.Should().ContainSingle().Which.Should().Be("Region");
        config.NullLabel.Should().Be("_null_");
    }

    [Fact]
    public void Create_WithCustomNullLabel_UsesCustomLabel()
    {
        var config = PivotQueryConfig.Create(
            "Status", "Amount", "SUM",
            new[] { "Region" },
            nullLabel: "N/A");

        config.NullLabel.Should().Be("N/A");
    }

    [Fact]
    public void Create_CaseInsensitiveAggregateFunction_Parses()
    {
        var config = PivotQueryConfig.Create("Status", "Amount", "sum", new[] { "Region" });
        config.AggregateFunction.Should().Be(PivotAggregateFunction.Sum);

        config = PivotQueryConfig.Create("Status", "Amount", "AVG", new[] { "Region" });
        config.AggregateFunction.Should().Be(PivotAggregateFunction.Avg);

        config = PivotQueryConfig.Create("Status", "Amount", "Min", new[] { "Region" });
        config.AggregateFunction.Should().Be(PivotAggregateFunction.Min);

        config = PivotQueryConfig.Create("Status", "Amount", "MAX", new[] { "Region" });
        config.AggregateFunction.Should().Be(PivotAggregateFunction.Max);
    }

    [Fact]
    public void Create_WithMultipleGroupByColumns_ReturnsAll()
    {
        var config = PivotQueryConfig.Create(
            "Status", "Amount", "SUM",
            new[] { "Region", "Year" });

        config.GroupByColumns.Should().HaveCount(2);
        config.GroupByColumns.Should().Contain("Region");
        config.GroupByColumns.Should().Contain("Year");
    }

    [Fact]
    public void Create_EmptyPivotColumn_Throws()
    {
        Action act = () => PivotQueryConfig.Create("", "Amount", "SUM", new[] { "Region" });
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Pivot column*required*");
    }

    [Fact]
    public void Create_EmptyValueColumn_Throws()
    {
        Action act = () => PivotQueryConfig.Create("Status", "", "SUM", new[] { "Region" });
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Value column*required*");
    }

    [Fact]
    public void Create_EmptyAggregateFunction_Throws()
    {
        Action act = () => PivotQueryConfig.Create("Status", "Amount", "", new[] { "Region" });
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Aggregate function*required*");
    }

    [Fact]
    public void Create_InvalidAggregateFunction_Throws()
    {
        Action act = () => PivotQueryConfig.Create("Status", "Amount", "MEDIAN", new[] { "Region" });
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Invalid aggregate function 'MEDIAN'*");
    }

    [Fact]
    public void Create_EmptyGroupByColumns_Throws()
    {
        Action act = () => PivotQueryConfig.Create("Status", "Amount", "SUM", Array.Empty<string>());
        act.Should().Throw<ArgumentException>()
            .WithMessage("*At least one group-by column*");
    }

    [Fact]
    public void Create_NullGroupByColumns_Throws()
    {
        Action act = () => PivotQueryConfig.Create("Status", "Amount", "SUM", null!);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*At least one group-by column*");
    }

    [Fact]
    public void Create_EmptyGroupByColumnName_Throws()
    {
        Action act = () => PivotQueryConfig.Create("Status", "Amount", "SUM", new[] { "Region", "" });
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Group-by column names must not be empty*");
    }

    [Fact]
    public void Create_PivotColumnInGroupBy_Throws()
    {
        Action act = () => PivotQueryConfig.Create("Status", "Amount", "SUM", new[] { "Status", "Region" });
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Pivot column 'Status' must not appear in group-by*");
    }

    [Fact]
    public void Create_PivotColumnInGroupByCaseInsensitive_Throws()
    {
        Action act = () => PivotQueryConfig.Create("Status", "Amount", "SUM", new[] { "status", "Region" });
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Pivot column 'Status' must not appear in group-by*");
    }

    [Fact]
    public void ValidateColumns_AllColumnsExist_DoesNotThrow()
    {
        var dbModel = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithColumn("Amount", "decimal")
                .WithColumn("Region", "nvarchar"))
            .Build();

        var table = dbModel.GetTableFromDbName("Orders");
        var config = PivotQueryConfig.Create("Status", "Amount", "SUM", new[] { "Region" });

        Action act = () => config.ValidateColumns(table.ColumnLookup);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateColumns_MissingPivotColumn_Throws()
    {
        var dbModel = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Amount", "decimal")
                .WithColumn("Region", "nvarchar"))
            .Build();

        var table = dbModel.GetTableFromDbName("Orders");
        var config = PivotQueryConfig.Create("Status", "Amount", "SUM", new[] { "Region" });

        Action act = () => config.ValidateColumns(table.ColumnLookup);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Pivot column 'Status' does not exist*");
    }

    [Fact]
    public void ValidateColumns_MissingValueColumn_Throws()
    {
        var dbModel = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithColumn("Region", "nvarchar"))
            .Build();

        var table = dbModel.GetTableFromDbName("Orders");
        var config = PivotQueryConfig.Create("Status", "Amount", "SUM", new[] { "Region" });

        Action act = () => config.ValidateColumns(table.ColumnLookup);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Value column 'Amount' does not exist*");
    }

    [Fact]
    public void ValidateColumns_MissingGroupByColumn_Throws()
    {
        var dbModel = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithColumn("Amount", "decimal"))
            .Build();

        var table = dbModel.GetTableFromDbName("Orders");
        var config = PivotQueryConfig.Create("Status", "Amount", "SUM", new[] { "Region" });

        Action act = () => config.ValidateColumns(table.ColumnLookup);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Group-by column 'Region' does not exist*");
    }

    #endregion

    #region CASE WHEN Pivot Generation

    [Fact]
    public void GeneratePivot_BasicPivot_GeneratesCorrectSql()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?> { "Active", "Inactive" };

        var result = PivotSqlGenerator.GeneratePivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("SELECT [Region]");
        result.Sql.Should().Contain("COUNT(CASE WHEN [Status] = @p0 THEN [Id] END) AS [__pivot_0],");
        result.Sql.Should().Contain("COUNT(CASE WHEN [Status] = @p1 THEN [Id] END) AS [__pivot_1] FROM");
        // A label is client data of any length (PostgreSQL truncates identifiers at 63
        // bytes), so it never becomes the result-set alias.
        result.Sql.Should().NotContain("AS [Active]").And.NotContain("AS [Inactive]");
        result.Sql.Should().Contain("GROUP BY [Region]");
        result.Parameters.Should().HaveCount(2);
    }

    [Fact]
    public void GeneratePivot_WithNullValue_UsesIsNull()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?> { "Active", null };

        var result = PivotSqlGenerator.GeneratePivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("COUNT(CASE WHEN [Status] = @p0 THEN [Id] END) AS [__pivot_0],");
        result.Sql.Should().Contain("COUNT(CASE WHEN [Status] IS NULL THEN [Id] END) AS [__pivot_1] FROM");
        // Only one parameter for "Active", the NULL check uses IS NULL
        result.Parameters.Should().ContainSingle(p => p.Name == "@p0");
    }

    [Fact]
    public void GeneratePivot_WithSumAggregate_UsesSum()
    {
        var config = PivotQueryConfig.Create("Status", "Amount", "SUM", new[] { "Region" });
        var pivotValues = new List<object?> { "Open" };

        var result = PivotSqlGenerator.GeneratePivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("SUM(CASE WHEN [Status] = @p0 THEN [Amount] END) AS [__pivot_0] FROM");
    }

    [Fact]
    public void GeneratePivot_WithFilter_IncludesFilter()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?> { "Active" };
        var filter = new ParameterizedSql(" WHERE [Year] = @p0",
            new List<SqlParameterInfo> { new("@p0", 2024) });

        var result = PivotSqlGenerator.GeneratePivot(
            Dialect, config, "[Orders]", pivotValues, filter);

        result.Sql.Should().Contain("WHERE [Year] = @p0");
        result.Parameters.Should().Contain(p => p.Name == "@p0" && (int)p.Value! == 2024);
    }

    [Fact]
    public void GeneratePivot_WithMultipleGroupBy_IncludesAll()
    {
        var config = PivotQueryConfig.Create("Status", "Amount", "SUM", new[] { "Region", "Year" });
        var pivotValues = new List<object?> { "Open" };

        var result = PivotSqlGenerator.GeneratePivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("SELECT [Region], [Year]");
        result.Sql.Should().Contain("GROUP BY [Region], [Year]");
    }

    [Fact]
    public void GeneratePivot_EmptyPivotValues_ReturnsGroupByOnly()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?>();

        var result = PivotSqlGenerator.GeneratePivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("SELECT [Region] FROM [Orders]");
        result.Sql.Should().Contain("GROUP BY [Region]");
        result.Sql.Should().NotContain("CASE WHEN");
    }

    [Fact]
    public void GeneratePivot_ParametersAreCorrectlyIndexed()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?> { "A", "B", "C" };

        var result = PivotSqlGenerator.GeneratePivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Parameters.Should().HaveCount(3);
        result.Parameters[0].Value.Should().Be("A");
        result.Parameters[1].Value.Should().Be("B");
        result.Parameters[2].Value.Should().Be("C");
    }

    #endregion

    #region Distinct Values SQL Generation

    [Fact]
    public void GenerateDistinctValuesSql_BasicQuery_GeneratesCorrectSql()
    {
        var result = PivotSqlGenerator.GenerateDistinctValuesSql(
            Dialect, "Status", "[Orders]");

        result.Sql.Should().Be("SELECT DISTINCT [Status] FROM [Orders] ORDER BY [Status]");
        result.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void GenerateDistinctValuesSql_WithFilter_IncludesFilter()
    {
        var filter = new ParameterizedSql(" WHERE [Year] = @p0",
            new List<SqlParameterInfo> { new("@p0", 2024) });

        var result = PivotSqlGenerator.GenerateDistinctValuesSql(
            Dialect, "Status", "[Orders]", filter);

        result.Sql.Should().Contain("WHERE [Year] = @p0");
        result.Sql.Should().Contain("ORDER BY [Status]");
        result.Parameters.Should().ContainSingle();
    }

    [Fact]
    public void GenerateDistinctValuesSql_WithSchemaTable_UsesFullRef()
    {
        var result = PivotSqlGenerator.GenerateDistinctValuesSql(
            Dialect, "Status", "[dbo].[Orders]");

        result.Sql.Should().Contain("FROM [dbo].[Orders]");
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void PivotWorkflow_ConfigThenValidateThenGenerate_FullPipeline()
    {
        // Arrange
        var dbModel = DbModelTestFixture.Create()
            .WithTable("Sales", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Region", "nvarchar")
                .WithColumn("Quarter", "nvarchar")
                .WithColumn("Amount", "decimal"))
            .Build();

        var table = dbModel.GetTableFromDbName("Sales");

        // Act
        var config = PivotQueryConfig.Create("Quarter", "Amount", "SUM", new[] { "Region" });
        config.ValidateColumns(table.ColumnLookup);

        var tableRef = Dialect.TableReference(table.TableSchema, table.DbName);
        var pivotValues = new List<object?> { "Q1", "Q2" };

        var result = PivotSqlGenerator.GeneratePivot(
            Dialect, config, tableRef, pivotValues);

        // Assert
        result.Sql.Should().Contain("SELECT [Region]");
        result.Sql.Should().Contain("SUM(CASE WHEN [Quarter] = @p0 THEN [Amount] END) AS [__pivot_0],");
        result.Sql.Should().Contain("SUM(CASE WHEN [Quarter] = @p1 THEN [Amount] END) AS [__pivot_1] FROM");
        result.Sql.Should().Contain("GROUP BY [Region]");
        result.Parameters.Should().HaveCount(2);
    }

    [Fact]
    public void PivotWorkflow_WithFilterAndNulls_HandlesEdgeCases()
    {
        var config = PivotQueryConfig.Create("Status", "Total", "AVG", new[] { "Category" });
        var pivotValues = new List<object?> { "Active", null, "Archived" };
        var filter = new ParameterizedSql(" WHERE [Year] >= @p0",
            new List<SqlParameterInfo> { new("@p0", 2020) });

        var caseResult = PivotSqlGenerator.GeneratePivot(
            Dialect, config, "[dbo].[Orders]", pivotValues, filter);

        caseResult.Sql.Should().Contain("AVG(CASE WHEN [Status] IS NULL THEN [Total] END) AS [__pivot_1],");
        caseResult.Sql.Should().Contain("WHERE [Year] >= @p0");
        caseResult.Sql.Should().Contain("GROUP BY [Category]");
    }

    #endregion

    #region All Aggregate Functions

    [Theory]
    [InlineData("COUNT", "COUNT")]
    [InlineData("SUM", "SUM")]
    [InlineData("AVG", "AVG")]
    [InlineData("MIN", "MIN")]
    [InlineData("MAX", "MAX")]
    public void GeneratePivot_AllAggregateFunctions_GenerateCorrectCase(string funcInput, string expectedSql)
    {
        var config = PivotQueryConfig.Create("Status", "Amount", funcInput, new[] { "Region" });
        var pivotValues = new List<object?> { "X" };

        var result = PivotSqlGenerator.GeneratePivot(
            Dialect, config, "[T]", pivotValues);

        result.Sql.Should().Contain($"{expectedSql}(CASE WHEN [Status] = @p0 THEN [Amount] END)");
    }

    #endregion

    #region Every Dialect Emits CASE WHEN

    [Fact]
    public void GeneratePivot_OnSqlServer_RoutesToCaseWhen()
    {
        var config = PivotQueryConfig.Create("Status", "Amount", "sum", new[] { "Region" });
        var pivotValues = new List<object?> { "Open", "Closed" };

        var result = PivotSqlGenerator.GeneratePivot(
            BifrostQL.SqlServer.SqlServerDialect.Instance, config, "[dbo].[T]", pivotValues);

        result.Sql.Should().Contain("CASE WHEN");
        result.Sql.Should().NotContain("PIVOT (");
        result.Sql.Should().Contain("[Status]");
    }

    [Fact]
    public void GeneratePivot_OnSqlite_RoutesToCaseWhen()
    {
        var config = PivotQueryConfig.Create("Status", "Amount", "sum", new[] { "Region" });
        var pivotValues = new List<object?> { "Open", "Closed" };

        var result = PivotSqlGenerator.GeneratePivot(
            BifrostQL.Sqlite.SqliteDialect.Instance, config, "\"T\"", pivotValues);

        result.Sql.Should().Contain("CASE WHEN");
        result.Sql.Should().NotContain("PIVOT (");
        // Sqlite uses double-quoted identifiers, not brackets.
        result.Sql.Should().Contain("\"Status\"");
    }

    [Fact]
    public void GeneratePivot_OnPostgres_RoutesToCaseWhen()
    {
        var config = PivotQueryConfig.Create("Status", "Amount", "sum", new[] { "Region" });
        var pivotValues = new List<object?> { "Open", "Closed" };

        var result = PivotSqlGenerator.GeneratePivot(
            BifrostQL.Ngsql.PostgresDialect.Instance, config, "\"public\".\"T\"", pivotValues);

        result.Sql.Should().Contain("CASE WHEN");
        result.Sql.Should().NotContain("PIVOT (");
        result.Sql.Should().Contain("\"Status\"");
    }

    [Fact]
    public void GeneratePivot_OnMySql_RoutesToCaseWhen()
    {
        var config = PivotQueryConfig.Create("Status", "Amount", "sum", new[] { "Region" });
        var pivotValues = new List<object?> { "Open", "Closed" };

        var result = PivotSqlGenerator.GeneratePivot(
            BifrostQL.MySql.MySqlDialect.Instance, config, "`T`", pivotValues);

        result.Sql.Should().Contain("CASE WHEN");
        result.Sql.Should().NotContain("PIVOT (");
        result.Sql.Should().Contain("`Status`");
    }

    #endregion
}
