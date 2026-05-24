using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.QueryModel.VisualQuery;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Tests for <see cref="FkAutoJoin.Resolve"/>: single-FK, composite-FK, no-FK,
/// ambiguous (multiple FK paths), and direction independence.
/// </summary>
public sealed class FkAutoJoinTests
{
    private static IDbModel SingleFkModel() => DbModelTestFixture.Create()
        .WithTable("users", t => t.WithSchema("dbo").WithPrimaryKey("id", "int"))
        .WithTable("orders", t => t.WithSchema("dbo").WithPrimaryKey("id", "int").WithColumn("user_id", "int"))
        .WithForeignKey("FK_orders_users", "orders", "user_id", "users", "id")
        .Build();

    private static IDbModel CompositeFkModel() => DbModelTestFixture.Create()
        .WithTable("parent", t => t.WithSchema("dbo")
            .WithColumn("tenant_id", "int", isPrimaryKey: true)
            .WithColumn("id", "int", isPrimaryKey: true))
        .WithTable("child", t => t.WithSchema("dbo")
            .WithPrimaryKey("id", "int")
            .WithColumn("tenant_id", "int")
            .WithColumn("parent_id", "int"))
        .WithForeignKey("FK_child_parent",
            "dbo", "child", new[] { "tenant_id", "parent_id" },
            "dbo", "parent", new[] { "tenant_id", "id" })
        .Build();

    // Ambiguous = the two tables reference each other (orders -> users AND
    // users -> orders), so there are two distinct FK paths to choose from.
    // (Two FKs to the *same* parent collapse in the model's per-parent
    // SingleLinks map, so that flavor of ambiguity isn't representable here.)
    private static IDbModel BidirectionalModel() => DbModelTestFixture.Create()
        .WithTable("users", t => t.WithSchema("dbo")
            .WithPrimaryKey("id", "int")
            .WithColumn("last_order_id", "int"))
        .WithTable("orders", t => t.WithSchema("dbo")
            .WithPrimaryKey("id", "int")
            .WithColumn("user_id", "int"))
        .WithForeignKey("FK_orders_users", "orders", "user_id", "users", "id")
        .WithForeignKey("FK_users_orders", "users", "last_order_id", "orders", "id")
        .Build();

    [Fact]
    public void SingleFk_ReturnsOneCandidate()
    {
        var join = FkAutoJoin.Resolve(SingleFkModel(), "dbo.orders", "dbo.users").Should().ContainSingle().Subject;

        join.LeftTable.Should().Be("dbo.orders");
        join.LeftColumns.Should().Equal("user_id");
        join.RightTable.Should().Be("dbo.users");
        join.RightColumns.Should().Equal("id");
        join.Type.Should().Be(VisualJoinType.Inner);
    }

    [Fact]
    public void SingleFk_IsDirectionIndependent()
    {
        var forward = FkAutoJoin.Resolve(SingleFkModel(), "dbo.orders", "dbo.users");
        var reverse = FkAutoJoin.Resolve(SingleFkModel(), "dbo.users", "dbo.orders");

        forward.Should().ContainSingle();
        reverse.Should().ContainSingle();
        // Same FK either way: child=orders references parent=users.
        reverse[0].LeftTable.Should().Be("dbo.orders");
        reverse[0].RightTable.Should().Be("dbo.users");
    }

    [Fact]
    public void CompositeFk_ReturnsParallelColumnArrays()
    {
        var join = FkAutoJoin.Resolve(CompositeFkModel(), "dbo.child", "dbo.parent").Should().ContainSingle().Subject;

        join.LeftTable.Should().Be("dbo.child");
        join.LeftColumns.Should().Equal("tenant_id", "parent_id");
        join.RightTable.Should().Be("dbo.parent");
        join.RightColumns.Should().Equal("tenant_id", "id");
    }

    [Fact]
    public void NoRelationship_ReturnsEmpty()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("alpha", t => t.WithSchema("dbo").WithPrimaryKey("id", "int"))
            .WithTable("beta", t => t.WithSchema("dbo").WithPrimaryKey("id", "int"))
            .Build();

        FkAutoJoin.Resolve(model, "dbo.alpha", "dbo.beta").Should().BeEmpty();
    }

    [Fact]
    public void MultipleFkPaths_ReturnsAllCandidates()
    {
        var candidates = FkAutoJoin.Resolve(BidirectionalModel(), "dbo.orders", "dbo.users");

        // Two distinct FK paths: orders->users and users->orders.
        candidates.Should().HaveCount(2);
        candidates.Should().Contain(c => c.LeftTable == "dbo.orders" && c.RightTable == "dbo.users"
            && c.LeftColumns.SequenceEqual(new[] { "user_id" }));
        candidates.Should().Contain(c => c.LeftTable == "dbo.users" && c.RightTable == "dbo.orders"
            && c.LeftColumns.SequenceEqual(new[] { "last_order_id" }));
    }
}
