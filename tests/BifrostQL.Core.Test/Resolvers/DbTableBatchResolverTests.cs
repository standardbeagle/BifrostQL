using System.Reflection;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Resolvers;

public sealed class DbTableBatchResolverTests
{
    private static readonly MethodInfo SchemaTextFromModelMethod = typeof(DbSchema).Assembly
        .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
        .GetMethod("SchemaTextFromModel", BindingFlags.Static | BindingFlags.Public)!;

    private static string GetSchemaText(IDbModel model)
        => (string)SchemaTextFromModelMethod.Invoke(null, new object[] { model, true })!;

    #region Schema Generation Tests

    [Fact]
    public void SchemaText_ContainsBatchFieldDefinition()
    {
        var model = CreateSimpleModel();
        var schemaText = GetSchemaText(model);

        schemaText.Should().Contain("Users_batch(actions: [batch_Users!]!) : Int");
    }

    [Fact]
    public void SchemaText_ContainsBatchInputType()
    {
        var model = CreateSimpleModel();
        var schemaText = GetSchemaText(model);

        schemaText.Should().Contain("input batch_Users {");
        schemaText.Should().Contain("insert: Insert_Users");
        schemaText.Should().Contain("update: Update_Users");
        schemaText.Should().Contain("upsert: Upsert_Users");
        schemaText.Should().Contain("delete: Delete_Users");
    }

    [Fact]
    public void SchemaText_ContainsBatchFieldForMultipleTables()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal"))
            .Build();

        var schemaText = GetSchemaText(model);

        schemaText.Should().Contain("Users_batch(actions: [batch_Users!]!) : Int");
        schemaText.Should().Contain("Orders_batch(actions: [batch_Orders!]!) : Int");
        schemaText.Should().Contain("input batch_Users {");
        schemaText.Should().Contain("input batch_Orders {");
    }

    #endregion

    #region Schema Wiring Tests

    [Fact]
    public void DbSchema_BuildsSuccessfully_WithBatchResolver()
    {
        var model = CreateSimpleModel();

        var schema = DbSchema.FromModel(model);

        schema.Should().NotBeNull();
    }

    [Fact]
    public void DbSchema_BuildsSuccessfully_WithMultipleTablesAndBatchResolver()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar"))
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithColumn("Status", "nvarchar"))
            .Build();

        var schema = DbSchema.FromModel(model);

        schema.Should().NotBeNull();
    }

    [Fact]
    public void DbSchema_BuildsSuccessfully_WithBatchMaxSizeMetadata()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithMetadata("batch-max-size", "50"))
            .Build();

        var schema = DbSchema.FromModel(model);

        schema.Should().NotBeNull();
    }

    #endregion

    #region Batch Size Metadata Tests

    [Fact]
    public void BatchResolver_CanBeCreated_WithTable()
    {
        var model = CreateSimpleModel();
        var table = model.GetTableFromDbName("Users");

        var resolver = new DbTableBatchResolver(table);

        resolver.Should().NotBeNull();
    }

    [Fact]
    public void TableMetadata_BatchMaxSize_IsReadable()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithMetadata("batch-max-size", "50"))
            .Build();

        var table = model.GetTableFromDbName("Users");

        table.GetMetadataValue("batch-max-size").Should().Be("50");
    }

    [Fact]
    public void TableMetadata_BatchMaxSize_ReturnsNull_WhenNotSet()
    {
        var model = CreateSimpleModel();
        var table = model.GetTableFromDbName("Users");

        table.GetMetadataValue("batch-max-size").Should().BeNull();
    }

    #endregion

    #region Schema Text Action Types Tests

    [Fact]
    public void SchemaText_BatchInputType_ReferencesAllActionTypes()
    {
        var model = CreateSimpleModel();
        var schemaText = GetSchemaText(model);

        var batchSection = ExtractInputType(schemaText, "batch_Users");

        batchSection.Should().Contain("insert:");
        batchSection.Should().Contain("update:");
        batchSection.Should().Contain("upsert:");
        batchSection.Should().Contain("delete:");
    }

    [Fact]
    public void SchemaText_BatchFieldAppearsInMutationType()
    {
        var model = CreateSimpleModel();
        var schemaText = GetSchemaText(model);

        var mutSection = ExtractTypeBlock(schemaText, "type databaseInput");

        mutSection.Should().Contain("Users_batch");
    }

    [Fact]
    public void SchemaText_BatchFieldReturnType_IsInt()
    {
        var model = CreateSimpleModel();
        var schemaText = GetSchemaText(model);

        schemaText.Should().Contain("Users_batch(actions: [batch_Users!]!) : Int");
    }

    #endregion

    #region Batch Input Type Nullable Fields

    [Fact]
    public void SchemaText_BatchInputFields_AreNullable()
    {
        var model = CreateSimpleModel();
        var schemaText = GetSchemaText(model);
        var batchSection = ExtractInputType(schemaText, "batch_Users");

        // Each action field should be nullable (no ! suffix) since only one per action is used
        batchSection.Should().Contain("insert: Insert_Users");
        batchSection.Should().NotContain("insert: Insert_Users!");
        batchSection.Should().Contain("delete: Delete_Users");
        batchSection.Should().NotContain("delete: Delete_Users!");
    }

    #endregion

    #region Multiple Table Schema Tests

    [Fact]
    public void SchemaText_EachTable_HasOwnBatchInputType()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var schemaText = GetSchemaText(model);

        schemaText.Should().Contain("input batch_Users {");
        schemaText.Should().Contain("input batch_Orders {");
    }

    [Fact]
    public void SchemaText_EachTable_HasOwnBatchField()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var schemaText = GetSchemaText(model);

        schemaText.Should().Contain("Users_batch(actions: [batch_Users!]!) : Int");
        schemaText.Should().Contain("Orders_batch(actions: [batch_Orders!]!) : Int");
    }

    #endregion

    #region Helper Methods

    private static IDbModel CreateSimpleModel()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar"))
            .Build();
    }

    private static string ExtractInputType(string schemaText, string typeName)
    {
        var startMarker = $"input {typeName} {{";
        var startIndex = schemaText.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0) return string.Empty;
        var endIndex = schemaText.IndexOf("}", startIndex);
        if (endIndex < 0) return string.Empty;
        return schemaText.Substring(startIndex, endIndex - startIndex + 1);
    }

    private static string ExtractTypeBlock(string schemaText, string typeDecl)
    {
        var startIndex = schemaText.IndexOf(typeDecl, StringComparison.Ordinal);
        if (startIndex < 0) return string.Empty;
        var endIndex = schemaText.IndexOf("}", startIndex);
        if (endIndex < 0) return string.Empty;
        return schemaText.Substring(startIndex, endIndex - startIndex + 1);
    }

    #endregion
}
