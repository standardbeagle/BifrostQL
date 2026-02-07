using System.Data;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.StoredProcedure;

public class DbStoredProcedureModelTests
{
    [Fact]
    public void DbProcedureParameter_ToString_IncludesDirectionAndType()
    {
        var param = new DbProcedureParameter
        {
            DbName = "UserId",
            GraphQlName = "userId",
            DataType = "int",
            Direction = ParameterDirection.Input,
            OrdinalPosition = 1,
            IsNullable = false,
        };

        param.ToString().Should().Contain("UserId").And.Contain("int").And.Contain("Input");
    }

    [Fact]
    public void DbProcedureParameter_ToString_ShowsNullable()
    {
        var param = new DbProcedureParameter
        {
            DbName = "Name",
            GraphQlName = "name",
            DataType = "nvarchar",
            Direction = ParameterDirection.Input,
            OrdinalPosition = 1,
            IsNullable = true,
        };

        param.ToString().Should().Contain("NULL");
    }

    [Fact]
    public void DbStoredProcedure_FullDbRef_WithSchema()
    {
        var proc = CreateProc("dbo", "GetUsers");
        proc.FullDbRef.Should().Be("[dbo].[GetUsers]");
    }

    [Fact]
    public void DbStoredProcedure_FullDbRef_WithoutSchema()
    {
        var proc = new DbStoredProcedure
        {
            DbName = "GetUsers",
            GraphQlName = "getUsers",
            ProcedureSchema = "",
            Parameters = Array.Empty<DbProcedureParameter>(),
        };

        proc.FullDbRef.Should().Be("[GetUsers]");
    }

    [Fact]
    public void DbStoredProcedure_FullGraphQlName_DboSchema_OmitsSchema()
    {
        var proc = CreateProc("dbo", "GetUsers");
        proc.FullGraphQlName.Should().Be("getUsers");
    }

    [Fact]
    public void DbStoredProcedure_FullGraphQlName_NonDboSchema_IncludesSchema()
    {
        var proc = CreateProc("reporting", "GetSalesReport");
        proc.FullGraphQlName.Should().Be("reporting_getSalesReport");
    }

    [Fact]
    public void DbStoredProcedure_InputTypeName()
    {
        var proc = CreateProc("dbo", "GetUsers");
        proc.InputTypeName.Should().Be("sp_getUsers_Input");
    }

    [Fact]
    public void DbStoredProcedure_ResultTypeName()
    {
        var proc = CreateProc("dbo", "GetUsers");
        proc.ResultTypeName.Should().Be("sp_getUsers_Result");
    }

    [Fact]
    public void DbStoredProcedure_InputParameters_FiltersCorrectly()
    {
        var proc = CreateProcWithMixedParams();

        proc.InputParameters.Should().HaveCount(2);
        proc.InputParameters.Select(p => p.DbName).Should().Contain("UserId").And.Contain("InOutParam");
    }

    [Fact]
    public void DbStoredProcedure_OutputParameters_FiltersCorrectly()
    {
        var proc = CreateProcWithMixedParams();

        proc.OutputParameters.Should().HaveCount(2);
        proc.OutputParameters.Select(p => p.DbName).Should().Contain("TotalCount").And.Contain("InOutParam");
    }

    [Fact]
    public void DbStoredProcedure_ToString_IncludesSchemaAndName()
    {
        var proc = CreateProc("dbo", "GetUsers");
        proc.ToString().Should().Be("[dbo].[GetUsers]");
    }

    [Fact]
    public void MatchesFilter_NoFilters_ReturnsTrue()
    {
        DbStoredProcedure.MatchesFilter("GetUsers", null, null).Should().BeTrue();
    }

    [Fact]
    public void MatchesFilter_IncludeMatches_ReturnsTrue()
    {
        DbStoredProcedure.MatchesFilter("GetUsers", "^Get", null).Should().BeTrue();
    }

    [Fact]
    public void MatchesFilter_IncludeDoesNotMatch_ReturnsFalse()
    {
        DbStoredProcedure.MatchesFilter("DeleteUsers", "^Get", null).Should().BeFalse();
    }

    [Fact]
    public void MatchesFilter_ExcludeMatches_ReturnsFalse()
    {
        DbStoredProcedure.MatchesFilter("sp_internal_cleanup", null, "^sp_internal").Should().BeFalse();
    }

    [Fact]
    public void MatchesFilter_ExcludeDoesNotMatch_ReturnsTrue()
    {
        DbStoredProcedure.MatchesFilter("GetUsers", null, "^sp_internal").Should().BeTrue();
    }

    [Fact]
    public void MatchesFilter_ExcludeTakesPrecedence()
    {
        DbStoredProcedure.MatchesFilter("GetInternalUsers", "^Get", "Internal").Should().BeFalse();
    }

    [Fact]
    public void MatchesFilter_CaseInsensitive()
    {
        DbStoredProcedure.MatchesFilter("getusers", "^Get", null).Should().BeTrue();
    }

    private static DbStoredProcedure CreateProc(string schema, string name)
    {
        return new DbStoredProcedure
        {
            DbName = name,
            GraphQlName = name.ToGraphQl("sp"),
            ProcedureSchema = schema,
            Parameters = Array.Empty<DbProcedureParameter>(),
        };
    }

    private static DbStoredProcedure CreateProcWithMixedParams()
    {
        return new DbStoredProcedure
        {
            DbName = "GetUserData",
            GraphQlName = "getUserData",
            ProcedureSchema = "dbo",
            Parameters = new[]
            {
                new DbProcedureParameter
                {
                    DbName = "UserId",
                    GraphQlName = "userId",
                    DataType = "int",
                    Direction = ParameterDirection.Input,
                    OrdinalPosition = 1,
                },
                new DbProcedureParameter
                {
                    DbName = "TotalCount",
                    GraphQlName = "totalCount",
                    DataType = "int",
                    Direction = ParameterDirection.Output,
                    OrdinalPosition = 2,
                },
                new DbProcedureParameter
                {
                    DbName = "InOutParam",
                    GraphQlName = "inOutParam",
                    DataType = "nvarchar",
                    Direction = ParameterDirection.InputOutput,
                    OrdinalPosition = 3,
                },
            },
        };
    }
}

public class StoredProcedureSchemaGenerationTests
{
    [Fact]
    public void SchemaText_IncludesReadOnlyProcInQueryType()
    {
        var model = CreateModelWithProcs(readOnly: true);
        var schemaText = GetSchemaText(model);

        schemaText.Should().Contain("type database {");
        schemaText.Should().Contain("getUsers");
    }

    [Fact]
    public void SchemaText_IncludesMutatingProcInMutationType()
    {
        var model = CreateModelWithProcs(readOnly: false);
        var schemaText = GetSchemaText(model);

        schemaText.Should().Contain("type databaseInput {");
        var mutSection = ExtractTypeBlock(schemaText, "type databaseInput");
        mutSection.Should().Contain("updateUserStatus");
    }

    [Fact]
    public void SchemaText_ReadOnlyProcNotInMutationType()
    {
        var model = CreateModelWithProcs(readOnly: true);
        var schemaText = GetSchemaText(model);

        var mutSection = ExtractTypeBlock(schemaText, "type databaseInput");
        mutSection.Should().NotContain("getUsers");
    }

    [Fact]
    public void SchemaText_GeneratesResultType()
    {
        var model = CreateModelWithProcs(readOnly: true);
        var schemaText = GetSchemaText(model);

        schemaText.Should().Contain("type sp_getUsers_Result {");
        schemaText.Should().Contain("resultSets: [[JSON]]");
        schemaText.Should().Contain("affectedRows: Int!");
    }

    [Fact]
    public void SchemaText_GeneratesInputType()
    {
        var proc = new DbStoredProcedure
        {
            DbName = "GetUserById",
            GraphQlName = "getUserById",
            ProcedureSchema = "dbo",
            IsReadOnly = true,
            Parameters = new[]
            {
                new DbProcedureParameter
                {
                    DbName = "UserId",
                    GraphQlName = "userId",
                    DataType = "int",
                    Direction = ParameterDirection.Input,
                    OrdinalPosition = 1,
                    IsNullable = false,
                },
            },
        };
        var model = CreateModelWithCustomProcs(proc);
        var schemaText = GetSchemaText(model);

        schemaText.Should().Contain("input sp_getUserById_Input {");
        schemaText.Should().Contain("userId: Int!");
    }

    [Fact]
    public void SchemaText_NullableInputParameter()
    {
        var proc = new DbStoredProcedure
        {
            DbName = "SearchUsers",
            GraphQlName = "searchUsers",
            ProcedureSchema = "dbo",
            IsReadOnly = true,
            Parameters = new[]
            {
                new DbProcedureParameter
                {
                    DbName = "NameFilter",
                    GraphQlName = "nameFilter",
                    DataType = "nvarchar",
                    Direction = ParameterDirection.Input,
                    OrdinalPosition = 1,
                    IsNullable = true,
                },
            },
        };
        var model = CreateModelWithCustomProcs(proc);
        var schemaText = GetSchemaText(model);

        schemaText.Should().Contain("nameFilter: String");
        schemaText.Should().NotContain("nameFilter: String!");
    }

    [Fact]
    public void SchemaText_NoInputType_WhenNoParameters()
    {
        var proc = new DbStoredProcedure
        {
            DbName = "GetAllUsers",
            GraphQlName = "getAllUsers",
            ProcedureSchema = "dbo",
            IsReadOnly = true,
            Parameters = Array.Empty<DbProcedureParameter>(),
        };
        var model = CreateModelWithCustomProcs(proc);
        var schemaText = GetSchemaText(model);

        schemaText.Should().NotContain("input sp_getAllUsers_Input");
    }

    [Fact]
    public void SchemaText_OutputParameter_AppearsInResultType()
    {
        var proc = new DbStoredProcedure
        {
            DbName = "GetUserData",
            GraphQlName = "getUserData",
            ProcedureSchema = "dbo",
            IsReadOnly = true,
            Parameters = new[]
            {
                new DbProcedureParameter
                {
                    DbName = "UserId",
                    GraphQlName = "userId",
                    DataType = "int",
                    Direction = ParameterDirection.Input,
                    OrdinalPosition = 1,
                },
                new DbProcedureParameter
                {
                    DbName = "TotalCount",
                    GraphQlName = "totalCount",
                    DataType = "int",
                    Direction = ParameterDirection.Output,
                    OrdinalPosition = 2,
                },
            },
        };
        var model = CreateModelWithCustomProcs(proc);
        var schemaText = GetSchemaText(model);

        var resultBlock = ExtractTypeBlock(schemaText, "type sp_getUserData_Result");
        resultBlock.Should().Contain("totalCount: Int");
    }

    [Fact]
    public void SchemaText_ProcWithNoInputParams_HasNoArguments()
    {
        var proc = new DbStoredProcedure
        {
            DbName = "GetAllUsers",
            GraphQlName = "getAllUsers",
            ProcedureSchema = "dbo",
            IsReadOnly = true,
            Parameters = Array.Empty<DbProcedureParameter>(),
        };
        var model = CreateModelWithCustomProcs(proc);
        var schemaText = GetSchemaText(model);

        var queryBlock = ExtractTypeBlock(schemaText, "type database");
        queryBlock.Should().Contain("getAllUsers: sp_getAllUsers_Result");
        queryBlock.Should().NotContain("getAllUsers(");
    }

    [Fact]
    public void SchemaText_ProcWithInputParams_HasInputArgument()
    {
        var proc = new DbStoredProcedure
        {
            DbName = "GetUserById",
            GraphQlName = "getUserById",
            ProcedureSchema = "dbo",
            IsReadOnly = true,
            Parameters = new[]
            {
                new DbProcedureParameter
                {
                    DbName = "UserId",
                    GraphQlName = "userId",
                    DataType = "int",
                    Direction = ParameterDirection.Input,
                    OrdinalPosition = 1,
                },
            },
        };
        var model = CreateModelWithCustomProcs(proc);
        var schemaText = GetSchemaText(model);

        var queryBlock = ExtractTypeBlock(schemaText, "type database");
        queryBlock.Should().Contain("getUserById(input: sp_getUserById_Input): sp_getUserById_Result");
    }

    [Fact]
    public void SchemaText_MultipleInputParams_AllIncludedInInputType()
    {
        var proc = new DbStoredProcedure
        {
            DbName = "Search",
            GraphQlName = "search",
            ProcedureSchema = "dbo",
            IsReadOnly = true,
            Parameters = new[]
            {
                new DbProcedureParameter
                {
                    DbName = "Query",
                    GraphQlName = "query",
                    DataType = "nvarchar",
                    Direction = ParameterDirection.Input,
                    OrdinalPosition = 1,
                    IsNullable = false,
                },
                new DbProcedureParameter
                {
                    DbName = "MaxResults",
                    GraphQlName = "maxResults",
                    DataType = "int",
                    Direction = ParameterDirection.Input,
                    OrdinalPosition = 2,
                    IsNullable = true,
                },
            },
        };
        var model = CreateModelWithCustomProcs(proc);
        var schemaText = GetSchemaText(model);

        schemaText.Should().Contain("query: String!");
        schemaText.Should().Contain("maxResults: Int");
    }

    [Fact]
    public void Schema_WithStoredProcedures_BuildsSuccessfully()
    {
        var proc = new DbStoredProcedure
        {
            DbName = "GetUserById",
            GraphQlName = "getUserById",
            ProcedureSchema = "dbo",
            IsReadOnly = true,
            Parameters = new[]
            {
                new DbProcedureParameter
                {
                    DbName = "UserId",
                    GraphQlName = "userId",
                    DataType = "int",
                    Direction = ParameterDirection.Input,
                    OrdinalPosition = 1,
                },
            },
        };
        var model = CreateModelWithCustomProcs(proc);

        var schema = DbSchema.FromModel(model);
        schema.Should().NotBeNull();
    }

    [Fact]
    public void Schema_WithMutatingProc_BuildsSuccessfully()
    {
        var proc = new DbStoredProcedure
        {
            DbName = "UpdateUserStatus",
            GraphQlName = "updateUserStatus",
            ProcedureSchema = "dbo",
            IsReadOnly = false,
            Parameters = new[]
            {
                new DbProcedureParameter
                {
                    DbName = "UserId",
                    GraphQlName = "userId",
                    DataType = "int",
                    Direction = ParameterDirection.Input,
                    OrdinalPosition = 1,
                },
                new DbProcedureParameter
                {
                    DbName = "Status",
                    GraphQlName = "status",
                    DataType = "nvarchar",
                    Direction = ParameterDirection.Input,
                    OrdinalPosition = 2,
                },
            },
        };
        var model = CreateModelWithCustomProcs(proc);

        var schema = DbSchema.FromModel(model);
        schema.Should().NotBeNull();
    }

    [Fact]
    public void Schema_WithOutputParams_BuildsSuccessfully()
    {
        var proc = new DbStoredProcedure
        {
            DbName = "GetUserData",
            GraphQlName = "getUserData",
            ProcedureSchema = "dbo",
            IsReadOnly = true,
            Parameters = new[]
            {
                new DbProcedureParameter
                {
                    DbName = "UserId",
                    GraphQlName = "userId",
                    DataType = "int",
                    Direction = ParameterDirection.Input,
                    OrdinalPosition = 1,
                },
                new DbProcedureParameter
                {
                    DbName = "TotalCount",
                    GraphQlName = "totalCount",
                    DataType = "int",
                    Direction = ParameterDirection.Output,
                    OrdinalPosition = 2,
                },
            },
        };
        var model = CreateModelWithCustomProcs(proc);

        var schema = DbSchema.FromModel(model);
        schema.Should().NotBeNull();
    }

    [Fact]
    public void Schema_WithNoProcs_StillBuildsSuccessfully()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var schema = DbSchema.FromModel(model);
        schema.Should().NotBeNull();
    }

    [Fact]
    public void Schema_MultipleProcsReadAndMutate_BuildsSuccessfully()
    {
        var procs = new[]
        {
            new DbStoredProcedure
            {
                DbName = "GetUsers",
                GraphQlName = "getUsers",
                ProcedureSchema = "dbo",
                IsReadOnly = true,
                Parameters = Array.Empty<DbProcedureParameter>(),
            },
            new DbStoredProcedure
            {
                DbName = "UpdateUser",
                GraphQlName = "updateUser",
                ProcedureSchema = "dbo",
                IsReadOnly = false,
                Parameters = new[]
                {
                    new DbProcedureParameter
                    {
                        DbName = "UserId",
                        GraphQlName = "userId",
                        DataType = "int",
                        Direction = ParameterDirection.Input,
                        OrdinalPosition = 1,
                    },
                },
            },
        };

        var model = CreateModelWithCustomProcs(procs);

        var schema = DbSchema.FromModel(model);
        schema.Should().NotBeNull();
    }

    [Fact]
    public void Schema_ProcWithNoResultSets_BuildsSuccessfully()
    {
        var proc = new DbStoredProcedure
        {
            DbName = "CleanupOldRecords",
            GraphQlName = "cleanupOldRecords",
            ProcedureSchema = "dbo",
            IsReadOnly = false,
            Parameters = new[]
            {
                new DbProcedureParameter
                {
                    DbName = "OlderThanDays",
                    GraphQlName = "olderThanDays",
                    DataType = "int",
                    Direction = ParameterDirection.Input,
                    OrdinalPosition = 1,
                },
            },
        };
        var model = CreateModelWithCustomProcs(proc);

        var schema = DbSchema.FromModel(model);
        schema.Should().NotBeNull();

        var schemaText = GetSchemaText(model);
        schemaText.Should().Contain("affectedRows: Int!");
    }

    [Fact]
    public void Schema_ProcWithAllDataTypes_BuildsSuccessfully()
    {
        var proc = new DbStoredProcedure
        {
            DbName = "TypeTest",
            GraphQlName = "typeTest",
            ProcedureSchema = "dbo",
            IsReadOnly = true,
            Parameters = new[]
            {
                new DbProcedureParameter { DbName = "IntParam", GraphQlName = "intParam", DataType = "int", Direction = ParameterDirection.Input, OrdinalPosition = 1 },
                new DbProcedureParameter { DbName = "StringParam", GraphQlName = "stringParam", DataType = "nvarchar", Direction = ParameterDirection.Input, OrdinalPosition = 2 },
                new DbProcedureParameter { DbName = "BoolParam", GraphQlName = "boolParam", DataType = "bit", Direction = ParameterDirection.Input, OrdinalPosition = 3 },
                new DbProcedureParameter { DbName = "DecimalParam", GraphQlName = "decimalParam", DataType = "decimal", Direction = ParameterDirection.Input, OrdinalPosition = 4 },
                new DbProcedureParameter { DbName = "DateParam", GraphQlName = "dateParam", DataType = "datetime2", Direction = ParameterDirection.Input, OrdinalPosition = 5, IsNullable = true },
            },
        };
        var model = CreateModelWithCustomProcs(proc);

        var schema = DbSchema.FromModel(model);
        schema.Should().NotBeNull();

        var schemaText = GetSchemaText(model);
        schemaText.Should().Contain("intParam: Int!");
        schemaText.Should().Contain("stringParam: String!");
        schemaText.Should().Contain("boolParam: Boolean!");
        schemaText.Should().Contain("decimalParam: Decimal!");
        schemaText.Should().Contain("dateParam: DateTime");
    }

    [Fact]
    public void Schema_ProcWithNonDboSchema_UsesSchemaPrefix()
    {
        var proc = new DbStoredProcedure
        {
            DbName = "GetReport",
            GraphQlName = "getReport",
            ProcedureSchema = "reporting",
            IsReadOnly = true,
            Parameters = Array.Empty<DbProcedureParameter>(),
        };
        var model = CreateModelWithCustomProcs(proc);
        var schemaText = GetSchemaText(model);

        schemaText.Should().Contain("reporting_getReport");
        schemaText.Should().Contain("sp_reporting_getReport_Result");
    }

    private static string GetSchemaText(IDbModel model)
    {
        var includeDynamicJoins = model.GetMetadataBool("dynamic-joins", true);
        var method = typeof(DbSchema).Assembly
            .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
            .GetMethod("SchemaTextFromModel", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!;
        return (string)method.Invoke(null, new object[] { model, includeDynamicJoins })!;
    }

    private static string ExtractTypeBlock(string schemaText, string typePrefix)
    {
        var startIdx = schemaText.IndexOf(typePrefix, StringComparison.Ordinal);
        if (startIdx < 0) return string.Empty;
        var braceStart = schemaText.IndexOf('{', startIdx);
        if (braceStart < 0) return string.Empty;
        var depth = 1;
        var pos = braceStart + 1;
        while (pos < schemaText.Length && depth > 0)
        {
            if (schemaText[pos] == '{') depth++;
            else if (schemaText[pos] == '}') depth--;
            pos++;
        }
        return schemaText.Substring(startIdx, pos - startIdx);
    }

    private static IDbModel CreateModelWithProcs(bool readOnly)
    {
        var procName = readOnly ? "GetUsers" : "UpdateUserStatus";
        var graphQlName = readOnly ? "getUsers" : "updateUserStatus";

        var proc = new DbStoredProcedure
        {
            DbName = procName,
            GraphQlName = graphQlName,
            ProcedureSchema = "dbo",
            IsReadOnly = readOnly,
            Parameters = readOnly
                ? Array.Empty<DbProcedureParameter>()
                : new[]
                {
                    new DbProcedureParameter
                    {
                        DbName = "UserId",
                        GraphQlName = "userId",
                        DataType = "int",
                        Direction = ParameterDirection.Input,
                        OrdinalPosition = 1,
                    },
                },
        };

        return CreateModelWithCustomProcs(proc);
    }

    private static IDbModel CreateModelWithCustomProcs(params DbStoredProcedure[] procs)
    {
        return new TestDbModelWithProcs(
            DbModelTestFixture.Create()
                .WithTable("Users", t => t
                    .WithPrimaryKey("Id")
                    .WithColumn("Name", "nvarchar"))
                .Build(),
            procs);
    }
}

public class DbModelIncludeExcludeTests
{
    [Fact]
    public void FromTables_NoFilter_IncludesAllProcs()
    {
        var procs = new[]
        {
            CreateProc("GetUsers"),
            CreateProc("DeleteUsers"),
        };

        var model = BuildModelWithProcs(procs, new Dictionary<string, object?>());

        model.StoredProcedures.Should().HaveCount(2);
    }

    [Fact]
    public void FromTables_IncludeFilter_OnlyMatchingProcs()
    {
        var procs = new[]
        {
            CreateProc("GetUsers"),
            CreateProc("GetOrders"),
            CreateProc("DeleteUsers"),
        };

        var metadata = new Dictionary<string, object?> { ["sp-include"] = "^Get" };
        var model = BuildModelWithProcs(procs, metadata);

        model.StoredProcedures.Should().HaveCount(2);
        model.StoredProcedures.Select(p => p.DbName).Should().Contain("GetUsers").And.Contain("GetOrders");
    }

    [Fact]
    public void FromTables_ExcludeFilter_RemovesMatchingProcs()
    {
        var procs = new[]
        {
            CreateProc("GetUsers"),
            CreateProc("sp_internal_cleanup"),
            CreateProc("sp_internal_audit"),
        };

        var metadata = new Dictionary<string, object?> { ["sp-exclude"] = "^sp_internal" };
        var model = BuildModelWithProcs(procs, metadata);

        model.StoredProcedures.Should().HaveCount(1);
        model.StoredProcedures.Single().DbName.Should().Be("GetUsers");
    }

    [Fact]
    public void FromTables_BothFilters_ExcludeTakesPrecedence()
    {
        var procs = new[]
        {
            CreateProc("GetPublicUsers"),
            CreateProc("GetInternalUsers"),
            CreateProc("DeleteUsers"),
        };

        var metadata = new Dictionary<string, object?>
        {
            ["sp-include"] = "^Get",
            ["sp-exclude"] = "Internal",
        };
        var model = BuildModelWithProcs(procs, metadata);

        model.StoredProcedures.Should().HaveCount(1);
        model.StoredProcedures.Single().DbName.Should().Be("GetPublicUsers");
    }

    [Fact]
    public void FromTables_EmptyProcs_ModelHasEmptyCollection()
    {
        var model = BuildModelWithProcs(Array.Empty<DbStoredProcedure>(), new Dictionary<string, object?>());
        model.StoredProcedures.Should().BeEmpty();
    }

    [Fact]
    public void FromTables_DefaultOverload_HasEmptyProcs()
    {
        var table = CreateMinimalTable();
        var metadataLoader = new TestMetadataLoader(new Dictionary<string, object?>());
        var model = DbModel.FromTables(new List<DbTable> { table }, metadataLoader);

        model.StoredProcedures.Should().BeEmpty();
    }

    private static DbStoredProcedure CreateProc(string name)
    {
        return new DbStoredProcedure
        {
            DbName = name,
            GraphQlName = name.ToGraphQl("sp"),
            ProcedureSchema = "dbo",
            Parameters = Array.Empty<DbProcedureParameter>(),
        };
    }

    private static DbModel BuildModelWithProcs(IReadOnlyCollection<DbStoredProcedure> procs, Dictionary<string, object?> extraMetadata)
    {
        var table = CreateMinimalTable();
        var metadataLoader = new TestMetadataLoader(extraMetadata);
        return DbModel.FromTables(new List<DbTable> { table }, metadataLoader, procs);
    }

    private static DbTable CreateMinimalTable()
    {
        var idColumn = new ColumnDto
        {
            TableCatalog = "db",
            TableSchema = "dbo",
            TableName = "Users",
            ColumnName = "Id",
            GraphQlName = "id",
            NormalizedName = "id",
            ColumnRef = new ColumnRef("db", "dbo", "Users", "Id"),
            DataType = "int",
            IsPrimaryKey = true,
            OrdinalPosition = 1,
        };

        return new DbTable
        {
            DbName = "Users",
            GraphQlName = "users",
            NormalizedName = "User",
            TableSchema = "dbo",
            TableType = "BASE TABLE",
            ColumnLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase) { ["Id"] = idColumn },
            GraphQlLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase) { ["id"] = idColumn },
        };
    }

    private sealed class TestMetadataLoader : IMetadataLoader
    {
        private readonly Dictionary<string, object?> _dbMetadata;

        public TestMetadataLoader(Dictionary<string, object?> dbMetadata)
        {
            _dbMetadata = dbMetadata;
        }

        public void ApplyDatabaseMetadata(IDictionary<string, object?> metadata, string rootName = ":root")
        {
            foreach (var kv in _dbMetadata)
                metadata[kv.Key] = kv.Value;
        }

        public void ApplySchemaMetadata(IDbSchema schema, IDictionary<string, object?> metadata) { }
        public void ApplyTableMetadata(IDbTable table, IDictionary<string, object?> metadata) { }
        public void ApplyColumnMetadata(IDbTable table, ColumnDto column, IDictionary<string, object?> metadata) { }
    }
}

internal sealed class TestDbModelWithProcs : IDbModel
{
    private readonly IDbModel _inner;
    private readonly IReadOnlyCollection<DbStoredProcedure> _procs;

    public TestDbModelWithProcs(IDbModel inner, IReadOnlyCollection<DbStoredProcedure> procs)
    {
        _inner = inner;
        _procs = procs;
    }

    public IReadOnlyCollection<IDbTable> Tables => _inner.Tables;
    public IReadOnlyCollection<DbStoredProcedure> StoredProcedures => _procs;
    public IDictionary<string, object?> Metadata
    {
        get => _inner.Metadata;
        init => throw new NotSupportedException();
    }
    public string? GetMetadataValue(string property) => _inner.GetMetadataValue(property);
    public bool GetMetadataBool(string property, bool defaultValue) => _inner.GetMetadataBool(property, defaultValue);
    public IDbTable GetTableByFullGraphQlName(string fullName) => _inner.GetTableByFullGraphQlName(fullName);
    public IDbTable GetTableFromDbName(string tableName) => _inner.GetTableFromDbName(tableName);
}
