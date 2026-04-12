using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.Modules.Eav;
using BifrostQL.Core.QueryModel;
using FluentAssertions;

namespace BifrostQL.Core.Test.Modules.Eav;

public class EavQueryTransformerTests
{
    [Fact]
    public void GenerateFlattenedQuerySql_BasicQuery_ContainsExpectedElements()
    {
        var dialect = new SqlServerDialect();
        var transformer = new EavQueryTransformer(dialect);

        var parentTable = CreateParentTable();
        var metaTable = CreateMetaTable();
        var config = new EavConfig("wp_postmeta", "wp_posts", "post_id", "meta_key", "meta_value");

        var flattenedTable = new EavFlattenedTable
        {
            ParentTable = parentTable,
            MetaTable = metaTable,
            Config = config,
            DynamicColumns = new List<EavColumn>(),
        };

        var columns = new List<EavColumn>
        {
            new() { MetaKey = "title", GraphQlName = "title", SqlAlias = "eav_title", DataType = "nvarchar" },
            new() { MetaKey = "views", GraphQlName = "views", SqlAlias = "eav_views", DataType = "nvarchar" },
        };

        var sql = transformer.GenerateFlattenedQuerySql(flattenedTable, columns);

        sql.Sql.Should().Contain("SELECT [ID], p.[eav_title], p.[eav_views]");
        sql.Sql.Should().Contain("FROM [dbo].[wp_posts] parent");
        sql.Sql.Should().Contain("LEFT JOIN");
        sql.Sql.Should().Contain("[post_id]");
        sql.Sql.Should().Contain("parent.[ID] = p.[post_id]");
    }

    [Fact]
    public void GenerateFlattenedQuerySql_WithColumns_ContainsPivotSubquery()
    {
        var dialect = new SqlServerDialect();
        var transformer = new EavQueryTransformer(dialect);

        var parentTable = CreateParentTable();
        var metaTable = CreateMetaTable();
        var config = new EavConfig("wp_postmeta", "wp_posts", "post_id", "meta_key", "meta_value");

        var flattenedTable = new EavFlattenedTable
        {
            ParentTable = parentTable,
            MetaTable = metaTable,
            Config = config,
            DynamicColumns = new List<EavColumn>(),
        };

        var columns = new List<EavColumn>
        {
            new() { MetaKey = "title", GraphQlName = "title", SqlAlias = "eav_title", DataType = "nvarchar" },
        };

        var sql = transformer.GenerateFlattenedQuerySql(flattenedTable, columns);

        // Should contain MAX(CASE WHEN ...) pattern
        sql.Sql.Should().Contain("MAX(CASE WHEN [meta_key] = @p0 THEN [meta_value] END)");
        sql.Sql.Should().Contain("AS [eav_title]");
        sql.Sql.Should().Contain("FROM [dbo].[wp_postmeta]");
        sql.Sql.Should().Contain("GROUP BY [post_id]");
    }

    [Fact]
    public void GenerateFlattenedQuerySql_WithPagination_ContainsOffsetFetch()
    {
        var dialect = new SqlServerDialect();
        var transformer = new EavQueryTransformer(dialect);

        var parentTable = CreateParentTable();
        var metaTable = CreateMetaTable();
        var config = new EavConfig("wp_postmeta", "wp_posts", "post_id", "meta_key", "meta_value");

        var flattenedTable = new EavFlattenedTable
        {
            ParentTable = parentTable,
            MetaTable = metaTable,
            Config = config,
            DynamicColumns = new List<EavColumn>(),
        };

        var columns = new List<EavColumn>();
        var sql = transformer.GenerateFlattenedQuerySql(flattenedTable, columns, limit: 10, offset: 20);

        sql.Sql.Should().Contain("OFFSET 20 ROWS");
        sql.Sql.Should().Contain("FETCH NEXT 10 ROWS ONLY");
    }

    [Fact]
    public void GenerateCountSql_BasicQuery_ReturnsCountStatement()
    {
        var dialect = new SqlServerDialect();
        var transformer = new EavQueryTransformer(dialect);

        var parentTable = CreateParentTable();
        var metaTable = CreateMetaTable();
        var config = new EavConfig("wp_postmeta", "wp_posts", "post_id", "meta_key", "meta_value");

        var flattenedTable = new EavFlattenedTable
        {
            ParentTable = parentTable,
            MetaTable = metaTable,
            Config = config,
            DynamicColumns = new List<EavColumn>(),
        };

        var sql = transformer.GenerateCountSql(flattenedTable);

        sql.Sql.Should().Be("SELECT COUNT(*) FROM [dbo].[wp_posts]");
    }

    [Fact]
    public void GenerateCountSql_WithoutFilter_ReturnsSimpleCount()
    {
        var dialect = new SqlServerDialect();
        var transformer = new EavQueryTransformer(dialect);

        var parentTable = CreateParentTable();
        var metaTable = CreateMetaTable();
        var config = new EavConfig("wp_postmeta", "wp_posts", "post_id", "meta_key", "meta_value");

        var flattenedTable = new EavFlattenedTable
        {
            ParentTable = parentTable,
            MetaTable = metaTable,
            Config = config,
            DynamicColumns = new List<EavColumn>(),
        };

        var sql = transformer.GenerateCountSql(flattenedTable);

        sql.Sql.Should().Be("SELECT COUNT(*) FROM [dbo].[wp_posts]");
    }

    [Fact]
    public void GenerateFlattenedQuerySql_MultipleColumns_CreatesMultipleCaseWhen()
    {
        var dialect = new SqlServerDialect();
        var transformer = new EavQueryTransformer(dialect);

        var parentTable = CreateParentTable();
        var metaTable = CreateMetaTable();
        var config = new EavConfig("wp_postmeta", "wp_posts", "post_id", "meta_key", "meta_value");

        var flattenedTable = new EavFlattenedTable
        {
            ParentTable = parentTable,
            MetaTable = metaTable,
            Config = config,
            DynamicColumns = new List<EavColumn>(),
        };

        var columns = new List<EavColumn>
        {
            new() { MetaKey = "title", GraphQlName = "title", SqlAlias = "eav_title", DataType = "nvarchar" },
            new() { MetaKey = "views", GraphQlName = "views", SqlAlias = "eav_views", DataType = "nvarchar" },
            new() { MetaKey = "author", GraphQlName = "author", SqlAlias = "eav_author", DataType = "nvarchar" },
        };

        var sql = transformer.GenerateFlattenedQuerySql(flattenedTable, columns);

        // Should have 3 MAX(CASE WHEN...) expressions
        var caseCount = sql.Sql.Split("MAX(CASE WHEN").Length - 1;
        caseCount.Should().Be(3);

        // Should have all column aliases
        sql.Sql.Should().Contain("[eav_title]");
        sql.Sql.Should().Contain("[eav_views]");
        sql.Sql.Should().Contain("[eav_author]");
    }

    [Fact]
    public void GenerateFlattenedQuerySql_ParametersAreCollected()
    {
        var dialect = new SqlServerDialect();
        var transformer = new EavQueryTransformer(dialect);

        var parentTable = CreateParentTable();
        var metaTable = CreateMetaTable();
        var config = new EavConfig("wp_postmeta", "wp_posts", "post_id", "meta_key", "meta_value");

        var flattenedTable = new EavFlattenedTable
        {
            ParentTable = parentTable,
            MetaTable = metaTable,
            Config = config,
            DynamicColumns = new List<EavColumn>(),
        };

        var columns = new List<EavColumn>
        {
            new() { MetaKey = "title", GraphQlName = "title", SqlAlias = "eav_title", DataType = "nvarchar" },
            new() { MetaKey = "views", GraphQlName = "views", SqlAlias = "eav_views", DataType = "nvarchar" },
        };

        var sql = transformer.GenerateFlattenedQuerySql(flattenedTable, columns);

        // Should have parameters for each meta_key
        sql.Parameters.Should().HaveCount(2);
        sql.Parameters[0].Value.Should().Be("title");
        sql.Parameters[1].Value.Should().Be("views");
    }

    #region Helper Methods

    private static DbTable CreateParentTable()
    {
        var pkColumn = new ColumnDto
        {
            ColumnName = "ID",
            GraphQlName = "ID",
            DataType = "int",
            IsPrimaryKey = true,
            IsNullable = false,
        };

        return new DbTable
        {
            DbName = "wp_posts",
            GraphQlName = "wp_posts",
            NormalizedName = "post",
            TableSchema = "dbo",
            ColumnLookup = new Dictionary<string, ColumnDto> { ["ID"] = pkColumn },
            GraphQlLookup = new Dictionary<string, ColumnDto> { ["ID"] = pkColumn },
        };
    }

    private static DbTable CreateMetaTable()
    {
        return new DbTable
        {
            DbName = "wp_postmeta",
            GraphQlName = "wp_postmeta",
            NormalizedName = "postmetum",
            TableSchema = "dbo",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
        };
    }

    #endregion
}
