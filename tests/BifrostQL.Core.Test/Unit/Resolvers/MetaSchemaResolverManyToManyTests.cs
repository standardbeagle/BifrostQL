using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;

namespace BifrostQL.Core.Test.Unit.Resolvers;

/// <summary>
/// Pins the many-to-many projection emitted by the _dbSchema resolver. The
/// edit-db UI consumes this to recognise a junction relationship, skip to the
/// target entity, and reveal the junction payload on demand.
/// </summary>
public sealed class MetaSchemaResolverManyToManyTests
{
    private static JsonElement ResolveTable(IDbModel model, string graphQlName)
    {
        var resolver = new MetaSchemaResolver(model);
        var result = resolver.ResolveAsync(new NullArgContext()).AsTask().GetAwaiter().GetResult();

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(result, options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .First(t => t.GetProperty("graphQlName").GetString() == graphQlName)
            .Clone();
    }

    [Fact]
    public void Projection_PureJunction_EmitsManyToManyJoin()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .WithTable("Roles", t => t.WithPrimaryKey("Id").WithColumn("Label", "nvarchar"))
            .WithTable("UserRoles", t => t
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int"))
            .WithSingleLink("UserRoles", "UserId", "Users", "Id", "Users")
            .WithSingleLink("UserRoles", "RoleId", "Roles", "Id", "Roles")
            .WithMultiLink("Users", "Id", "UserRoles", "UserId", "UserRoles")
            .WithManyToManyLink("Users", "Id", "UserRoles", "UserId", "RoleId", "Roles", "Id")
            .Build();

        var users = ResolveTable(model, "Users");

        var m2m = users.GetProperty("manyToManyJoins");
        m2m.GetArrayLength().Should().Be(1);

        var link = m2m[0];
        link.GetProperty("targetTable").GetString().Should().Be("Roles");
        link.GetProperty("junctionTable").GetString().Should().Be("UserRoles");
        link.GetProperty("junctionTargetField").GetString().Should().Be("Roles");
        link.GetProperty("sourceColumnNames").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Id");
        link.GetProperty("junctionSourceColumnNames").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("UserId");
        link.GetProperty("junctionTargetColumnNames").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("RoleId");
        link.GetProperty("targetColumnNames").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Id");
        link.GetProperty("hasPayload").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Projection_PayloadJunction_FlagsHasPayload()
    {
        // A payload junction is opted in explicitly via many-to-many metadata
        // (auto-detection deliberately skips junctions that carry real columns).
        var model = DbModelTestFixture.Create()
            .WithTable("Students", t => t.WithSchema("dbo").WithPrimaryKey("Id").WithColumn("Name", "nvarchar")
                .WithMetadata("many-to-many", "Courses:Enrollments"))
            .WithTable("Courses", t => t.WithSchema("dbo").WithPrimaryKey("Id").WithColumn("Title", "nvarchar"))
            .WithTable("Enrollments", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("StudentId", "int")
                .WithColumn("CourseId", "int")
                .WithColumn("Grade", "nvarchar"))
            .WithForeignKey("FK_Enroll_Students", "Enrollments", "StudentId", "Students", "Id")
            .WithForeignKey("FK_Enroll_Courses", "Enrollments", "CourseId", "Courses", "Id")
            .Build();

        var students = ResolveTable(model, "Students");
        var m2m = students.GetProperty("manyToManyJoins");
        m2m.GetArrayLength().Should().Be(1);
        m2m[0].GetProperty("targetTable").GetString().Should().Be("Courses");
        m2m[0].GetProperty("hasPayload").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Projection_NoManyToMany_EmitsEmptyArray()
    {
        var model = StandardTestFixtures.UsersWithOrders();

        var users = ResolveTable(model, "Users");
        users.GetProperty("manyToManyJoins").GetArrayLength().Should().Be(0);
    }

    /// <summary>Minimal context: the resolver only reads the graphQlName argument.</summary>
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
