using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;

namespace BifrostQL.Core.Test.Unit.Resolvers;

/// <summary>Regression coverage for relationship-discovery provenance in _dbSchema.</summary>
public sealed class MetaSchemaResolverRelationshipKindTests
{
    [Fact]
    public void Projection_DeclaredAndNameBasedLinks_ExposeMatchingProvenanceInBothDirections()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Customers", t => t.WithPrimaryKey("Id"))
            .WithTable("Orders", t => t.WithPrimaryKey("Id").WithColumn("CustomerId", "int"))
            .WithTable("Invoices", t => t.WithPrimaryKey("Id").WithColumn("CustomerId", "int"))
            .Build();

        var foreignKeys = new[]
        {
            new DbForeignKey
            {
                ConstraintName = "FK_Orders_Customers",
                ChildTableSchema = string.Empty,
                ChildTableName = "Orders",
                ChildColumnNames = new[] { "CustomerId" },
                ParentTableSchema = string.Empty,
                ParentTableName = "Customers",
                ParentColumnNames = new[] { "Id" },
            },
        };
        new ForeignKeyRelationshipStrategy().DiscoverRelationships(model, foreignKeys);
        new NameBasedRelationshipStrategy().DiscoverRelationships(model, foreignKeys);

        JoinKinds(model, "Orders", "singleJoins").Should().ContainSingle().Which.Should().Be("foreign-key");
        JoinKinds(model, "Customers", "multiJoins").Should().Contain("foreign-key");
        JoinKinds(model, "Invoices", "singleJoins").Should().ContainSingle().Which.Should().Be("name-based");
        JoinKinds(model, "Customers", "multiJoins").Should().Contain("name-based");
    }

    [Fact]
    public void Projection_PolymorphicLink_RetainsDiscriminatorAndProvenance()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Companies", t => t.WithPrimaryKey("Id"))
            .WithTable("Notes", t => t.WithPrimaryKey("Id")
                .WithColumn("EntityType", "nvarchar")
                .WithColumn("EntityId", "int")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicTypeCol, "EntityType")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicIdCol, "EntityId")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicMap, "company=Companies"))
            .Build();
        new PolymorphicRelationshipStrategy().DiscoverRelationships(model);

        var join = ResolveTable(model, "Companies").GetProperty("multiJoins")[0];
        join.GetProperty("relationshipKind").GetString().Should().Be("polymorphic");
        join.GetProperty("isPolymorphic").GetBoolean().Should().BeTrue();
        join.GetProperty("polymorphicTypeColumn").GetString().Should().Be("EntityType");
        join.GetProperty("polymorphicTypeValue").GetString().Should().Be("company");
    }

    private static IEnumerable<string?> JoinKinds(IDbModel model, string table, string joinProperty) =>
        ResolveTable(model, table).GetProperty(joinProperty).EnumerateArray()
            .Select(join => join.GetProperty("relationshipKind").GetString());

    private static JsonElement ResolveTable(IDbModel model, string graphQlName)
    {
        var result = new MetaSchemaResolver(model).ResolveAsync(new NullArgContext()).AsTask().GetAwaiter().GetResult();
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Single(table => table.GetProperty("graphQlName").GetString() == graphQlName).Clone();
    }

    private sealed class NullArgContext : IBifrostFieldContext
    {
        public string FieldName => "_dbSchema";
        public string? FieldAlias => null;
        public object? Source => null;
        public IReadOnlyList<object> Path => Array.Empty<object>();
        public IDictionary<string, object?> UserContext => new Dictionary<string, object?>();
        public IServiceProvider? RequestServices => null;
        public bool HasSubFields => true;
        public object Document => null!;
        public object Variables => null!;
        public IDictionary<string, object?> InputExtensions => new Dictionary<string, object?>();
        public CancellationToken CancellationToken => CancellationToken.None;
        public bool HasArgument(string name) => false;
        public T? GetArgument<T>(string name) => default;
    }
}
