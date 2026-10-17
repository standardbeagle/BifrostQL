using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Schema;

/// <summary>
/// Requiredness of the per-table mutation input types:
/// <list type="bullet">
/// <item>update — an UPDATE sets only the columns it is given, so only the primary
/// key is required (a partial update is the normal case);</item>
/// <item>insert — a column is required only when it is NOT NULL and the database
/// cannot fill it (no default; identity and computed columns are not inputs);</item>
/// <item>upsert — the key stays required, the rest follows the insert rule.</item>
/// </list>
/// The fixture spans each shape: NOT NULL without a default, NOT NULL with a
/// default, nullable, and a composite key (both key columns must stay required).
/// </summary>
public sealed class MutationInputRequirednessTests
{
    private static string Field(string sdl, string column) =>
        sdl.Split('\n').Single(l => l.TrimStart().StartsWith($"{column} :")).Trim();

    private static IDbTable Orders() => DbModelTestFixture.Create()
        .WithTable("orders", t => t
            .WithSchema("public")
            .WithColumn("tenant", "int", isPrimaryKey: true)
            .WithColumn("number", "int", isPrimaryKey: true)
            .WithColumn("customer", "nvarchar", isNullable: false)
            .WithColumn("status", "nvarchar", isNullable: false, hasDefault: true)
            .WithColumn("note", "nvarchar", isNullable: true))
        .Build()
        .Tables.Single(t => t.GraphQlName == "orders");

    private static string Sdl(MutateActions action, IdentityType identity) =>
        new TableSchemaGenerator(Orders()).GetMutationParameterType(action, identity);

    [Fact]
    public void UpdateInput_NotNullNonKeyColumn_IsOptional()
    {
        var sdl = Sdl(MutateActions.Update, IdentityType.Required);

        Field(sdl, "customer").Should().Be("customer : String", "a partial update need not resend it; sdl:\n{0}", sdl);
        Field(sdl, "status").Should().Be("status : String");
        Field(sdl, "note").Should().Be("note : String");
    }

    [Fact]
    public void UpdateInput_EveryPrimaryKeyColumn_StaysRequired()
    {
        var sdl = Sdl(MutateActions.Update, IdentityType.Required);

        Field(sdl, "tenant").Should().Be("tenant : Int!", "the key addresses the row; sdl:\n{0}", sdl);
        Field(sdl, "number").Should().Be("number : Int!");
    }

    [Fact]
    public void InsertInput_NotNullWithoutDefault_IsRequired()
    {
        var sdl = Sdl(MutateActions.Insert, IdentityType.None);

        Field(sdl, "customer").Should().Be("customer : String!", "nothing can fill it; sdl:\n{0}", sdl);
        Field(sdl, "tenant").Should().Be("tenant : Int!");
        Field(sdl, "number").Should().Be("number : Int!");
    }

    [Fact]
    public void InsertInput_NotNullWithDefault_IsOptional()
    {
        var sdl = Sdl(MutateActions.Insert, IdentityType.None);

        Field(sdl, "status").Should().Be("status : String", "the database default fills it; sdl:\n{0}", sdl);
        Field(sdl, "note").Should().Be("note : String");
    }

    [Fact]
    public void UpsertInput_FollowsTheInsertRule_AndKeepsTheKeyRequired()
    {
        var sdl = Sdl(MutateActions.Upsert, IdentityType.Optional);

        Field(sdl, "customer").Should().Be("customer : String!", "an upsert may insert; sdl:\n{0}", sdl);
        Field(sdl, "status").Should().Be("status : String");
        Field(sdl, "note").Should().Be("note : String");
        Field(sdl, "tenant").Should().Be("tenant : Int!");
        Field(sdl, "number").Should().Be("number : Int!");
    }

    [Fact]
    public void UpsertInput_KeyWithDefault_StaysRequired()
    {
        var table = DbModelTestFixture.Create()
            .WithTable("tokens", t => t
                .WithSchema("public")
                .WithColumn("id", "uniqueidentifier", isPrimaryKey: true, hasDefault: true)
                .WithColumn("label", "nvarchar", isNullable: false))
            .Build()
            .Tables.Single(t => t.GraphQlName == "tokens");
        var generator = new TableSchemaGenerator(table);

        var upsert = generator.GetMutationParameterType(MutateActions.Upsert, IdentityType.Optional);
        var insert = generator.GetMutationParameterType(MutateActions.Insert, IdentityType.None);

        Field(upsert, "id").Should().EndWith("!", "upsert key handling is unchanged; sdl:\n{0}", upsert);
        Field(insert, "id").Should().NotEndWith("!", "an insert may let the database generate the key; sdl:\n{0}", insert);
    }
}
