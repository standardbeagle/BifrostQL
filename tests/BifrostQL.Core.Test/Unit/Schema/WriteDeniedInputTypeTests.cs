using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Schema;

/// <summary>
/// RED/GREEN TDD coverage for the E19 half of S4a: a column that is write-denied
/// for EVERY caller (unconditional <c>policy-write-deny</c>, no roles) leaves the
/// table's insert/update input types — a NOT NULL computed-style column must not
/// make the table un-insertable. A grant-conditional column (write-deny qualified
/// by <c>policy-write-deny-roles</c>, or gated by <c>write-requires</c>) stays in
/// the shared input type but becomes optional there.
/// </summary>
public sealed class WriteDeniedInputTypeTests
{
    private static string? InputLine(string sdl, string column) =>
        sdl.Split('\n').SingleOrDefault(l => l.TrimStart().StartsWith($"{column} :"))?.Trim();

    private static IDbModel ModelWithDeniedTotal(params (string key, string value)[] extraMetadata)
    {
        var builder = DbModelTestFixture.Create()
            .WithTable("orders", t =>
            {
                t.WithSchema("public")
                    .WithPrimaryKey("id")
                    .WithColumn("total", "decimal", isNullable: false)
                    .WithColumn("name", "nvarchar", isNullable: false)
                    .WithMetadata(MetadataKeys.Policy.Actions, "read,create,update,delete")
                    .WithMetadata(MetadataKeys.Policy.WriteDeny, "total");
                foreach (var (key, value) in extraMetadata)
                    t.WithMetadata(key, value);
            });
        return builder.Build();
    }

    [Fact]
    public void InsertInput_UnconditionalWriteDeniedNotNullColumn_IsRemoved()
    {
        var model = ModelWithDeniedTotal();
        var orders = model.Tables.Single(t => t.GraphQlName == "orders");

        var sdl = new TableSchemaGenerator(orders).GetMutationParameterType(MutateActions.Insert, IdentityType.None);

        InputLine(sdl, "total").Should().BeNull(
            "the column is write-denied for every caller, so no caller can ever supply it; sdl:\n{0}", sdl);
        InputLine(sdl, "name").Should().NotBeNull().And.EndWith("!",
            "an unrelated NOT NULL column stays required (control: the input type still has required fields)");
    }

    [Fact]
    public void UpdateInput_UnconditionalWriteDeniedColumn_IsRemoved()
    {
        var model = ModelWithDeniedTotal();
        var orders = model.Tables.Single(t => t.GraphQlName == "orders");

        var sdl = new TableSchemaGenerator(orders).GetMutationParameterType(MutateActions.Update, IdentityType.Required);

        InputLine(sdl, "total").Should().BeNull("sdl:\n{0}", sdl);
    }

    [Fact]
    public void InsertInput_RoleQualifiedWriteDeniedColumn_StaysButOptional()
    {
        var model = ModelWithDeniedTotal(
            (MetadataKeys.Policy.WriteDenyRoles, "member"));
        var orders = model.Tables.Single(t => t.GraphQlName == "orders");

        var sdl = new TableSchemaGenerator(orders).GetMutationParameterType(MutateActions.Insert, IdentityType.None);

        InputLine(sdl, "total").Should().NotBeNull(
            "the deny is qualified to the member role; the shared input type serves accounting too; sdl:\n{0}", sdl)
            .And.NotEndWith("!",
                "but a member must be able to omit it, so it is optional for every caller");
    }

    [Fact]
    public void InsertInput_WriteRequiresColumn_StaysButOptional()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("users", t => t
                .WithSchema("public")
                .WithPrimaryKey("id")
                .WithColumn("cost_rate", "decimal", isNullable: false)
                .WithColumn("name")
                .WithMetadata(MetadataKeys.Policy.Actions, "read,create,update,delete")
                .WithColumnMetadata("cost_rate", MetadataKeys.Policy.WriteRequires, "team.manage"))
            .Build();
        var users = model.Tables.Single(t => t.GraphQlName == "users");

        var sdl = new TableSchemaGenerator(users).GetMutationParameterType(MutateActions.Insert, IdentityType.None);

        InputLine(sdl, "cost_rate").Should().NotBeNull(
            "a grant-conditional column stays in the shared input type; sdl:\n{0}", sdl)
            .And.NotEndWith("!",
                "a caller without the grant must be able to omit it");
    }

    [Fact]
    public void UpdateInput_WriteRequiresColumn_StaysButOptional()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("users", t => t
                .WithSchema("public")
                .WithPrimaryKey("id")
                .WithColumn("cost_rate", "decimal", isNullable: false)
                .WithColumn("name")
                .WithMetadata(MetadataKeys.Policy.Actions, "read,create,update,delete")
                .WithColumnMetadata("cost_rate", MetadataKeys.Policy.WriteRequires, "team.manage"))
            .Build();
        var users = model.Tables.Single(t => t.GraphQlName == "users");

        var sdl = new TableSchemaGenerator(users).GetMutationParameterType(MutateActions.Update, IdentityType.Required);

        InputLine(sdl, "cost_rate").Should().NotBeNull().And.NotEndWith("!");
    }

    [Fact]
    public void InsertInput_NoPolicyMetadata_NotNullColumnStaysRequired()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("public")
                .WithPrimaryKey("id")
                .WithColumn("total", "decimal", isNullable: false))
            .Build();
        var orders = model.Tables.Single(t => t.GraphQlName == "orders");

        var sdl = new TableSchemaGenerator(orders).GetMutationParameterType(MutateActions.Insert, IdentityType.None);

        InputLine(sdl, "total").Should().NotBeNull().And.EndWith("!",
            "absent-policy default: no write deny, nothing leaves the input type");
    }
}
