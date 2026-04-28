using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.Modules.Eav;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Modules.Eav;

public class EavFlattenerTests
{
    #region EavDetector Tests

    [Fact]
    public void DetectFromMetadata_AllKeysPresent_ReturnsConfig()
    {
        var metaTable = new DbTable
        {
            DbName = "wp_postmeta",
            GraphQlName = "wp_postmeta",
            NormalizedName = "postmetum",
            TableSchema = "dbo",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
            Metadata = new Dictionary<string, object?>
            {
                ["eav-parent"] = "wp_posts",
                ["eav-fk"] = "post_id",
                ["eav-key"] = "meta_key",
                ["eav-value"] = "meta_value",
            }
        };

        var parentTable = new DbTable
        {
            DbName = "wp_posts",
            GraphQlName = "wp_posts",
            NormalizedName = "post",
            TableSchema = "dbo",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
        };

        var tables = new List<IDbTable> { metaTable, parentTable };
        var config = EavDetector.DetectFromMetadata(metaTable, tables);

        config.Should().NotBeNull();
        config!.MetaTableDbName.Should().Be("wp_postmeta");
        config.ParentTableDbName.Should().Be("wp_posts");
        config.ForeignKeyColumn.Should().Be("post_id");
        config.KeyColumn.Should().Be("meta_key");
        config.ValueColumn.Should().Be("meta_value");
    }

    [Fact]
    public void DetectFromMetadata_MissingKey_ReturnsNull()
    {
        var metaTable = new DbTable
        {
            DbName = "wp_postmeta",
            GraphQlName = "wp_postmeta",
            NormalizedName = "postmetum",
            TableSchema = "dbo",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
            Metadata = new Dictionary<string, object?>
            {
                ["eav-parent"] = "wp_posts",
                ["eav-fk"] = "post_id",
                // missing eav-key and eav-value
            }
        };

        var tables = new List<IDbTable> { metaTable };
        var config = EavDetector.DetectFromMetadata(metaTable, tables);

        config.Should().BeNull();
    }

    [Fact]
    public void DetectFromMetadata_ParentNotFound_ReturnsNull()
    {
        var metaTable = new DbTable
        {
            DbName = "wp_postmeta",
            GraphQlName = "wp_postmeta",
            NormalizedName = "postmetum",
            TableSchema = "dbo",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
            Metadata = new Dictionary<string, object?>
            {
                ["eav-parent"] = "nonexistent_table",
                ["eav-fk"] = "post_id",
                ["eav-key"] = "meta_key",
                ["eav-value"] = "meta_value",
            }
        };

        var tables = new List<IDbTable> { metaTable };
        var config = EavDetector.DetectFromMetadata(metaTable, tables);

        config.Should().BeNull();
    }

    [Fact]
    public void DetectHeuristic_MetaKeyMetaValuePattern_ReturnsConfig()
    {
        var metaTable = new DbTable
        {
            DbName = "wp_postmeta",
            GraphQlName = "wp_postmeta",
            NormalizedName = "postmetum",
            TableSchema = "dbo",
            ColumnLookup = new Dictionary<string, ColumnDto>
            {
                ["post_id"] = new() { ColumnName = "post_id", DataType = "int" },
                ["meta_key"] = new() { ColumnName = "meta_key", DataType = "nvarchar" },
                ["meta_value"] = new() { ColumnName = "meta_value", DataType = "nvarchar" },
            },
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
        };

        var parentTable = new DbTable
        {
            DbName = "wp_posts",
            GraphQlName = "wp_posts",
            NormalizedName = "post",
            TableSchema = "dbo",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
        };

        var tables = new List<IDbTable> { metaTable, parentTable };
        var config = EavDetector.DetectHeuristic(metaTable, tables);

        config.Should().NotBeNull();
        config!.ForeignKeyColumn.Should().Be("post_id");
        config.KeyColumn.Should().Be("meta_key");
        config.ValueColumn.Should().Be("meta_value");
    }

    #endregion

    #region EavColumnDiscoverer Tests

    [Fact]
    public void GenerateDiscoverySql_ReturnsCorrectSql()
    {
        var dialect = new SqlServerDialect();
        var discoverer = new EavColumnDiscoverer(dialect);

        var config = new EavConfig("wp_postmeta", "wp_posts", "post_id", "meta_key", "meta_value");
        var metaTable = new DbTable
        {
            DbName = "wp_postmeta",
            TableSchema = "dbo",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
        };

        var sql = discoverer.GenerateDiscoverySql(config, metaTable);

        sql.Sql.Should().Contain("SELECT DISTINCT [meta_key] FROM [dbo].[wp_postmeta]");
        sql.Sql.Should().Contain("WHERE [meta_key] IS NOT NULL");
        sql.Sql.Should().Contain("ORDER BY [meta_key]");
    }

    [Fact]
    public void CreateColumns_GeneratesValidColumnDefinitions()
    {
        var dialect = new SqlServerDialect();
        var discoverer = new EavColumnDiscoverer(dialect);

        var metaKeys = new[] { "title", "views", "custom-field", "field.with.dots" };
        var columns = discoverer.CreateColumns(metaKeys);

        columns.Should().HaveCount(4);

        // Check title column
        var titleCol = columns.First(c => c.MetaKey == "title");
        titleCol.GraphQlName.Should().Be("title");
        titleCol.SqlAlias.Should().Be("eav_title");
        titleCol.DataType.Should().Be("nvarchar");

        // Check views column
        var viewsCol = columns.First(c => c.MetaKey == "views");
        viewsCol.GraphQlName.Should().Be("views");

        // Check special characters are handled
        var customCol = columns.First(c => c.MetaKey == "custom-field");
        customCol.GraphQlName.Should().Be("custom_field");

        var dotsCol = columns.First(c => c.MetaKey == "field.with.dots");
        dotsCol.GraphQlName.Should().Be("field_with_dots");
    }

    [Fact]
    public void CreateColumns_EmptyMetaKeys_ReturnsEmptyList()
    {
        var dialect = new SqlServerDialect();
        var discoverer = new EavColumnDiscoverer(dialect);

        var columns = discoverer.CreateColumns(Array.Empty<string>());

        columns.Should().BeEmpty();
    }

    [Fact]
    public void CreateColumns_NullAndEmptyMetaKeys_AreSkipped()
    {
        var dialect = new SqlServerDialect();
        var discoverer = new EavColumnDiscoverer(dialect);

        var metaKeys = new[] { "valid", "", null!, "   " };
        var columns = discoverer.CreateColumns(metaKeys.Where(k => k != null).Cast<string>());

        columns.Should().HaveCount(1);
        columns[0].MetaKey.Should().Be("valid");
    }

    #endregion

    #region EavTypeConverter Tests

    [Theory]
    [InlineData("123", 123L)]
    [InlineData("-456", -456L)]
    [InlineData("123456789012345", 123456789012345L)]
    public void ConvertValue_Integer_ReturnsLong(string input, long expected)
    {
        var result = EavTypeConverter.ConvertValue(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("123.45", 123.45)]
    [InlineData("-67.89", -67.89)]
    [InlineData("0.001", 0.001)]
    public void ConvertValue_Decimal_ReturnsDecimal(string input, decimal expected)
    {
        var result = EavTypeConverter.ConvertValue(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    public void ConvertValue_Boolean_ReturnsBool(string input, bool expected)
    {
        var result = EavTypeConverter.ConvertValue(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("2024-01-15")]
    [InlineData("2024-01-15T10:30:00")]
    public void ConvertValue_DateTime_ReturnsDateTime(string input)
    {
        var result = EavTypeConverter.ConvertValue(input);
        result.Should().BeOfType<DateTime>();
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("123abc")]
    [InlineData("mixed123value")]
    public void ConvertValue_String_ReturnsString(string input)
    {
        var result = EavTypeConverter.ConvertValue(input);
        result.Should().Be(input);
    }

    [Fact]
    public void ConvertValue_NullOrEmpty_ReturnsNull()
    {
        EavTypeConverter.ConvertValue(null).Should().BeNull();
        EavTypeConverter.ConvertValue("").Should().BeNull();
    }

    [Fact]
    public void InferGraphQlType_AllIntegers_ReturnsInt()
    {
        var samples = new[] { "1", "2", "100", "-50" };
        var type = EavTypeConverter.InferGraphQlType(samples);
        type.Should().Be("Int");
    }

    [Fact]
    public void InferGraphQlType_AllDecimals_ReturnsFloat()
    {
        var samples = new[] { "1.5", "2.75", "100.123" };
        var type = EavTypeConverter.InferGraphQlType(samples);
        type.Should().Be("Float");
    }

    [Fact]
    public void InferGraphQlType_AllBooleans_ReturnsBoolean()
    {
        var samples = new[] { "true", "false", "True" };
        var type = EavTypeConverter.InferGraphQlType(samples);
        type.Should().Be("Boolean");
    }

    [Fact]
    public void InferGraphQlType_MixedTypes_ReturnsString()
    {
        var samples = new[] { "1", "hello", "true" };
        var type = EavTypeConverter.InferGraphQlType(samples);
        type.Should().Be("String");
    }

    [Fact]
    public void InferGraphQlType_EmptySamples_ReturnsString()
    {
        var type = EavTypeConverter.InferGraphQlType(Array.Empty<string>());
        type.Should().Be("String");
    }

    #endregion

    #region EavSchemaCache Tests

    [Fact]
    public void SchemaCache_GetColumns_NotExists_ReturnsNull()
    {
        var cache = new EavSchemaCache();
        var result = cache.GetColumns("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public void SchemaCache_SetAndGetColumns_ReturnsColumns()
    {
        var cache = new EavSchemaCache();
        var columns = new List<EavColumn>
        {
            new() { MetaKey = "title", GraphQlName = "title", SqlAlias = "eav_title", DataType = "nvarchar" },
        };

        cache.SetColumns("wp_postmeta", columns);
        var result = cache.GetColumns("wp_postmeta");

        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        result![0].MetaKey.Should().Be("title");
    }

    [Fact]
    public void SchemaCache_AfterExpiry_ReturnsNull()
    {
        var cache = new EavSchemaCache(TimeSpan.FromMilliseconds(1));
        var columns = new List<EavColumn>
        {
            new() { MetaKey = "title", GraphQlName = "title", SqlAlias = "eav_title", DataType = "nvarchar" },
        };

        cache.SetColumns("wp_postmeta", columns);
        Thread.Sleep(50); // Wait for expiry

        var result = cache.GetColumns("wp_postmeta");
        result.Should().BeNull();
    }

    [Fact]
    public void SchemaCache_Invalidate_RemovesEntry()
    {
        var cache = new EavSchemaCache();
        var columns = new List<EavColumn>
        {
            new() { MetaKey = "title", GraphQlName = "title", SqlAlias = "eav_title", DataType = "nvarchar" },
        };

        cache.SetColumns("wp_postmeta", columns);
        cache.Invalidate("wp_postmeta");

        var result = cache.GetColumns("wp_postmeta");
        result.Should().BeNull();
    }

    [Fact]
    public void SchemaCache_InvalidateAll_ClearsAllEntries()
    {
        var cache = new EavSchemaCache();
        var columns = new List<EavColumn>
        {
            new() { MetaKey = "title", GraphQlName = "title", SqlAlias = "eav_title", DataType = "nvarchar" },
        };

        cache.SetColumns("wp_postmeta", columns);
        cache.SetColumns("wp_usermeta", columns);
        cache.InvalidateAll();

        cache.GetColumns("wp_postmeta").Should().BeNull();
        cache.GetColumns("wp_usermeta").Should().BeNull();
    }

    #endregion
}
