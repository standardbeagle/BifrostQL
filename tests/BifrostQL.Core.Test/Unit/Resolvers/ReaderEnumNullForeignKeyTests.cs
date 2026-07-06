using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// A NULL foreign-key component on a parent row is ordinary, valid data — the
/// parent simply references nothing on that link. It must NOT fail the whole
/// query (as the former <c>throw BifrostExecutionError("key value is null")</c>
/// did): a single-link resolves to null, a multi-link resolves to an empty
/// collection. The non-null cases also guard the shared per-result-set index
/// that replaced the former O(parents × children) stitch.
/// </summary>
public sealed class ReaderEnumNullForeignKeyTests
{
    private static IDbModel BuildModel() =>
        DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithColumn("id", "int", isPrimaryKey: true)
                .WithColumn("customer_id", "int", isNullable: true))
            .WithTable("customers", t => t
                .WithColumn("id", "int", isPrimaryKey: true)
                .WithColumn("name", "nvarchar"))
            .Build();

    private static IBifrostFieldContext FieldContext(string fieldName, string? alias = null)
    {
        var ctx = Substitute.For<IBifrostFieldContext>();
        ctx.FieldName.Returns(fieldName);
        ctx.FieldAlias.Returns(alias);
        return ctx;
    }

    /// <summary>
    /// Builds a root ReaderEnum over an `orders` parent with two rows —
    /// row 0 customer_id=5 (a real FK), row 1 customer_id=null — joined to a
    /// `customers` result set via <paramref name="queryType"/>.
    /// </summary>
    private static (ReaderEnum reader, TableJoin join) BuildReader(QueryType queryType)
    {
        var model = BuildModel();
        var orders = model.GetTableFromDbName("orders");
        var customers = model.GetTableFromDbName("customers");

        var parentForFrom = GqlObjectQueryBuilder.Create().WithDbTable(orders).Build();
        var childQuery = GqlObjectQueryBuilder.Create()
            .WithDbTable(customers)
            .WithColumns("id", "name")
            .Build();

        var rootQuery = GqlObjectQueryBuilder.Create()
            .WithDbTable(orders)
            .WithColumns("id", "customer_id")
            .WithJoin(b => b
                .WithName("customer")
                .WithFromColumn("customer_id")
                .WithConnectedColumn("id")
                .WithQueryType(queryType)
                .WithFromTable(parentForFrom)
                .WithConnectedTable(childQuery))
            .Build();

        var join = rootQuery.Joins[0];

        var parentIndex = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = 0,
            ["customer_id"] = 1,
        };
        var parentData = new List<object?[]>
        {
            new object?[] { 1, 5 },
            new object?[] { 2, null },
        };

        // Child result set carries the join-key alias `src_id` (single-column join).
        var childIndex = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = 0,
            ["name"] = 1,
            ["src_id"] = 2,
        };
        var childData = new List<object?[]>
        {
            new object?[] { 5, "Alice", 5 },
            new object?[] { 6, "Alice2", 5 },
        };

        var tables = new Dictionary<string, (IDictionary<string, int> index, IList<object?[]> data)>
        {
            [rootQuery.KeyName] = (parentIndex, parentData),
            [join.JoinName] = (childIndex, childData),
        };

        return (new ReaderEnum(rootQuery, tables), join);
    }

    [Fact]
    public async Task SingleLink_NullForeignKey_ResolvesToNull()
    {
        var (reader, _) = BuildReader(QueryType.Single);

        var result = await reader.Get(1, FieldContext("customer"));

        result.Should().BeNull();
    }

    [Fact]
    public async Task SingleLink_NonNullForeignKey_ResolvesMatchingRow()
    {
        var (reader, _) = BuildReader(QueryType.Single);

        var result = await reader.Get(0, FieldContext("customer"));

        result.Should().BeOfType<SingleRowLookup>();
        var nested = await ((SingleRowLookup)result!).Get(FieldContext("name"));
        nested.Should().Be("Alice");
    }

    [Fact]
    public async Task MultiLink_NullForeignKey_ResolvesToEmptyCollection()
    {
        var (reader, _) = BuildReader(QueryType.Join);

        var result = await reader.Get(1, FieldContext("customer"));

        result.Should().BeAssignableTo<IEnumerable<object?>>();
        ((IEnumerable<object?>)result!).Cast<object?>().Should().BeEmpty();
    }

    [Fact]
    public async Task MultiLink_NonNullForeignKey_ReturnsMatchingRows()
    {
        var (reader, _) = BuildReader(QueryType.Join);

        var result = await reader.Get(0, FieldContext("customer"));

        result.Should().BeAssignableTo<IEnumerable<object?>>();
        // Two child rows carry src_id = 5; both match the parent FK.
        ((IEnumerable<object?>)result!).Cast<object?>().Should().HaveCount(2);
    }
}
