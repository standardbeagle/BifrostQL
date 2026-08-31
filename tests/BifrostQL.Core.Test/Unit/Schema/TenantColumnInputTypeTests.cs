using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// TenantMutationTransformer pins the tenant-filter column server-side on every
/// INSERT/UPDATE, so mutation input types must not require the client to supply
/// it — even when the column is NOT NULL in the database.
/// </summary>
public class TenantColumnInputTypeTests
{
    private static string InputLine(string sdl, string column)
    {
        var line = sdl.Split('\n').SingleOrDefault(l => l.TrimStart().StartsWith($"{column} :"));
        line.Should().NotBeNull($"input SDL should contain a '{column}' field; sdl:\n{sdl}");
        return line!.Trim();
    }

    [Fact]
    public void InsertInput_TenantFilterColumn_IsOptionalDespiteNotNull()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithPrimaryKey("id")
                .WithColumn("tenant_id", "bigint", isNullable: false)
                .WithColumn("name")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id"))
            .Build();
        var orders = model.Tables.Single(t => t.GraphQlName == "orders");

        var sdl = new TableSchemaGenerator(orders).GetMutationParameterType(MutateActions.Insert, IdentityType.None);

        InputLine(sdl, "tenant_id").Should().NotEndWith("!",
            "the tenant transformer supplies the value; clients must not be forced to send one");
    }

    [Fact]
    public void InsertInput_NotNullColumn_StaysRequiredWithoutTenantFilter()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithPrimaryKey("id")
                .WithColumn("tenant_id", "bigint", isNullable: false)
                .WithColumn("name"))
            .Build();
        var orders = model.Tables.Single(t => t.GraphQlName == "orders");

        var sdl = new TableSchemaGenerator(orders).GetMutationParameterType(MutateActions.Insert, IdentityType.None);

        InputLine(sdl, "tenant_id").Should().EndWith("!",
            "only the declared tenant-filter column is exempt from NOT NULL requiredness");
    }
}
