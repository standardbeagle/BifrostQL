using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.Modules.Eav;
using BifrostQL.Core.QueryModel;
using FluentAssertions;

namespace BifrostQL.Core.Test.Modules.Eav;

public class EavModuleIntegrationTests
{
    [Fact]
    public void Module_Initialized_WithEavConfigs_HasFlattenedTables()
    {
        var model = CreateModelWithEav();
        var dialect = new SqlServerDialect();
        var module = new EavModule(model, dialect);

        module.FlattenedTables.Should().HaveCount(1);
        module.FlattenedTables[0].ParentTable.DbName.Should().Be("wp_posts");
        module.FlattenedTables[0].MetaTable.DbName.Should().Be("wp_postmeta");
    }

    [Fact]
    public void GetFlattenedTable_ByParentName_ReturnsCorrectTable()
    {
        var model = CreateModelWithEav();
        var dialect = new SqlServerDialect();
        var module = new EavModule(model, dialect);

        var table = module.GetFlattenedTable("wp_posts");

        table.Should().NotBeNull();
        table!.ParentTable.DbName.Should().Be("wp_posts");
    }

    [Fact]
    public void GetFlattenedTable_ByWrongName_ReturnsNull()
    {
        var model = CreateModelWithEav();
        var dialect = new SqlServerDialect();
        var module = new EavModule(model, dialect);

        var table = module.GetFlattenedTable("nonexistent");

        table.Should().BeNull();
    }

    [Fact]
    public void GetFlattenedTableByMetaTable_ByMetaName_ReturnsCorrectTable()
    {
        var model = CreateModelWithEav();
        var dialect = new SqlServerDialect();
        var module = new EavModule(model, dialect);

        var table = module.GetFlattenedTableByMetaTable("wp_postmeta");

        table.Should().NotBeNull();
        table!.MetaTable.DbName.Should().Be("wp_postmeta");
    }

    [Fact]
    public void ModuleExtensions_IsEavParentTable_ParentTable_ReturnsTrue()
    {
        var model = CreateModelWithEav();
        var dialect = new SqlServerDialect();
        var module = new EavModule(model, dialect);

        var parentTable = model.Tables.First(t => t.DbName == "wp_posts");
        parentTable.IsEavParentTable(model).Should().BeTrue();
    }

    [Fact]
    public void ModuleExtensions_IsEavMetaTable_MetaTable_ReturnsTrue()
    {
        var model = CreateModelWithEav();
        var dialect = new SqlServerDialect();
        var module = new EavModule(model, dialect);

        var metaTable = model.Tables.First(t => t.DbName == "wp_postmeta");
        metaTable.IsEavMetaTable(model).Should().BeTrue();
    }

    [Fact]
    public void ModuleExtensions_GetEavConfig_MetaTable_ReturnsConfig()
    {
        var model = CreateModelWithEav();

        var metaTable = model.Tables.First(t => t.DbName == "wp_postmeta");
        var config = metaTable.GetEavConfig(model);

        config.Should().NotBeNull();
        config!.MetaTableDbName.Should().Be("wp_postmeta");
        config.ParentTableDbName.Should().Be("wp_posts");
    }

    [Fact]
    public void EavColumn_ToColumnDto_CreatesValidColumnDto()
    {
        var eavColumn = new EavColumn
        {
            MetaKey = "title",
            GraphQlName = "title",
            SqlAlias = "eav_title",
            DataType = "nvarchar",
        };

        var columnDto = eavColumn.ToColumnDto();

        columnDto.ColumnName.Should().Be("eav_title");
        columnDto.GraphQlName.Should().Be("title");
        columnDto.DataType.Should().Be("nvarchar");
        columnDto.IsNullable.Should().BeTrue();
        columnDto.IsPrimaryKey.Should().BeFalse();
    }

    [Fact]
    public void EavFlattenedQuery_DefaultValues_AreSet()
    {
        var model = CreateModelWithEav();
        var dialect = new SqlServerDialect();
        var module = new EavModule(model, dialect);
        var flattenedTable = module.FlattenedTables[0];

        var query = new EavFlattenedQuery
        {
            FlattenedTable = flattenedTable,
            Columns = null,
            IncludeTotal = true,
        };

        query.FlattenedTable.Should().NotBeNull();
        query.Columns.Should().BeNull();
        query.IncludeTotal.Should().BeTrue();
        query.Limit.Should().BeNull();
        query.Offset.Should().BeNull();
    }

    [Fact]
    public void EavQueryResult_ContainsExpectedData()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["ID"] = 1, ["eav_title"] = "Hello" },
            new() { ["ID"] = 2, ["eav_title"] = "World" },
        };

        var columns = new List<EavColumn>
        {
            new() { MetaKey = "title", GraphQlName = "title", SqlAlias = "eav_title", DataType = "nvarchar" },
        };

        var result = new EavQueryResult
        {
            Rows = rows,
            TotalCount = 2,
            Columns = columns,
        };

        result.Rows.Should().HaveCount(2);
        result.TotalCount.Should().Be(2);
        result.Columns.Should().HaveCount(1);
    }

    [Fact]
    public void EavPagedResult_ContainsExpectedData()
    {
        var data = new List<Dictionary<string, object?>>
        {
            new() { ["ID"] = 1, ["eav_title"] = "Hello" },
        };

        var result = new EavPagedResult
        {
            Data = data,
            Total = 10,
            Offset = 0,
            Limit = 1,
        };

        result.Data.Should().HaveCount(1);
        result.Total.Should().Be(10);
        result.Offset.Should().Be(0);
        result.Limit.Should().Be(1);
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
