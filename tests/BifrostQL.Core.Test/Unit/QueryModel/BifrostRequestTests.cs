using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

public sealed class BifrostQueryIntentTests
{
    [Fact]
    public void Default_HasEmptyCollections()
    {
        var intent = new BifrostQueryIntent();

        intent.RequestType.Should().Be(BifrostRequestType.Query);
        intent.Table.Should().BeNull();
        intent.Alias.Should().BeNull();
        intent.Filter.Should().BeNull();
        intent.Fields.Should().BeEmpty();
        intent.Arguments.Should().BeEmpty();
        intent.Joins.Should().BeEmpty();
    }

    [Fact]
    public void WithAllProperties_RoundTrips()
    {
        var filter = TableFilter.FromObject(
            new Dictionary<string, object?> { { "Id", new Dictionary<string, object?> { { "_eq", 1 } } } },
            "Users");

        var nested = new BifrostQueryIntent
        {
            RequestType = BifrostRequestType.Query,
            Table = "Orders",
            Fields = new[] { "Id", "Total" },
        };

        var intent = new BifrostQueryIntent
        {
            RequestType = BifrostRequestType.Mutation,
            Table = "Users",
            Alias = "u",
            Filter = filter,
            Fields = new[] { "Id", "Name", "Email" },
            Arguments = new Dictionary<string, object?> { { "limit", 10 }, { "offset", 0 } },
            Joins = new[] { nested },
        };

        intent.RequestType.Should().Be(BifrostRequestType.Mutation);
        intent.Table.Should().Be("Users");
        intent.Alias.Should().Be("u");
        intent.Filter.Should().BeSameAs(filter);
        intent.Fields.Should().HaveCount(3);
        intent.Fields[0].Should().Be("Id");
        intent.Arguments.Should().ContainKey("limit").WhoseValue.Should().Be(10);
        intent.Joins.Should().HaveCount(1);
        intent.Joins[0].Table.Should().Be("Orders");
    }

    [Fact]
    public void SubscriptionRequestType_IsAvailable()
    {
        var intent = new BifrostQueryIntent
        {
            RequestType = BifrostRequestType.Subscription,
            Table = "events",
        };

        intent.RequestType.Should().Be(BifrostRequestType.Subscription);
    }
}

public sealed class BifrostRequestAdapterTests
{
    [Fact]
    public void FromQueryField_ScalarFields_ExtractsFieldNames()
    {
        var queryField = new QueryField
        {
            Name = "Users",
            Fields = new List<IQueryField>
            {
                new QueryField { Name = "Id" },
                new QueryField { Name = "Name" },
                new QueryField { Name = "Email" },
            },
        };

        var result = BifrostRequestAdapter.FromQueryField(queryField, BifrostRequestType.Query);

        result.Table.Should().Be("Users");
        result.RequestType.Should().Be(BifrostRequestType.Query);
        result.Fields.Should().BeEquivalentTo(new[] { "Id", "Name", "Email" });
        result.Joins.Should().BeEmpty();
    }

    [Fact]
    public void FromQueryField_WithAlias_PreservesAlias()
    {
        var queryField = new QueryField
        {
            Name = "Users",
            Alias = "allUsers",
            Fields = new List<IQueryField>
            {
                new QueryField { Name = "Id" },
            },
        };

        var result = BifrostRequestAdapter.FromQueryField(queryField, BifrostRequestType.Query);

        result.Table.Should().Be("Users");
        result.Alias.Should().Be("allUsers");
    }

    [Fact]
    public void FromQueryField_WithArguments_ExtractsArguments()
    {
        var queryField = new QueryField
        {
            Name = "Users",
            Arguments = new List<QueryArgument>
            {
                new QueryArgument { Name = "limit", Value = 10 },
                new QueryArgument { Name = "offset", Value = 5 },
                new QueryArgument { Name = "sort", Value = new List<object?> { "Name_asc" } },
            },
            Fields = new List<IQueryField>
            {
                new QueryField { Name = "Id" },
            },
        };

        var result = BifrostRequestAdapter.FromQueryField(queryField, BifrostRequestType.Query);

        result.Arguments.Should().HaveCount(3);
        result.Arguments["limit"].Should().Be(10);
        result.Arguments["offset"].Should().Be(5);
    }

    [Fact]
    public void FromQueryField_WithJoinSubFields_CreatesNestedJoins()
    {
        // _join_Orders is a join-type field (starts with _join_)
        var queryField = new QueryField
        {
            Name = "Users",
            Fields = new List<IQueryField>
            {
                new QueryField { Name = "Id" },
                new QueryField
                {
                    Name = "_join_Orders",
                    Arguments = new List<QueryArgument>
                    {
                        new QueryArgument { Name = "on", Value = new Dictionary<string, object?> { { "UserId", new Dictionary<string, object?> { { "_eq", "Id" } } } } },
                    },
                    Fields = new List<IQueryField>
                    {
                        new QueryField { Name = "Id" },
                        new QueryField { Name = "Total" },
                    },
                },
            },
        };

        var result = BifrostRequestAdapter.FromQueryField(queryField, BifrostRequestType.Query);

        result.Fields.Should().ContainSingle().Which.Should().Be("Id");
        result.Joins.Should().ContainSingle();
        result.Joins[0].Table.Should().Be("_join_Orders");
        result.Joins[0].Fields.Should().BeEquivalentTo(new[] { "Id", "Total" });
    }

    [Fact]
    public void FromQueryField_WithLinkSubFields_CreatesNestedJoins()
    {
        // Link fields have sub-fields but don't start with special prefixes
        var queryField = new QueryField
        {
            Name = "Users",
            Fields = new List<IQueryField>
            {
                new QueryField { Name = "Id" },
                new QueryField
                {
                    Name = "orders",
                    Fields = new List<IQueryField>
                    {
                        new QueryField { Name = "Id" },
                        new QueryField { Name = "Total" },
                    },
                },
            },
        };

        var result = BifrostRequestAdapter.FromQueryField(queryField, BifrostRequestType.Query);

        result.Fields.Should().ContainSingle().Which.Should().Be("Id");
        result.Joins.Should().ContainSingle();
        result.Joins[0].Table.Should().Be("orders");
        result.Joins[0].Fields.Should().BeEquivalentTo(new[] { "Id", "Total" });
    }

    [Fact]
    public void FromQueryFields_MultipleFields_ConvertsAll()
    {
        var fields = new List<IQueryField>
        {
            new QueryField
            {
                Name = "Users",
                Fields = new List<IQueryField> { new QueryField { Name = "Id" } },
            },
            new QueryField
            {
                Name = "Orders",
                Fields = new List<IQueryField> { new QueryField { Name = "Id" } },
            },
        };

        var result = BifrostRequestAdapter.FromQueryFields(fields, BifrostRequestType.Query);

        result.Should().HaveCount(2);
        result[0].Table.Should().Be("Users");
        result[1].Table.Should().Be("Orders");
    }

    [Fact]
    public void FromQueryField_MutationType_SetsRequestType()
    {
        var queryField = new QueryField
        {
            Name = "Users",
            Fields = new List<IQueryField> { new QueryField { Name = "Id" } },
        };

        var result = BifrostRequestAdapter.FromQueryField(queryField, BifrostRequestType.Mutation);

        result.RequestType.Should().Be(BifrostRequestType.Mutation);
    }
}

public sealed class BifrostRequestRoundTripTests
{
    [Fact]
    public void ToQueryField_ScalarFields_PreservesNames()
    {
        var intent = new BifrostQueryIntent
        {
            Table = "Users",
            Fields = new[] { "Id", "Name", "Email" },
        };

        var queryField = BifrostRequestAdapter.ToQueryField(intent);

        queryField.Name.Should().Be("Users");
        queryField.Fields.Should().HaveCount(3);
        queryField.Fields[0].Name.Should().Be("Id");
        queryField.Fields[1].Name.Should().Be("Name");
        queryField.Fields[2].Name.Should().Be("Email");
    }

    [Fact]
    public void ToQueryField_WithAlias_PreservesAlias()
    {
        var intent = new BifrostQueryIntent
        {
            Table = "Users",
            Alias = "allUsers",
            Fields = new[] { "Id" },
        };

        var queryField = BifrostRequestAdapter.ToQueryField(intent);

        queryField.Name.Should().Be("Users");
        queryField.Alias.Should().Be("allUsers");
    }

    [Fact]
    public void ToQueryField_WithArguments_ConvertsToQueryArguments()
    {
        var intent = new BifrostQueryIntent
        {
            Table = "Users",
            Arguments = new Dictionary<string, object?> { { "limit", 10 }, { "offset", 5 } },
            Fields = new[] { "Id" },
        };

        var queryField = BifrostRequestAdapter.ToQueryField(intent);

        queryField.Arguments.Should().HaveCount(2);
        queryField.Arguments[0].Name.Should().Be("limit");
        queryField.Arguments[0].Value.Should().Be(10);
        queryField.Arguments[1].Name.Should().Be("offset");
        queryField.Arguments[1].Value.Should().Be(5);
    }

    [Fact]
    public void ToQueryField_WithNestedJoins_CreatesSubFields()
    {
        var intent = new BifrostQueryIntent
        {
            Table = "Users",
            Fields = new[] { "Id" },
            Joins = new IBifrostRequest[]
            {
                new BifrostQueryIntent
                {
                    Table = "Orders",
                    Fields = new[] { "Id", "Total" },
                },
            },
        };

        var queryField = BifrostRequestAdapter.ToQueryField(intent);

        // 1 scalar (Id) + 1 join (Orders)
        queryField.Fields.Should().HaveCount(2);
        queryField.Fields[0].Name.Should().Be("Id");
        queryField.Fields[1].Name.Should().Be("Orders");
        queryField.Fields[1].Fields.Should().HaveCount(2);
    }

    [Fact]
    public void RoundTrip_FromQueryFieldThenBack_PreservesStructure()
    {
        var original = new QueryField
        {
            Name = "Users",
            Alias = "allUsers",
            Arguments = new List<QueryArgument>
            {
                new QueryArgument { Name = "limit", Value = 10 },
            },
            Fields = new List<IQueryField>
            {
                new QueryField { Name = "Id" },
                new QueryField { Name = "Name" },
            },
        };

        var intent = BifrostRequestAdapter.FromQueryField(original, BifrostRequestType.Query);
        var roundTripped = BifrostRequestAdapter.ToQueryField(intent);

        roundTripped.Name.Should().Be("Users");
        roundTripped.Alias.Should().Be("allUsers");
        roundTripped.Arguments.Should().ContainSingle().Which.Name.Should().Be("limit");
        roundTripped.Fields.Should().HaveCount(2);
        roundTripped.Fields[0].Name.Should().Be("Id");
        roundTripped.Fields[1].Name.Should().Be("Name");
    }
}

public sealed class BifrostDispatcherRequestTests
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    [Fact]
    public void ToObjectQueries_SimpleQuery_ProducesGqlObjectQuery()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var dispatcher = new BifrostDispatcher(model);

        var requests = new IBifrostRequest[]
        {
            new BifrostQueryIntent
            {
                RequestType = BifrostRequestType.Query,
                Table = "Users",
                Fields = new[] { "Id", "Name" },
            },
        };

        var queries = dispatcher.ToObjectQueries(requests);

        queries.Should().ContainSingle();
        queries[0].TableName.Should().Be("Users");
        queries[0].GraphQlName.Should().Be("Users");
        queries[0].ScalarColumns.Should().HaveCount(2);
    }

    [Fact]
    public void ToObjectQueries_WithAlias_PreservesAlias()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var dispatcher = new BifrostDispatcher(model);

        var requests = new IBifrostRequest[]
        {
            new BifrostQueryIntent
            {
                RequestType = BifrostRequestType.Query,
                Table = "Users",
                Alias = "allUsers",
                Fields = new[] { "Id" },
            },
        };

        var queries = dispatcher.ToObjectQueries(requests);

        queries.Should().ContainSingle();
        queries[0].Alias.Should().Be("allUsers");
    }

    [Fact]
    public void ToObjectQueries_MultipleRequests_ProducesMultipleQueries()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var dispatcher = new BifrostDispatcher(model);

        var requests = new IBifrostRequest[]
        {
            new BifrostQueryIntent
            {
                RequestType = BifrostRequestType.Query,
                Table = "Users",
                Fields = new[] { "Id", "Name" },
            },
            new BifrostQueryIntent
            {
                RequestType = BifrostRequestType.Query,
                Table = "Orders",
                Fields = new[] { "Id", "Total" },
            },
        };

        var queries = dispatcher.ToObjectQueries(requests);

        queries.Should().HaveCount(2);
        queries[0].TableName.Should().Be("Users");
        queries[1].TableName.Should().Be("Orders");
    }

    [Fact]
    public void ToObjectQueries_WithLimitOffset_PassesArguments()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var dispatcher = new BifrostDispatcher(model);

        var requests = new IBifrostRequest[]
        {
            new BifrostQueryIntent
            {
                RequestType = BifrostRequestType.Query,
                Table = "Users",
                Fields = new[] { "Id" },
                Arguments = new Dictionary<string, object?>
                {
                    { "limit", 10 },
                    { "offset", 20 },
                },
            },
        };

        var queries = dispatcher.ToObjectQueries(requests);

        queries.Should().ContainSingle();
        queries[0].Limit.Should().Be(10);
        queries[0].Offset.Should().Be(20);
    }

    [Fact]
    public void ToObjectQueries_WithSort_PassesSortArgument()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var dispatcher = new BifrostDispatcher(model);

        var requests = new IBifrostRequest[]
        {
            new BifrostQueryIntent
            {
                RequestType = BifrostRequestType.Query,
                Table = "Users",
                Fields = new[] { "Id", "Name" },
                Arguments = new Dictionary<string, object?>
                {
                    { "sort", new List<object?> { "Name_asc" } },
                },
            },
        };

        var queries = dispatcher.ToObjectQueries(requests);

        queries.Should().ContainSingle();
        queries[0].Sort.Should().ContainSingle().Which.Should().Be("Name_asc");
    }

    [Fact]
    public void ToObjectQueries_ProducesValidSql()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var dispatcher = new BifrostDispatcher(model);

        var requests = new IBifrostRequest[]
        {
            new BifrostQueryIntent
            {
                RequestType = BifrostRequestType.Query,
                Table = "Users",
                Fields = new[] { "Id", "Name" },
            },
        };

        var queries = dispatcher.ToObjectQueries(requests);
        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        queries[0].AddSqlParameterized(model, Dialect, sqls, parameters);

        sqls.Should().ContainKey("Users");
        var sql = sqls["Users"].Sql;
        sql.Should().Contain("SELECT");
        sql.Should().Contain("[Id]");
        sql.Should().Contain("[Name]");
        sql.Should().Contain("FROM [Users]");
    }

    [Fact]
    public void ToObjectQueries_WithPreBuiltFilter_AppliesFilter()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var dispatcher = new BifrostDispatcher(model);

        var filter = TableFilter.FromObject(
            new Dictionary<string, object?> { { "Id", new Dictionary<string, object?> { { "_eq", 42 } } } },
            "Users");

        var requests = new IBifrostRequest[]
        {
            new BifrostQueryIntent
            {
                RequestType = BifrostRequestType.Query,
                Table = "Users",
                Fields = new[] { "Id", "Name" },
                Filter = filter,
            },
        };

        var queries = dispatcher.ToObjectQueries(requests);

        queries.Should().ContainSingle();
        queries[0].Filter.Should().NotBeNull();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        queries[0].AddSqlParameterized(model, Dialect, sqls, parameters);

        var sql = sqls["Users"].Sql;
        sql.Should().Contain("WHERE");
    }

    [Fact]
    public void ToObjectQueries_WithFilterArgAndPreBuiltFilter_MergesFilters()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var dispatcher = new BifrostDispatcher(model);

        var preBuiltFilter = TableFilter.FromObject(
            new Dictionary<string, object?> { { "Name", new Dictionary<string, object?> { { "_eq", "Alice" } } } },
            "Users");

        var requests = new IBifrostRequest[]
        {
            new BifrostQueryIntent
            {
                RequestType = BifrostRequestType.Query,
                Table = "Users",
                Fields = new[] { "Id", "Name" },
                Filter = preBuiltFilter,
                Arguments = new Dictionary<string, object?>
                {
                    { "filter", new Dictionary<string, object?> { { "Id", new Dictionary<string, object?> { { "_eq", 1 } } } } },
                },
            },
        };

        var queries = dispatcher.ToObjectQueries(requests);

        // Both filters should be merged with AND
        queries.Should().ContainSingle();
        queries[0].Filter.Should().NotBeNull();
        queries[0].Filter!.FilterType.Should().Be(FilterType.And);
    }
}

public sealed class BifrostRequestTypeTests
{
    [Fact]
    public void AllRequestTypes_AreDistinct()
    {
        var query = BifrostRequestType.Query;
        var mutation = BifrostRequestType.Mutation;
        var subscription = BifrostRequestType.Subscription;

        query.Should().NotBe(mutation);
        query.Should().NotBe(subscription);
        mutation.Should().NotBe(subscription);
    }

    [Fact]
    public void RequestType_HasExpectedValues()
    {
        ((int)BifrostRequestType.Query).Should().Be(0);
        ((int)BifrostRequestType.Mutation).Should().Be(1);
        ((int)BifrostRequestType.Subscription).Should().Be(2);
    }
}
