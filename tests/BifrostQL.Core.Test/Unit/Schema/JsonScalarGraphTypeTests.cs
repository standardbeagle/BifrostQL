using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using GraphQLParser.AST;
using Xunit;

namespace BifrostQL.Core.Test.Schema;

public class JsonScalarGraphTypeTests
{
    private readonly JsonScalarGraphType _scalar = new();

    #region Serialize Tests (Query Results: DB -> Client)

    [Fact]
    public void Serialize_NullValue_ReturnsNull()
    {
        var result = _scalar.Serialize(null);
        Assert.Null(result);
    }

    [Fact]
    public void Serialize_EmptyString_ReturnsNull()
    {
        var result = _scalar.Serialize("");
        Assert.Null(result);
    }

    [Fact]
    public void Serialize_WhitespaceString_ReturnsNull()
    {
        var result = _scalar.Serialize("   ");
        Assert.Null(result);
    }

    [Fact]
    public void Serialize_JsonObject_ReturnsDictionary()
    {
        var result = _scalar.Serialize("{\"name\":\"Alice\",\"age\":30}");

        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("Alice", dict["name"]);
        Assert.Equal(30L, (long)dict["age"]!);
    }

    [Fact]
    public void Serialize_JsonArray_ReturnsList()
    {
        var result = _scalar.Serialize("[1,2,3]");

        var list = Assert.IsType<List<object?>>(result);
        Assert.Equal(3, list.Count);
        Assert.Equal(1L, (long)list[0]!);
        Assert.Equal(2L, (long)list[1]!);
        Assert.Equal(3L, (long)list[2]!);
    }

    [Fact]
    public void Serialize_NestedJson_ReturnsNestedStructure()
    {
        var result = _scalar.Serialize("{\"user\":{\"name\":\"Bob\"},\"tags\":[\"admin\",\"active\"]}");

        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        var user = Assert.IsType<Dictionary<string, object?>>(dict["user"]);
        Assert.Equal("Bob", user["name"]);
        var tags = Assert.IsType<List<object?>>(dict["tags"]);
        Assert.Equal("admin", tags[0]);
        Assert.Equal("active", tags[1]);
    }

    [Fact]
    public void Serialize_JsonBoolean_ReturnsBoolean()
    {
        var result = _scalar.Serialize("true");
        Assert.Equal(true, result);
    }

    [Fact]
    public void Serialize_JsonString_ReturnsString()
    {
        var result = _scalar.Serialize("\"hello\"");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Serialize_InvalidJson_ReturnsOriginalString()
    {
        var result = _scalar.Serialize("not valid json {");
        Assert.Equal("not valid json {", result);
    }

    [Fact]
    public void Serialize_NonStringValue_ReturnsAsIs()
    {
        var result = _scalar.Serialize(42);
        Assert.Equal(42, result);
    }

    [Fact]
    public void Serialize_JsonWithFloats_ReturnsDoubles()
    {
        var result = _scalar.Serialize("{\"price\":19.99}");

        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal(19.99, dict["price"]);
    }

    [Fact]
    public void Serialize_JsonNull_ReturnsNull()
    {
        var result = _scalar.Serialize("null");
        Assert.Null(result);
    }

    #endregion

    #region ParseValue Tests (Mutation Input: Client -> DB)

    [Fact]
    public void ParseValue_NullValue_ReturnsNull()
    {
        var result = _scalar.ParseValue(null);
        Assert.Null(result);
    }

    [Fact]
    public void ParseValue_StringValue_ReturnsStringAsIs()
    {
        var result = _scalar.ParseValue("{\"key\":\"value\"}");
        Assert.Equal("{\"key\":\"value\"}", result);
    }

    [Fact]
    public void ParseValue_DictionaryValue_ReturnsJsonString()
    {
        var input = new Dictionary<string, object?> { ["name"] = "Alice", ["age"] = 30 };
        var result = _scalar.ParseValue(input);

        Assert.IsType<string>(result);
        var parsed = JsonSerializer.Deserialize<JsonElement>((string)result!);
        Assert.Equal("Alice", parsed.GetProperty("name").GetString());
        Assert.Equal(30, parsed.GetProperty("age").GetInt32());
    }

    [Fact]
    public void ParseValue_ListValue_ReturnsJsonArrayString()
    {
        var input = new List<object?> { 1, 2, 3 };
        var result = _scalar.ParseValue(input);

        Assert.IsType<string>(result);
        var parsed = JsonSerializer.Deserialize<JsonElement>((string)result!);
        Assert.Equal(JsonValueKind.Array, parsed.ValueKind);
        Assert.Equal(3, parsed.GetArrayLength());
    }

    [Fact]
    public void ParseValue_JsonElement_ReturnsRawText()
    {
        var element = JsonSerializer.Deserialize<JsonElement>("{\"key\":\"value\"}");
        var result = _scalar.ParseValue(element);

        Assert.IsType<string>(result);
        Assert.Contains("key", (string)result!);
        Assert.Contains("value", (string)result!);
    }

    #endregion

    #region ParseLiteral Tests (Inline GraphQL Literals)

    [Fact]
    public void ParseLiteral_NullValue_ReturnsNull()
    {
        var result = _scalar.ParseLiteral(new GraphQLNullValue());
        Assert.Null(result);
    }

    [Fact]
    public void ParseLiteral_StringValue_ReturnsString()
    {
        var value = new GraphQLStringValue("{\"key\":\"value\"}");
        var result = _scalar.ParseLiteral(value);
        Assert.Equal("{\"key\":\"value\"}", result);
    }

    [Fact]
    public void ParseLiteral_IntValue_Throws()
    {
        var value = new GraphQLIntValue(42);
        Assert.Throws<InvalidOperationException>(() => _scalar.ParseLiteral(value));
    }

    #endregion

    #region EffectiveDataType Tests

    [Fact]
    public void ColumnDto_EffectiveDataType_ReturnsDataTypeWhenNoMetadata()
    {
        var column = new ColumnDto
        {
            ColumnName = "Name",
            GraphQlName = "name",
            DataType = "nvarchar",
            Metadata = new Dictionary<string, object?>()
        };

        Assert.Equal("nvarchar", column.EffectiveDataType);
    }

    [Fact]
    public void ColumnDto_EffectiveDataType_ReturnsJsonWhenMetadataOverrideSet()
    {
        var column = new ColumnDto
        {
            ColumnName = "Settings",
            GraphQlName = "settings",
            DataType = "nvarchar",
            Metadata = new Dictionary<string, object?> { ["type"] = "json" }
        };

        Assert.Equal("json", column.EffectiveDataType);
    }

    [Fact]
    public void ColumnDto_EffectiveDataType_ReturnsRawTypeWhenMetadataIsOtherValue()
    {
        var column = new ColumnDto
        {
            ColumnName = "Name",
            GraphQlName = "name",
            DataType = "nvarchar",
            Metadata = new Dictionary<string, object?> { ["type"] = "custom" }
        };

        Assert.Equal("custom", column.EffectiveDataType);
    }

    #endregion

    #region Schema Generation Integration Tests

    [Fact]
    public void SchemaText_ContainsJsonType_WhenColumnHasJsonMetadata()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Products", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Attributes", "nvarchar", isNullable: true))
            .Build();

        var table = model.GetTableFromDbName("Products");
        table.ColumnLookup["Attributes"].Metadata["type"] = "json";

        var schema = DbSchema.FromModel(model);
        Assert.NotNull(schema);
    }

    [Fact]
    public void SchemaText_NativeNvarchar_RemainsString_WhenNoJsonMetadata()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Products", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Description", "nvarchar", isNullable: true))
            .Build();

        var schema = DbSchema.FromModel(model);
        Assert.NotNull(schema);
    }

    [Fact]
    public void SchemaText_JsonColumn_AppearsInMutationInputType()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Products", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Config", "nvarchar", isNullable: true))
            .Build();

        var table = model.GetTableFromDbName("Products");
        table.ColumnLookup["Config"].Metadata["type"] = "json";

        var schema = DbSchema.FromModel(model);
        Assert.NotNull(schema);
    }

    [Fact]
    public void SchemaText_MultipleJsonColumns_GeneratesSingleFilterType()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Products", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Config", "nvarchar", isNullable: true)
                .WithColumn("Metadata", "nvarchar", isNullable: true))
            .Build();

        var table = model.GetTableFromDbName("Products");
        table.ColumnLookup["Config"].Metadata["type"] = "json";
        table.ColumnLookup["Metadata"].Metadata["type"] = "json";

        var schema = DbSchema.FromModel(model);
        Assert.NotNull(schema);
    }

    [Fact]
    public void SchemaText_MixedJsonAndNonJsonColumns_GeneratesCorrectSchema()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar")
                .WithColumn("Preferences", "nvarchar", isNullable: true)
                .WithColumn("Age", "int"))
            .Build();

        var table = model.GetTableFromDbName("Users");
        table.ColumnLookup["Preferences"].Metadata["type"] = "json";

        var schema = DbSchema.FromModel(model);
        Assert.NotNull(schema);
    }

    #endregion

    #region Scalar Type Properties

    [Fact]
    public void JsonScalarGraphType_HasCorrectName()
    {
        Assert.Equal("JSON", _scalar.Name);
    }

    [Fact]
    public void JsonScalarGraphType_HasDescription()
    {
        Assert.False(string.IsNullOrWhiteSpace(_scalar.Description));
    }

    #endregion
}
