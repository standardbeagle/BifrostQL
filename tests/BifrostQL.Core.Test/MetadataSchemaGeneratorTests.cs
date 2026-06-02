using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Pins the metadata-introspection schema contract consumed by the edit-db
/// client. Regression guard for the dbJoinSchema.fieldName gap: the resolver
/// (MetaSchemaResolver) emits a `fieldName` on every join, but the generated
/// SDL once omitted the field, so introspection rejected the client's query
/// ("Cannot query field 'fieldName' on type 'dbJoinSchema'") and the editor
/// failed to render any tables.
/// </summary>
public class MetadataSchemaGeneratorTests
{
    private static string JoinSchemaBlock()
    {
        var sdl = MetadataSchemaGenerator.Generate();
        var start = sdl.IndexOf("type dbJoinSchema {", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "dbJoinSchema type should be generated");
        var end = sdl.IndexOf('}', start);
        end.Should().BeGreaterThan(start);
        return sdl.Substring(start, end - start);
    }

    [Fact]
    public void DbJoinSchema_ExposesFieldName()
    {
        // edit-db queries join.fieldName; it must be declared (non-null, the
        // resolver always supplies ChildFieldName/ParentFieldName).
        JoinSchemaBlock().Should().Contain("fieldName: String!");
    }

    [Theory]
    [InlineData("name: String!")]
    [InlineData("fieldName: String!")]
    [InlineData("sourceColumnNames: [String!]!")]
    [InlineData("destinationTable: String!")]
    [InlineData("destinationColumnNames: [String!]!")]
    public void DbJoinSchema_DeclaresExpectedFields(string field)
    {
        JoinSchemaBlock().Should().Contain(field);
    }

    [Fact]
    public void DbTableSchema_ExposesManyToManyJoins()
    {
        // Without this field GraphQL.NET silently drops the resolver's
        // manyToManyJoins, so the edit-db client never learns a relationship is
        // a junction and the skip-the-junction UI cannot engage.
        var sdl = MetadataSchemaGenerator.Generate();
        sdl.Should().Contain("manyToManyJoins: [dbManyToManyJoinSchema!]!");
    }

    private static string ManyToManySchemaBlock()
    {
        var sdl = MetadataSchemaGenerator.Generate();
        var start = sdl.IndexOf("type dbManyToManyJoinSchema {", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "dbManyToManyJoinSchema type should be generated");
        var end = sdl.IndexOf('}', start);
        end.Should().BeGreaterThan(start);
        return sdl.Substring(start, end - start);
    }

    [Theory]
    [InlineData("targetTable: String!")]
    [InlineData("junctionTable: String!")]
    [InlineData("junctionTargetField: String!")]
    [InlineData("sourceColumnNames: [String!]!")]
    [InlineData("junctionSourceColumnNames: [String!]!")]
    [InlineData("junctionTargetColumnNames: [String!]!")]
    [InlineData("targetColumnNames: [String!]!")]
    [InlineData("hasPayload: Boolean!")]
    public void DbManyToManyJoinSchema_DeclaresExpectedFields(string field)
    {
        ManyToManySchemaBlock().Should().Contain(field);
    }
}
