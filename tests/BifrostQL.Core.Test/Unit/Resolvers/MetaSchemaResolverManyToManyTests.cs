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
    private static JsonElement ResolveTable(IDbModel model, string graphQlName, IDictionary<string, object?>? userContext = null)
    {
        var resolver = new MetaSchemaResolver(model);
        var context = new NullArgContext { UserContext = userContext ?? new Dictionary<string, object?>() };
        var result = resolver.ResolveAsync(context).AsTask().GetAwaiter().GetResult();

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

    /// <summary>
    /// A bridge names one column on each of its three tables (four in all). When the caller's
    /// policy hides any of them, publishing the bridge would disclose the hidden column's name,
    /// so the bridge is dropped, the same rule single and multi joins follow. The empty denial
    /// is the control: the same caller sees the bridge when every column is readable.
    /// </summary>
    [Theory]
    [InlineData(null, null, 1)]
    [InlineData("Users", "Id", 0)]
    [InlineData("UserRoles", "UserId", 0)]
    [InlineData("UserRoles", "RoleId", 0)]
    [InlineData("Roles", "Id", 0)]
    public void Projection_BridgeColumnHiddenByPolicy_DropsTheBridge(string? deniedTable, string? deniedColumn, int expected)
    {
        DbModelTestFixture.TableBuilder Policy(DbModelTestFixture.TableBuilder t, string table)
        {
            t.WithMetadata(MetadataKeys.Policy.Actions, "read");
            return table == deniedTable ? t.WithMetadata(MetadataKeys.Policy.ReadDeny, deniedColumn!) : t;
        }

        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => Policy(t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"), "Users"))
            .WithTable("Roles", t => Policy(t.WithPrimaryKey("Id").WithColumn("Label", "nvarchar"), "Roles"))
            .WithTable("UserRoles", t => Policy(t
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int"), "UserRoles"))
            .WithSingleLink("UserRoles", "UserId", "Users", "Id", "Users")
            .WithSingleLink("UserRoles", "RoleId", "Roles", "Id", "Roles")
            .WithMultiLink("Users", "Id", "UserRoles", "UserId", "UserRoles")
            .WithManyToManyLink("Users", "Id", "UserRoles", "UserId", "RoleId", "Roles", "Id")
            .Build();
        var member = new Dictionary<string, object?>
        {
            [MetadataKeys.Auth.DefaultUserIdContextKey] = "u1",
            [MetadataKeys.Auth.DefaultRolesContextKey] = new[] { "member" },
        };

        var users = ResolveTable(model, "Users", member);

        users.GetProperty("manyToManyJoins").GetArrayLength().Should().Be(expected);
    }

    /// <summary>Minimal context: the resolver only reads the graphQlName argument.</summary>
    private sealed class NullArgContext : IBifrostFieldContext
    {
        public string FieldName => "_dbSchema";
        public string? FieldAlias => null;
        public object? Source => null;
        public IReadOnlyList<object> Path => Array.Empty<object>();
        public IDictionary<string, object?> UserContext { get; init; } = new Dictionary<string, object?>();
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
