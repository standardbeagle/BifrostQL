using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Schema;

/// <summary>
/// S8: the <c>_dbSchema</c> meta-resolver answers PER CALLER. The model is projected
/// through <see cref="SchemaReadVisibility"/> (a denied table is absent), each table
/// carries <c>allowedActions</c> resolved via <see cref="PolicyEvaluator.CanAct"/>, each
/// column carries <c>readable</c>/<c>writable</c>, and the raw metadata bag (which leaks
/// <c>policy-*</c> rules) is served to admin callers only. <c>_grants</c> returns the
/// caller's own grant set; <c>_policyGrants</c> returns the catalogue of grant names the
/// model's policy metadata references.
/// </summary>
public sealed class DbSchemaProjectionTests
{
    /// <summary>
    /// members: read/create unconditional, update/delete behind the `manager` grant;
    /// cost_rate is read-gated behind `rates.view_cost` (masked by default — still
    /// listed, readable: false). ledger: read behind the `boss` grant, so a member
    /// cannot see the table at all while an admin bypasses the grant.
    /// </summary>
    private static IDbModel Model() => DbModelTestFixture.Create()
        .WithTable("members", t => t
            .WithSchema("dbo")
            .WithPrimaryKey("id")
            .WithColumn("name")
            .WithColumn("cost_rate", "decimal", graphQlName: "costRate")
            .WithColumnMetadata("cost_rate", MetadataKeys.Policy.ReadRequires, "rates.view_cost")
            .WithMetadata(MetadataKeys.Policy.Actions, "read, create, update[manager], delete[manager]"))
        .WithTable("ledger", t => t
            .WithSchema("dbo")
            .WithPrimaryKey("id")
            .WithColumn("amount", "decimal")
            .WithMetadata(MetadataKeys.Policy.Actions, "read[boss], update[boss]"))
        .Build();

    private static IDictionary<string, object?> Ctx(
        string userId, string[] roles, string[]? permissions = null) =>
        new Dictionary<string, object?>
        {
            [MetadataKeys.Auth.DefaultUserIdContextKey] = userId,
            [MetadataKeys.Auth.DefaultRolesContextKey] = roles,
            [MetadataKeys.Auth.DefaultPermissionsContextKey] = permissions ?? Array.Empty<string>(),
        };

    private static readonly string[] MemberRoles = { "member" };
    private static readonly string[] ManagerRoles = { "manager", "rates.view_cost" };

    private static JsonElement Resolve(IDbModel model, IDictionary<string, object?> ctx)
    {
        var result = new MetaSchemaResolver(model)
            .ResolveAsync(new StubContext(ctx)).AsTask().GetAwaiter().GetResult();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, options));
        return doc.RootElement.Clone();
    }

    private static JsonElement Table(JsonElement root, string graphQlName) =>
        root.EnumerateArray().First(t => t.GetProperty("graphQlName").GetString() == graphQlName);

    private static JsonElement Column(JsonElement table, string graphQlName) =>
        table.GetProperty("columns").EnumerateArray()
            .First(c => c.GetProperty("graphQlName").GetString() == graphQlName);

    [Fact]
    public void Member_gets_unconditional_actions_only_and_masked_column_is_not_readable()
    {
        var members = Table(Resolve(Model(), Ctx("u1", MemberRoles)), "members");

        members.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString())
            .Should().Equal(new[] { "read", "create" },
                "update/delete sit behind the manager grant the member does not hold");

        var costRate = Column(members, "costRate");
        costRate.GetProperty("readable").GetBoolean().Should().BeFalse(
            "read-requires: rates.view_cost is unmet — the column is masked to null");
        costRate.GetProperty("writable").GetBoolean().Should().BeTrue(
            "no write-requires or write-deny gates the column");
        Column(members, "name").GetProperty("readable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Manager_gets_all_actions_and_reads_the_gated_column()
    {
        var members = Table(Resolve(Model(), Ctx("u2", ManagerRoles)), "members");

        members.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString())
            .Should().Equal("read", "create", "update", "delete");
        Column(members, "costRate").GetProperty("readable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void A_denied_table_is_absent_for_the_member_and_present_for_admin()
    {
        var member = Resolve(Model(), Ctx("u1", MemberRoles));
        member.EnumerateArray().Select(t => t.GetProperty("graphQlName").GetString())
            .Should().NotContain("ledger", "read sits behind the boss grant");

        var admin = Resolve(Model(), Ctx("root", new[] { MetadataKeys.Policy.DefaultAdminRole }));
        admin.EnumerateArray().Select(t => t.GetProperty("graphQlName").GetString())
            .Should().Contain("ledger", "the admin bypass covers the grant requirement");
    }

    [Fact]
    public void The_non_admin_body_never_contains_a_policy_key()
    {
        var json = Resolve(Model(), Ctx("u1", MemberRoles)).GetRawText();
        json.Should().NotContain("policy-",
            "the raw metadata bag is served to admin callers only");
    }

    [Fact]
    public void The_admin_body_keeps_the_raw_metadata()
    {
        var members = Table(
            Resolve(Model(), Ctx("root", new[] { MetadataKeys.Policy.DefaultAdminRole })), "members");
        // The wire shape is [dbMetadataSchema!]!; raw JSON serialization of the
        // dictionary projects it as an object — the keys are what matters here.
        members.GetProperty("metadata").EnumerateObject()
            .Select(kv => kv.Name)
            .Should().Contain("policy-actions", "admin keeps the raw metadata bag");
    }

    [Fact]
    public async Task Grants_returns_the_union_of_roles_and_permissions()
    {
        var grants = await new CallerGrantsResolver()
            .ResolveAsync(new StubContext(Ctx("u1", MemberRoles, new[] { "perm.a" })));

        grants.Should().BeAssignableTo<IEnumerable<string>>()
            .Subject.Should().BeEquivalentTo(new[] { "member", "perm.a" });
    }

    [Fact]
    public async Task PolicyGrants_returns_the_referenced_grant_catalogue_sorted_and_deduped()
    {
        var grants = await new PolicyGrantCatalogueResolver(Model())
            .ResolveAsync(new StubContext(Ctx("u1", MemberRoles)));

        grants.Should().BeAssignableTo<IEnumerable<string>>()
            .Subject.Should().Equal("boss", "manager", "rates.view_cost");
    }

    [Fact]
    public void The_schema_declares_the_caller_projected_fields_and_root_fields()
    {
        var sdl = BifrostQL.Core.Schema.MetadataSchemaGenerator.Generate();
        sdl.Should().Contain("allowedActions: [String!]!");
        sdl.Should().Contain("readable: Boolean!");
        sdl.Should().Contain("writable: Boolean!");

        var schema = BifrostQL.Core.Schema.SchemaGenerator.SchemaTextFromModel(Model());
        schema.Should().Contain("_grants: [String!]!");
        schema.Should().Contain("_policyGrants: [String!]!");
    }

    private sealed class StubContext : IBifrostFieldContext
    {
        private readonly IDictionary<string, object?> _userContext;
        public StubContext(IDictionary<string, object?> userContext) => _userContext = userContext;
        public string FieldName => "_dbSchema";
        public string? FieldAlias => null;
        public object? Source => null;
        public IReadOnlyList<object> Path => Array.Empty<object>();
        public IDictionary<string, object?> UserContext => _userContext;
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
