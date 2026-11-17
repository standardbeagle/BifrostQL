using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// The schema generator derives type and root-field names from each table's GraphQL name
/// (<c>&lt;t&gt;_aggregate</c>, <c>&lt;t&gt;_batch</c>, <c>&lt;t&gt;Aggregate</c>, …). A second
/// table whose own name is one of those (a rollup table <c>orders_aggregate</c>) generated the
/// same name twice, and the schema build failed with no table named. Model load now refuses
/// the pair and names both tables.
/// </summary>
public sealed class GeneratedNameCollisionTests
{
    private static IDbModel TwoTables(string first, string second) =>
        DbModelTestFixture.Create()
            .WithTable(first, t => t.WithSchema("dbo").WithPrimaryKey("id").WithColumn("total", "int"))
            .WithTable(second, t => t.WithSchema("dbo").WithPrimaryKey("id").WithColumn("total", "int"))
            .Build();

    [Theory]
    // type orders_aggregate: the aggregate row type of orders and the table type of orders_aggregate.
    [InlineData("orders", "orders_aggregate", "orders_aggregate")]
    // type posts_paged: the paged wrapper of posts and the table type of posts_paged.
    [InlineData("posts", "posts_paged", "posts_paged")]
    // mutation field posts_batch: the batch field of posts and the table mutation of posts_batch.
    [InlineData("posts", "posts_batch", "posts_batch")]
    // query field ordersAggregate: the aggregate root field of orders and the table field of ordersAggregate.
    [InlineData("orders", "ordersAggregate", "ordersAggregate")]
    public void Validate_TableNamedLikeAnotherTablesGeneratedName_Throws(string first, string second, string name)
    {
        var act = () => ModelConfigValidator.Validate(TwoTables(first, second));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain($"'{name}'")
            .And.Contain($"dbo.{first}")
            .And.Contain($"dbo.{second}");
    }

    [Fact]
    public void Validate_TablesWhoseGeneratedNamesDiffer_DoesNotThrow()
    {
        var act = () => ModelConfigValidator.Validate(TwoTables("orders", "order_lines"));

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_SameNameInTwoSchemas_DoesNotThrow()
    {
        // Two same-named tables in two schemas are deduplicated into distinct GraphQL names
        // before validation, so their generated names never meet.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t.WithSchema("dbo").WithPrimaryKey("id"))
            .WithTable("orders", t => t.WithSchema("sales").WithPrimaryKey("id"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }
}
