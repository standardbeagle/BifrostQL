using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using GraphQL.Types;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Schema;

/// <summary>
/// <c>:root { mutation-forms: … }</c> narrows every table's generated mutation surface to the
/// listed forms: the mutation field carries exactly those arguments, <c>&lt;table&gt;_batch</c>
/// exists only when <c>batch</c> is listed, and an unlisted form's input type is not in the
/// schema. Each fact builds the real schema (<see cref="DbSchema.FromModel"/>), so the resolver
/// wiring must agree with the emitted SDL or the build throws.
/// </summary>
public sealed class MutationFormsSchemaTests
{
    private static IDbModel Model(string? forms)
    {
        var fixture = DbModelTestFixture.Create();
        if (forms is not null)
            fixture = fixture.WithModelMetadata(MetadataKeys.Model.MutationForms, forms);
        return fixture
            .WithTable("orders", t => t
                .WithSchema("public")
                .WithPrimaryKey("id")
                .WithColumn("total", "int", isNullable: false))
            .Build();
    }

    private static ISchema Schema(IDbModel model)
    {
        var schema = DbSchema.FromModel(model);
        schema.Initialize();
        return schema;
    }

    private static IEnumerable<string> Arguments(ISchema schema, string field) =>
        schema.Mutation!.GetField(field)!.Arguments!.Select(a => a.Name);

    private static IEnumerable<string> TypeNames(ISchema schema) => schema.AllTypes.Select(t => t.Name);

    [Fact]
    public void ListedForms_AreTheMutationFieldsOnlyArguments()
    {
        var schema = Schema(Model("insert, update, _primaryKey"));

        Arguments(schema, "orders").Should().BeEquivalentTo("insert", "update", "_primaryKey");
        TypeNames(schema).Should().Contain(new[] { "Insert_orders", "Update_orders" });
        TypeNames(schema).Should().NotContain(new[]
        {
            "Upsert_orders", "Delete_orders", "orders_sync_insert", "orders_save", "orders_delta",
        });
    }

    [Fact]
    public void UnlistedBatch_EmitsNoBatchField_AndNoBatchInput()
    {
        var schema = Schema(Model("insert, delete, delta"));

        schema.Mutation!.GetField("orders_batch").Should().BeNull();
        TypeNames(schema).Should().NotContain("batch_orders");
        // The delta carries only the listed row forms.
        var delta = (IInputObjectGraphType)schema.AllTypes["orders_delta"]!;
        delta.Fields.Select(f => f.Name).Should().BeEquivalentTo("inserted", "deleted");
    }

    [Fact]
    public void ListedBatch_EmitsTheBatchField_CarryingOnlyTheListedRowForms()
    {
        var schema = Schema(Model("update, batch"));

        schema.Mutation!.GetField("orders_batch").Should().NotBeNull();
        var batch = (IInputObjectGraphType)schema.AllTypes["batch_orders"]!;
        batch.Fields.Select(f => f.Name).Should().BeEquivalentTo("update");
        TypeNames(schema).Should().NotContain("Insert_orders");
    }

    [Fact]
    public void NoKey_KeepsEveryForm()
    {
        var schema = Schema(Model(null));

        Arguments(schema, "orders").Should().BeEquivalentTo(
            "insert", "update", "upsert", "delete", "sync", "save", "delta", "_primaryKey");
        schema.Mutation!.GetField("orders_batch").Should().NotBeNull();
        TypeNames(schema).Should().Contain(new[]
        {
            "Insert_orders", "Update_orders", "Upsert_orders", "Delete_orders",
            "batch_orders", "orders_sync_insert", "orders_save", "orders_delta",
        });
    }

    [Theory]
    [InlineData("insert, patch", "'patch' is not a mutation form")]
    [InlineData("_primaryKey", "names no write form")]
    [InlineData("batch, sync", "'batch' carries the listed row forms")]
    [InlineData("delta, upsert", "'delta' carries the listed row forms")]
    public void MalformedList_IsRefusedAtModelLoad(string forms, string reason)
    {
        var act = () => ModelConfigValidator.Validate(Model(forms));

        act.Should().Throw<Exception>().WithMessage($"*{MetadataKeys.Model.MutationForms}*{reason}*");
    }

    [Fact]
    public void WellFormedList_IsAcceptedAtModelLoad()
    {
        var act = () => ModelConfigValidator.Validate(Model("insert, update, delete, batch"));

        act.Should().NotThrow();
    }
}
