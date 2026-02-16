using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Schema;

public class ProtoSchemaGeneratorTests
{
    [Fact]
    public void GenerateProto_SingleTable_ContainsSyntaxAndPackage()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var proto = ProtoSchemaGenerator.GenerateProto(model);

        proto.Should().StartWith("syntax = \"proto3\";");
        proto.Should().Contain("package bifrostql;");
    }

    [Fact]
    public void GenerateProto_SingleTable_GeneratesTableRowMessage()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var proto = ProtoSchemaGenerator.GenerateProto(model);

        proto.Should().Contain("message UsersRow {");
        proto.Should().Contain("int32 Id = 1;");
        proto.Should().Contain("string Name = 2;");
    }

    [Fact]
    public void GenerateProto_MultipleTables_GeneratesMessagePerTable()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal"))
            .Build();

        var proto = ProtoSchemaGenerator.GenerateProto(model);

        proto.Should().Contain("message UsersRow {");
        proto.Should().Contain("message OrdersRow {");
    }

    [Fact]
    public void GenerateProto_IncludesBifrostMessageEnvelope()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal"))
            .Build();

        var proto = ProtoSchemaGenerator.GenerateProto(model);

        proto.Should().Contain("message BifrostMessage {");
        proto.Should().Contain("string table = 1;");
        proto.Should().Contain("oneof payload {");
        proto.Should().Contain("UsersRow Users_row = 2;");
        proto.Should().Contain("OrdersRow Orders_row = 3;");
    }

    [Fact]
    public void GenerateProto_NullableColumn_UsesOptionalKeyword()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Email", "nvarchar", isNullable: true))
            .Build();

        var proto = ProtoSchemaGenerator.GenerateProto(model);

        proto.Should().Contain("optional string Email = 2;");
    }

    [Fact]
    public void GenerateProto_NonNullableColumn_NoOptionalKeyword()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var proto = ProtoSchemaGenerator.GenerateProto(model);

        proto.Should().Contain("  string Name = 2;");
        proto.Should().NotContain("optional string Name");
    }

    [Fact]
    public void GenerateProto_FieldNumbersAreSequential()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar")
                .WithColumn("Age", "int"))
            .Build();

        var proto = ProtoSchemaGenerator.GenerateProto(model);

        proto.Should().Contain("Id = 1;");
        proto.Should().Contain("Name = 2;");
        proto.Should().Contain("Email = 3;");
        proto.Should().Contain("Age = 4;");
    }

    [Fact]
    public void GenerateProto_EmptyModel_GeneratesOnlyBifrostMessage()
    {
        var model = DbModelTestFixture.Create().Build();

        var proto = ProtoSchemaGenerator.GenerateProto(model);

        proto.Should().Contain("syntax = \"proto3\";");
        proto.Should().Contain("message BifrostMessage {");
        proto.Should().Contain("string table = 1;");
    }
}

public class ProtoTypeMapTests
{
    [Theory]
    [InlineData("int", "int32")]
    [InlineData("smallint", "int32")]
    [InlineData("tinyint", "int32")]
    [InlineData("bigint", "int64")]
    [InlineData("decimal", "double")]
    [InlineData("float", "double")]
    [InlineData("real", "float")]
    [InlineData("bit", "bool")]
    [InlineData("datetime", "string")]
    [InlineData("datetime2", "string")]
    [InlineData("datetimeoffset", "string")]
    [InlineData("date", "string")]
    [InlineData("time", "string")]
    [InlineData("uniqueidentifier", "string")]
    [InlineData("varchar", "string")]
    [InlineData("nvarchar", "string")]
    [InlineData("char", "string")]
    [InlineData("nchar", "string")]
    [InlineData("text", "string")]
    [InlineData("ntext", "string")]
    [InlineData("binary", "bytes")]
    [InlineData("varbinary", "bytes")]
    [InlineData("image", "bytes")]
    public void GetProtoType_MapsCorrectly(string sqlType, string expectedProtoType)
    {
        ProtoSchemaGenerator.GetProtoType(sqlType).Should().Be(expectedProtoType);
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("json")]
    [InlineData("geography")]
    [InlineData("unknown_custom_type")]
    public void GetProtoType_UnknownTypes_DefaultToString(string sqlType)
    {
        ProtoSchemaGenerator.GetProtoType(sqlType).Should().Be("string");
    }
}

public class ProtoSchemaIntegrationTests
{
    [Fact]
    public void GenerateProto_AllSqlTypes_ProducesValidProto3()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("AllTypes", t => t
                .WithPrimaryKey("Id")
                .WithColumn("IntCol", "int")
                .WithColumn("SmallIntCol", "smallint")
                .WithColumn("TinyIntCol", "tinyint")
                .WithColumn("BigIntCol", "bigint")
                .WithColumn("DecimalCol", "decimal")
                .WithColumn("FloatCol", "float")
                .WithColumn("RealCol", "real")
                .WithColumn("BitCol", "bit")
                .WithColumn("DateTimeCol", "datetime")
                .WithColumn("DateTime2Col", "datetime2")
                .WithColumn("DateTimeOffsetCol", "datetimeoffset")
                .WithColumn("VarcharCol", "varchar")
                .WithColumn("NVarcharCol", "nvarchar")
                .WithColumn("BinaryCol", "binary")
                .WithColumn("VarBinaryCol", "varbinary")
                .WithColumn("NullableCol", "nvarchar", isNullable: true))
            .Build();

        var proto = ProtoSchemaGenerator.GenerateProto(model);

        proto.Should().Contain("int32 Id = 1;");
        proto.Should().Contain("int32 IntCol = 2;");
        proto.Should().Contain("int32 SmallIntCol = 3;");
        proto.Should().Contain("int32 TinyIntCol = 4;");
        proto.Should().Contain("int64 BigIntCol = 5;");
        proto.Should().Contain("double DecimalCol = 6;");
        proto.Should().Contain("double FloatCol = 7;");
        proto.Should().Contain("float RealCol = 8;");
        proto.Should().Contain("bool BitCol = 9;");
        proto.Should().Contain("string DateTimeCol = 10;");
        proto.Should().Contain("string DateTime2Col = 11;");
        proto.Should().Contain("string DateTimeOffsetCol = 12;");
        proto.Should().Contain("string VarcharCol = 13;");
        proto.Should().Contain("string NVarcharCol = 14;");
        proto.Should().Contain("bytes BinaryCol = 15;");
        proto.Should().Contain("bytes VarBinaryCol = 16;");
        proto.Should().Contain("optional string NullableCol = 17;");
    }

    [Fact]
    public void GenerateProto_MetadataTypeOverride_UsesEffectiveDataType()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Config", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Data", "nvarchar")
                .WithColumnMetadata("Data", "type", "json"))
            .Build();

        var proto = ProtoSchemaGenerator.GenerateProto(model);

        // json is an unknown type, defaults to string
        proto.Should().Contain("string Data = 2;");
    }

    [Fact]
    public void GenerateProto_ECommerceModel_GeneratesAllMessages()
    {
        var model = StandardTestFixtures.ECommerce();

        var proto = ProtoSchemaGenerator.GenerateProto(model);

        proto.Should().Contain("message CategoriesRow {");
        proto.Should().Contain("message ProductsRow {");
        proto.Should().Contain("message OrdersRow {");
        proto.Should().Contain("message OrderItemsRow {");
        proto.Should().Contain("message BifrostMessage {");
        proto.Should().Contain("CategoriesRow Categories_row =");
        proto.Should().Contain("ProductsRow Products_row =");
        proto.Should().Contain("OrdersRow Orders_row =");
        proto.Should().Contain("OrderItemsRow OrderItems_row =");
    }

    [Fact]
    public void GenerateProto_BifrostMessageFieldNumbers_StartAtTwo()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Alpha", t => t.WithPrimaryKey("Id"))
            .WithTable("Beta", t => t.WithPrimaryKey("Id"))
            .WithTable("Gamma", t => t.WithPrimaryKey("Id"))
            .Build();

        var proto = ProtoSchemaGenerator.GenerateProto(model);

        proto.Should().Contain("string table = 1;");
        proto.Should().Contain("Alpha_row = 2;");
        proto.Should().Contain("Beta_row = 3;");
        proto.Should().Contain("Gamma_row = 4;");
    }
}
