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

    #region SQL Server PIVOT Generation

    [Fact]
    public void GenerateSqlServerPivot_BasicPivot_GeneratesCorrectSql()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?> { "Active", "Inactive", "Pending" };

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("SELECT [Region], [Active], [Inactive], [Pending]");
        result.Sql.Should().Contain("PIVOT (COUNT([Id])");
        result.Sql.Should().Contain("FOR [__pivot_col] IN ([Active], [Inactive], [Pending])");
        result.Sql.Should().Contain("FROM [Orders]");
    }

    [Fact]
    public void GenerateSqlServerPivot_WithMultipleGroupBy_IncludesAllGroupColumns()
    {
        var config = PivotQueryConfig.Create("Status", "Amount", "SUM", new[] { "Region", "Year" });
        var pivotValues = new List<object?> { "Open", "Closed" };

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[Sales]", pivotValues);

        result.Sql.Should().Contain("SELECT [Region], [Year], [Open], [Closed]");
        result.Sql.Should().Contain("PIVOT (SUM([Amount])");
    }

    [Fact]
    public void GenerateSqlServerPivot_WithNullPivotValue_UsesNullLabel()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?> { "Active", null, "Pending" };

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("[_null_]");
        result.Sql.Should().Contain("ISNULL(CAST([Status] AS NVARCHAR(MAX)), '_null_')");
    }

    [Fact]
    public void GenerateSqlServerPivot_WithCustomNullLabel_UsesCustomLabel()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" }, nullLabel: "Unknown");
        var pivotValues = new List<object?> { "Active", null };

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("[Unknown]");
        result.Sql.Should().Contain("ISNULL(CAST([Status] AS NVARCHAR(MAX)), 'Unknown')");
    }

    [Fact]
    public void GenerateSqlServerPivot_WithFilter_IncludesFilterInSubquery()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?> { "Active", "Inactive" };
        var filter = new ParameterizedSql(" WHERE [Year] = @p0",
            new List<SqlParameterInfo> { new("@p0", 2024) });

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[Orders]", pivotValues, filter);

        result.Sql.Should().Contain("WHERE [Year] = @p0");
        result.Parameters.Should().ContainSingle(p => p.Name == "@p0" && (int)p.Value! == 2024);
    }

    [Fact]
    public void GenerateSqlServerPivot_WithSumAggregate_UsesSum()
    {
        var config = PivotQueryConfig.Create("Status", "Amount", "SUM", new[] { "Region" });
        var pivotValues = new List<object?> { "Open", "Closed" };

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("PIVOT (SUM([Amount])");
    }

    [Fact]
    public void GenerateSqlServerPivot_WithAvgAggregate_UsesAvg()
    {
        var config = PivotQueryConfig.Create("Status", "Amount", "AVG", new[] { "Region" });
        var pivotValues = new List<object?> { "Open", "Closed" };

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("PIVOT (AVG([Amount])");
    }

    [Fact]
    public void GenerateSqlServerPivot_WithMinAggregate_UsesMin()
    {
        var config = PivotQueryConfig.Create("Status", "Price", "MIN", new[] { "Category" });
        var pivotValues = new List<object?> { "Small", "Large" };

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[Products]", pivotValues);

        result.Sql.Should().Contain("PIVOT (MIN([Price])");
    }

    [Fact]
    public void GenerateSqlServerPivot_WithMaxAggregate_UsesMax()
    {
        var config = PivotQueryConfig.Create("Status", "Price", "MAX", new[] { "Category" });
        var pivotValues = new List<object?> { "Small", "Large" };

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[Products]", pivotValues);

        result.Sql.Should().Contain("PIVOT (MAX([Price])");
    }

    [Fact]
    public void GenerateSqlServerPivot_EmptyPivotValues_ReturnsGroupByOnly()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?>();

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("SELECT [Region] FROM [Orders]");
        result.Sql.Should().Contain("GROUP BY [Region]");
        result.Sql.Should().NotContain("PIVOT");
    }

    [Fact]
    public void GenerateSqlServerPivot_WithSchemaTable_IncludesFullRef()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?> { "Active" };

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[dbo].[Orders]", pivotValues);

        result.Sql.Should().Contain("FROM [dbo].[Orders]");
    }

    #endregion

    #region CASE WHEN Pivot Generation

    [Fact]
    public void GenerateCaseWhenPivot_BasicPivot_GeneratesCorrectSql()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?> { "Active", "Inactive" };

        var result = PivotSqlGenerator.GenerateCaseWhenPivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("SELECT [Region]");
        result.Sql.Should().Contain("COUNT(CASE WHEN [Status] = @p0 THEN [Id] END) AS [Active]");
        result.Sql.Should().Contain("COUNT(CASE WHEN [Status] = @p1 THEN [Id] END) AS [Inactive]");
        result.Sql.Should().Contain("GROUP BY [Region]");
        result.Parameters.Should().HaveCount(2);
    }

    [Fact]
    public void GenerateCaseWhenPivot_WithNullValue_UsesIsNull()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?> { "Active", null };

        var result = PivotSqlGenerator.GenerateCaseWhenPivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("COUNT(CASE WHEN [Status] IS NULL THEN [Id] END) AS [_null_]");
        result.Sql.Should().Contain("COUNT(CASE WHEN [Status] = @p0 THEN [Id] END) AS [Active]");
        // Only one parameter for "Active", the NULL check uses IS NULL
        result.Parameters.Should().ContainSingle(p => p.Name == "@p0");
    }

    [Fact]
    public void GenerateCaseWhenPivot_WithSumAggregate_UsesSum()
    {
        var config = PivotQueryConfig.Create("Status", "Amount", "SUM", new[] { "Region" });
        var pivotValues = new List<object?> { "Open" };

        var result = PivotSqlGenerator.GenerateCaseWhenPivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("SUM(CASE WHEN [Status] = @p0 THEN [Amount] END) AS [Open]");
    }

    [Fact]
    public void GenerateCaseWhenPivot_WithFilter_IncludesFilter()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?> { "Active" };
        var filter = new ParameterizedSql(" WHERE [Year] = @p0",
            new List<SqlParameterInfo> { new("@p0", 2024) });

        var result = PivotSqlGenerator.GenerateCaseWhenPivot(
            Dialect, config, "[Orders]", pivotValues, filter);

        result.Sql.Should().Contain("WHERE [Year] = @p0");
        result.Parameters.Should().Contain(p => p.Name == "@p0" && (int)p.Value! == 2024);
    }

    [Fact]
    public void GenerateCaseWhenPivot_WithMultipleGroupBy_IncludesAll()
    {
        var config = PivotQueryConfig.Create("Status", "Amount", "SUM", new[] { "Region", "Year" });
        var pivotValues = new List<object?> { "Open" };

        var result = PivotSqlGenerator.GenerateCaseWhenPivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("SELECT [Region], [Year]");
        result.Sql.Should().Contain("GROUP BY [Region], [Year]");
    }

    [Fact]
    public void GenerateCaseWhenPivot_EmptyPivotValues_ReturnsGroupByOnly()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?>();

        var result = PivotSqlGenerator.GenerateCaseWhenPivot(
            Dialect, config, "[Orders]", pivotValues);

        result.Sql.Should().Contain("SELECT [Region] FROM [Orders]");
        result.Sql.Should().Contain("GROUP BY [Region]");
        result.Sql.Should().NotContain("CASE WHEN");
    }

    [Fact]
    public void GenerateCaseWhenPivot_ParametersAreCorrectlyIndexed()
    {
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" });
        var pivotValues = new List<object?> { "A", "B", "C" };

        var result = PivotSqlGenerator.GenerateCaseWhenPivot(
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
        // Arrange: Create a table model
        var dbModel = DbModelTestFixture.Create()
            .WithTable("Sales", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Region", "nvarchar")
                .WithColumn("Quarter", "nvarchar")
                .WithColumn("Amount", "decimal"))
            .Build();

        var table = dbModel.GetTableFromDbName("Sales");

        // Act: Create config, validate, generate SQL
        var config = PivotQueryConfig.Create("Quarter", "Amount", "SUM", new[] { "Region" });
        config.ValidateColumns(table.ColumnLookup);

        var tableRef = Dialect.TableReference(table.TableSchema, table.DbName);
        var pivotValues = new List<object?> { "Q1", "Q2", "Q3", "Q4" };

        var pivotSql = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, tableRef, pivotValues);

        // Assert
        pivotSql.Sql.Should().Contain("SELECT [Region], [Q1], [Q2], [Q3], [Q4]");
        pivotSql.Sql.Should().Contain("PIVOT (SUM([Amount])");
        pivotSql.Sql.Should().Contain("FOR [__pivot_col] IN ([Q1], [Q2], [Q3], [Q4])");
    }

    [Fact]
    public void PivotWorkflow_CaseWhenFallback_FullPipeline()
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

        var result = PivotSqlGenerator.GenerateCaseWhenPivot(
            Dialect, config, tableRef, pivotValues);

        // Assert
        result.Sql.Should().Contain("SELECT [Region]");
        result.Sql.Should().Contain("SUM(CASE WHEN [Quarter] = @p0 THEN [Amount] END) AS [Q1]");
        result.Sql.Should().Contain("SUM(CASE WHEN [Quarter] = @p1 THEN [Amount] END) AS [Q2]");
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

        // SQL Server pivot
        var sqlServerResult = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[dbo].[Orders]", pivotValues, filter);

        sqlServerResult.Sql.Should().Contain("[_null_]");
        sqlServerResult.Sql.Should().Contain("WHERE [Year] >= @p0");
        sqlServerResult.Sql.Should().Contain("PIVOT (AVG([Total])");

        // CASE WHEN fallback
        var caseResult = PivotSqlGenerator.GenerateCaseWhenPivot(
            Dialect, config, "[dbo].[Orders]", pivotValues, filter);

        caseResult.Sql.Should().Contain("AVG(CASE WHEN [Status] IS NULL THEN [Total] END) AS [_null_]");
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
    public void GenerateSqlServerPivot_AllAggregateFunctions_GenerateCorrectPivot(string funcInput, string expectedSql)
    {
        var config = PivotQueryConfig.Create("Status", "Amount", funcInput, new[] { "Region" });
        var pivotValues = new List<object?> { "X" };

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[T]", pivotValues);

        result.Sql.Should().Contain($"PIVOT ({expectedSql}([Amount])");
    }

    [Theory]
    [InlineData("COUNT", "COUNT")]
    [InlineData("SUM", "SUM")]
    [InlineData("AVG", "AVG")]
    [InlineData("MIN", "MIN")]
    [InlineData("MAX", "MAX")]
    public void GenerateCaseWhenPivot_AllAggregateFunctions_GenerateCorrectCase(string funcInput, string expectedSql)
    {
        var config = PivotQueryConfig.Create("Status", "Amount", funcInput, new[] { "Region" });
        var pivotValues = new List<object?> { "X" };

        var result = PivotSqlGenerator.GenerateCaseWhenPivot(
            Dialect, config, "[T]", pivotValues);

        result.Sql.Should().Contain($"{expectedSql}(CASE WHEN [Status] = @p0 THEN [Amount] END)");
    }

    #endregion
}
