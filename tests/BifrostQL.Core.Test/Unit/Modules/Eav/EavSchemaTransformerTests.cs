using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.Modules.Eav;
using BifrostQL.Core.QueryModel;
using FluentAssertions;

namespace BifrostQL.Core.Test.Modules.Eav;

public class EavSchemaTransformerTests
{
    [Fact]
    public void BuildFlattenedTables_WithEavConfigs_ReturnsFlattenedTables()
    {
        var model = CreateModelWithEav();
        var dialect = new SqlServerDialect();
        var transformer = new EavSchemaTransformer(model, dialect);

        var tables = transformer.BuildFlattenedTables();

        tables.Should().HaveCount(1);
        tables[0].ParentTable.DbName.Should().Be("wp_posts");
        tables[0].MetaTable.DbName.Should().Be("wp_postmeta");
        tables[0].Config.ForeignKeyColumn.Should().Be("post_id");
    }

    [Fact]
    public void BuildFlattenedTables_NoEavConfigs_ReturnsEmpty()
    {
        var model = CreateModelWithoutEav();
        var dialect = new SqlServerDialect();
        var transformer = new EavSchemaTransformer(model, dialect);

        var tables = transformer.BuildFlattenedTables();

        tables.Should().BeEmpty();
    }

    [Fact]
    public void GetFlattenedTypeName_ReturnsCorrectName()
    {
        var parentTable = new DbTable
        {
            DbName = "wp_posts",
            GraphQlName = "wp_posts",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
        };

        var name = EavSchemaTransformer.GetFlattenedTypeName(parentTable);

        name.Should().Be("wp_posts_flattened");
    }

    [Fact]
    public void GetFlattenedFieldName_WithPrefix_ReturnsBaseName()
    {
        var metaTable = new DbTable
        {
            DbName = "wp_postmeta",
            GraphQlName = "wp_postmeta",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
        };

        var name = EavSchemaTransformer.GetFlattenedFieldName(metaTable);

        name.Should().Be("_flattened_postmeta");
    }

    [Fact]
    public void GetFlattenedFieldName_WithoutPrefix_ReturnsFullName()
    {
        var metaTable = new DbTable
        {
            DbName = "postmeta",
            GraphQlName = "postmeta",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
        };

        var name = EavSchemaTransformer.GetFlattenedFieldName(metaTable);

        name.Should().Be("_flattened_postmeta");
    }

    [Fact]
    public void GetFlattenedQueryFieldName_ReturnsCorrectName()
    {
        var parentTable = new DbTable
        {
            DbName = "wp_posts",
            GraphQlName = "wp_posts",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
        };

        var metaTable = new DbTable
        {
            DbName = "wp_postmeta",
            GraphQlName = "wp_postmeta",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
        };

        var name = EavSchemaTransformer.GetFlattenedQueryFieldName(parentTable, metaTable);

        name.Should().Be("wp_posts_flattened_postmeta");
    }

    [Fact]
    public void GeneratePagedTypeDefinition_ContainsExpectedFields()
    {
        var parentTable = new DbTable
        {
            DbName = "wp_posts",
            GraphQlName = "wp_posts",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
        };

        var model = CreateModelWithEav();
        var dialect = new SqlServerDialect();
        var transformer = new EavSchemaTransformer(model, dialect);

        var typeDef = transformer.GeneratePagedTypeDefinition(parentTable);

        typeDef.Should().Contain("type wp_posts_flattened_paged {");
        typeDef.Should().Contain("data: [wp_posts_flattened]");
        typeDef.Should().Contain("total: Int!");
        typeDef.Should().Contain("offset: Int");
        typeDef.Should().Contain("limit: Int");
    }

    [Fact]
    public void GenerateSchemaExtensions_ContainsTypeDefinitions()
    {
        var model = CreateModelWithEav();
        var dialect = new SqlServerDialect();
        var transformer = new EavSchemaTransformer(model, dialect);
        var tables = transformer.BuildFlattenedTables();

        var extensions = transformer.GenerateSchemaExtensions(tables);

        extensions.Should().Contain("type wp_posts_flattened {");
        extensions.Should().Contain("ID");
    }

    [Fact]
    public void GenerateSchemaExtensions_ContainsParentFieldExtension()
    {
        var model = CreateModelWithEav();
        var dialect = new SqlServerDialect();
        var transformer = new EavSchemaTransformer(model, dialect);
        var tables = transformer.BuildFlattenedTables();

        var extensions = transformer.GenerateSchemaExtensions(tables);

        extensions.Should().Contain("extend type wp_posts {");
        extensions.Should().Contain("_flattened_postmeta");
    }

    #region Helper Methods

    private static IDbModel CreateModelWithEav()
    {
        var pkColumn = new ColumnDto
        {
            ColumnName = "ID",
            GraphQlName = "ID",
            DataType = "int",
            IsPrimaryKey = true,
        };

        var parentTable = new DbTable
        {
            DbName = "wp_posts",
            GraphQlName = "wp_posts",
            NormalizedName = "post",
            TableSchema = "dbo",
            ColumnLookup = new Dictionary<string, ColumnDto> { ["ID"] = pkColumn },
            GraphQlLookup = new Dictionary<string, ColumnDto> { ["ID"] = pkColumn },
        };

        var metaTable = new DbTable
        {
            DbName = "wp_postmeta",
            GraphQlName = "wp_postmeta",
            NormalizedName = "postmetum",
            TableSchema = "dbo",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
        };

        var eavConfig = new EavConfig("wp_postmeta", "wp_posts", "post_id", "meta_key", "meta_value");

        return new TestDbModelWithEav(
            new List<IDbTable> { parentTable, metaTable },
            new List<EavConfig> { eavConfig });
    }

    private static IDbModel CreateModelWithoutEav()
    {
        var table = new DbTable
        {
            DbName = "users",
            GraphQlName = "users",
            NormalizedName = "user",
            TableSchema = "dbo",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
        };

        return new TestDbModelWithEav(
            new List<IDbTable> { table },
            new List<EavConfig>());
    }

    private class TestDbModelWithEav : IDbModel
    {
        public IReadOnlyCollection<IDbTable> Tables { get; }
        public IReadOnlyCollection<DbStoredProcedure> StoredProcedures { get; } = Array.Empty<DbStoredProcedure>();
        public IReadOnlyList<EavConfig> EavConfigs { get; }
        public IDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
        public ITypeMapper TypeMapper => SqlServerTypeMapper.Instance;

        public TestDbModelWithEav(IReadOnlyCollection<IDbTable> tables, IReadOnlyList<EavConfig> eavConfigs)
        {
            Tables = tables;
            EavConfigs = eavConfigs;
        }

        public IDbTable GetTableByFullGraphQlName(string fullName)
        {
            return Tables.First(t => t.GraphQlName == fullName);
        }

        public IDbTable GetTableFromDbName(string tableName)
        {
            return Tables.First(t => t.DbName == tableName);
        }

        public string? GetMetadataValue(string property) => null;
        public bool GetMetadataBool(string property, bool defaultValue) => defaultValue;
    }

    #endregion
}
