using System.Reflection;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Schema;

/// <summary>
/// The filtered set-update surface is OPT-IN per table: only a table carrying
/// `filtered-update: enabled` gets the `updateWhere:` argument and its `_set` /
/// `_update_where` input types — every other table's SDL is unchanged, so the dangerous
/// surface cannot be probed where it was never enabled.
/// </summary>
public sealed class FilteredUpdateSchemaTests
{
    private static readonly MethodInfo SchemaTextFromModelMethod = typeof(DbSchema).Assembly
        .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
        .GetMethod("SchemaTextFromModel", BindingFlags.Static | BindingFlags.Public)!;

    private static string GetSchemaText(IDbModel model)
        => (string)SchemaTextFromModelMethod.Invoke(null, new object[] { model, true })!;

    private static IDbModel BuildModel(bool optIn)
        => DbModelTestFixture.Create()
            .WithTable("Orders", t =>
            {
                t.WithPrimaryKey("Id")
                    .WithColumn("Status", "nvarchar")
                    .WithColumn("Total", "decimal");
                if (optIn)
                    t.WithMetadata("filtered-update", "enabled");
            })
            .WithTable("Plain", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();

    [Fact]
    public void OptedInTable_GetsUpdateWhereArgumentAndInputTypes()
    {
        var schemaText = GetSchemaText(BuildModel(optIn: true));

        schemaText.Should().Contain("updateWhere: Orders_update_where");
        schemaText.Should().Contain("input Orders_update_where {");
        schemaText.Should().Contain("set: Orders_set!");
        schemaText.Should().Contain("where: TableFilterOrdersInput!", "the where grammar IS the read-side filter type");
        schemaText.Should().Contain("input Orders_set {");
    }

    [Fact]
    public void SetInputType_ExcludesPrimaryKeyColumns()
    {
        var schemaText = GetSchemaText(BuildModel(optIn: true));

        var start = schemaText.IndexOf("input Orders_set {", StringComparison.Ordinal);
        start.Should().BePositive();
        var block = schemaText.Substring(start, schemaText.IndexOf('}', start) - start);
        block.Should().Contain("Status");
        block.Should().Contain("Total");
        block.Should().NotContain("Id :", "the set fieldset can never move a row's identity");
        // All columns optional: a set-update writes only the fields supplied.
        block.Should().NotContain("!");
    }

    [Fact]
    public void TableWithoutOptIn_HasNoFilteredUpdateSurface()
    {
        var schemaText = GetSchemaText(BuildModel(optIn: false));

        schemaText.Should().NotContain("updateWhere:");
        schemaText.Should().NotContain("Orders_update_where");
        schemaText.Should().NotContain("Orders_set");
    }

    [Fact]
    public void OptIn_DoesNotLeakOntoOtherTables()
    {
        var schemaText = GetSchemaText(BuildModel(optIn: true));

        schemaText.Should().NotContain("Plain_update_where");
        schemaText.Should().NotContain("Plain_set");
    }
}
