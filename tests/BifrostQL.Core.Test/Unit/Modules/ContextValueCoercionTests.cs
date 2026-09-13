using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;

namespace BifrostQL.Core.Test.Modules;

public sealed class ContextValueCoercionTests
{
    [Theory]
    [InlineData("42")]
    [MemberData(nameof(ContextValues))]
    public void TenantFilter_CoercesContextValueToBigint(object value)
    {
        var model = TenantModel();
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = new Dictionary<string, object?> { ["tenant_id"] = value },
            QueryType = QueryType.Standard
        };

        var filter = new TenantFilterTransformer().GetAdditionalFilter(model.GetTableFromDbName("Orders"), context);

        filter!.Next!.Value.Should().BeOfType<long>().Which.Should().Be(42L);
    }

    [Theory]
    [InlineData("42")]
    [MemberData(nameof(ContextValues))]
    public async Task TenantMutation_CoercesFilterAndInsertPinToBigint(object value)
    {
        var model = TenantModel();
        var table = model.GetTableFromDbName("Orders");
        var context = new MutationTransformContext
        {
            Model = model,
            UserContext = new Dictionary<string, object?> { ["tenant_id"] = value }
        };
        var transformer = new TenantMutationTransformer();

        var update = await transformer.TransformAsync(table, MutationType.Update,
            new Dictionary<string, object?>(), context);
        var insert = await transformer.TransformAsync(table, MutationType.Insert,
            new Dictionary<string, object?>(), context);

        update.AdditionalFilter!.Next!.Value.Should().BeOfType<long>().Which.Should().Be(42L);
        insert.Data["account_id"].Should().BeOfType<long>().Which.Should().Be(42L);
    }

    [Fact]
    public void RowScope_CoercesContextValueToBigint()
    {
        var table = TenantModel().GetTableFromDbName("Orders");

        var filter = RowScopeCompiler.Compile(
            "account_id = {user_id}", table,
            new Dictionary<string, object?> { ["user_id"] = "7" });

        filter.Next!.Value.Should().BeOfType<long>().Which.Should().Be(7L);
    }

    [Theory]
    [InlineData("abc")]
    [MemberData(nameof(InvalidContextValues))]
    public void InvalidContextValue_FailsClosed(object value)
    {
        var model = TenantModel();
        var table = model.GetTableFromDbName("Orders");
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = new Dictionary<string, object?> { ["tenant_id"] = value },
            QueryType = QueryType.Standard
        };

        var exception = Assert.Throws<BifrostExecutionError>(() =>
            new TenantFilterTransformer().GetAdditionalFilter(table, context));

        exception.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
    }

    [Fact]
    public void GuidContextValue_CoercesToGuid()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", table => table
                .WithPrimaryKey("id")
                .WithColumn("id", "int")
                .WithColumn("account_id", "uuid")
                .WithMetadata("tenant-filter", "account_id"))
            .Build();
        var expected = Guid.NewGuid();
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = new Dictionary<string, object?> { ["tenant_id"] = expected.ToString() },
            QueryType = QueryType.Standard
        };

        var filter = new TenantFilterTransformer().GetAdditionalFilter(model.GetTableFromDbName("Orders"), context);

        filter!.Next!.Value.Should().BeOfType<Guid>().Which.Should().Be(expected);
    }

    [Fact]
    public void InvalidGuidContextValue_FailsClosed()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", table => table
                .WithPrimaryKey("id")
                .WithColumn("id", "int")
                .WithColumn("account_id", "uuid")
                .WithMetadata("tenant-filter", "account_id"))
            .Build();
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = new Dictionary<string, object?> { ["tenant_id"] = "not-a-guid" },
            QueryType = QueryType.Standard
        };

        var exception = Assert.Throws<BifrostExecutionError>(() =>
            new TenantFilterTransformer().GetAdditionalFilter(model.GetTableFromDbName("Orders"), context));

        exception.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
    }

    private static IDbModel TenantModel() => DbModelTestFixture.Create()
        .WithTable("Orders", table => table
            .WithPrimaryKey("id")
            .WithColumn("id", "int")
            .WithColumn("account_id", "bigint")
            .WithMetadata("tenant-filter", "account_id"))
        .Build();

    public static IEnumerable<object[]> ContextValues => new[]
    {
        new object[] { "42" },
        new object[] { new[] { "42" } }
    };

    public static IEnumerable<object[]> InvalidContextValues => new[]
    {
        new object[] { "abc" },
        new object[] { new[] { "1", "2" } }
    };
}
