using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// End-to-end proof for S4b read-side column grants with masking (E14/E15/E16).
/// A column gated by <c>read-requires</c> MASKS to null by default: a member's
/// selection is a 200 with <c>cost_rate: null</c>, while a holder of
/// <c>rates.view_cost</c> reads the value. The mask never becomes an oracle: the
/// same column is still refused as a filter, sort, or <c>_agg</c> input.
/// <c>deny-mode: refuse</c> keeps today's throw, and <c>deny-mode: null</c> on a
/// <c>policy-read-deny</c> column converts the throw into a mask. The mask rides
/// <see cref="IQueryIntentExecutor"/>, so the protocol adapters inherit it.
/// </summary>
public sealed class ColumnMaskLeakTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_column_mask_leak_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    private static readonly string[] Rules =
    {
        "main.members { policy-actions: read,update }",
        "main.members.cost_rate { read-requires: rates.view_cost }",
        "main.members.hourly_rate { read-requires: rates.view_cost; deny-mode: refuse }",
        "main.secrets { policy-actions: read; policy-read-deny: token; deny-mode: null }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS members");
        await Exec("DROP TABLE IF EXISTS secrets");
        await Exec(
            """
            CREATE TABLE members (
                id INTEGER PRIMARY KEY,
                display_name TEXT NOT NULL,
                cost_rate REAL NOT NULL,
                hourly_rate REAL NOT NULL
            )
            """);
        await Exec("CREATE TABLE secrets (id INTEGER PRIMARY KEY, token TEXT NOT NULL)");
        await Exec("INSERT INTO members (id, display_name, cost_rate, hourly_rate) VALUES (1, 'ada', 250.5, 120.0)");
        await Exec("INSERT INTO secrets (id, token) VALUES (1, 'sekrit')");

        var factory = new SqliteDbConnFactory(ConnString);
        _model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private static IFilterTransformers ReadTransformers() => new FilterTransformersWrap
    {
        Transformers = new IFilterTransformer[] { new PolicyFilterTransformer() },
    };

    private async Task<ExecutionResult> QueryAsync(string query, params string[] roles)
    {
        var schema = DbSchema.FromModel(_model);
        var transformerService = new QueryTransformerService(ReadTransformers());
        var services = new ServiceCollection();
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>
            {
                ["user_id"] = "user-1",
                ["roles"] = roles,
            };
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, transformerService),
            });
        });
    }

    private async Task<ExecutionResult> MutateAsync(string mutation, params string[] roles)
    {
        var schema = DbSchema.FromModel(_model);
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = Array.Empty<IMutationTransformer>(),
        });
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>
            {
                ["user_id"] = "user-1",
                ["roles"] = roles,
            };
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, NullQueryTransformerService.Instance),
            });
        });
    }

    private static JsonDocument Serialize(ExecutionResult result) =>
        JsonDocument.Parse(new GraphQLSerializer().Serialize(result));

    private QueryIntentExecutor BuildIntentExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, () => Task.FromResult(new Inputs(new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["dbSchema"] = DbSchema.FromModel(_model),
            ["connFactory"] = new SqliteDbConnFactory(ConnString),
        })));
        return new QueryIntentExecutor(
            pathCache, new QueryTransformerService(ReadTransformers()));
    }

    // ---- Selection masking ----

    [Fact]
    public async Task Member_SelectMaskedColumn_Gets200WithNull()
    {
        var result = await QueryAsync(
            "{ members { data { id display_name cost_rate } } }", "member");

        result.Errors.Should().BeNullOrEmpty();
        using var doc = Serialize(result);
        var row = doc.RootElement.GetProperty("data").GetProperty("members").GetProperty("data")[0];
        row.GetProperty("display_name").GetString().Should().Be("ada");
        row.GetProperty("cost_rate").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GrantHolder_SelectMaskedColumn_GetsValue()
    {
        var result = await QueryAsync(
            "{ members { data { id cost_rate } } }", "member", "rates.view_cost");

        result.Errors.Should().BeNullOrEmpty();
        using var doc = Serialize(result);
        doc.RootElement.GetProperty("data").GetProperty("members").GetProperty("data")[0]
            .GetProperty("cost_rate").GetDouble().Should().Be(250.5);
    }

    // ---- No oracle: filter / sort / aggregate still refuse ----

    [Fact]
    public async Task Member_FilterOnMaskedColumn_AccessDenied()
    {
        var result = await QueryAsync(
            "{ members(filter: { cost_rate: { _eq: 250.5 } }) { data { id } } }", "member");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors![0].Message.Should().Contain("not permitted by authorization policy");
    }

    [Fact]
    public async Task Member_SortOnMaskedColumn_AccessDenied()
    {
        var result = await QueryAsync(
            "{ members(sort: [cost_rate_asc]) { data { id } } }", "member");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors![0].Message.Should().Contain("not permitted by authorization policy");
    }

    [Fact]
    public async Task Member_AggregateOnMaskedColumn_AccessDenied()
    {
        var result = await QueryAsync(
            "{ membersAggregate(groupBy: [display_name]) { display_name _sum { cost_rate } } }", "member");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors![0].Message.Should().Contain("not permitted by authorization policy");
    }

    // ---- deny-mode ----

    [Fact]
    public async Task DenyModeRefuse_Select_ThrowsLikeToday()
    {
        var result = await QueryAsync(
            "{ members { data { id hourly_rate } } }", "member");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors![0].Message.Should().Contain("not permitted by authorization policy");
    }

    [Fact]
    public async Task DenyModeRefuse_GrantHolder_GetsValue()
    {
        var result = await QueryAsync(
            "{ members { data { id hourly_rate } } }", "rates.view_cost");

        result.Errors.Should().BeNullOrEmpty();
        using var doc = Serialize(result);
        doc.RootElement.GetProperty("data").GetProperty("members").GetProperty("data")[0]
            .GetProperty("hourly_rate").GetDouble().Should().Be(120.0);
    }

    [Fact]
    public async Task PolicyReadDeny_DenyModeNull_MasksInsteadOfThrowing()
    {
        var result = await QueryAsync(
            "{ secrets { data { id token } } }", "member");

        result.Errors.Should().BeNullOrEmpty();
        using var doc = Serialize(result);
        doc.RootElement.GetProperty("data").GetProperty("secrets").GetProperty("data")[0]
            .GetProperty("token").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- E15: mutation persists; the read-back shows the mask ----

    [Fact]
    public async Task Member_UpdatePersists_ReadBackShowsMask()
    {
        var mutation = await MutateAsync(
            "mutation { members(update: { id: 1, display_name: \"zed\", cost_rate: 250.5, hourly_rate: 120.0 }) }", "member");
        mutation.Errors.Should().BeNullOrEmpty();

        // The write persisted (read straight from SQLite, no projection involved).
        await using var cmd = new SqliteCommand(
            "SELECT display_name FROM members WHERE id = 1", _keepAlive);
        (await cmd.ExecuteScalarAsync()).Should().Be("zed");

        var result = await QueryAsync(
            "{ members { data { id display_name cost_rate } } }", "member");
        result.Errors.Should().BeNullOrEmpty();
        using var doc = Serialize(result);
        var row = doc.RootElement.GetProperty("data").GetProperty("members").GetProperty("data")[0];
        row.GetProperty("display_name").GetString().Should().Be("zed");
        row.GetProperty("cost_rate").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- E14: the mask rides IQueryIntentExecutor (adapters inherit it) ----

    [Fact]
    public async Task IntentExecutor_Member_MaskedColumnIsNull()
    {
        var executor = BuildIntentExecutor();
        var model = await executor.GetModelAsync(EndpointPath);
        var members = model.GetTableFromDbName("members");

        var result = await executor.ExecuteAsync(new QueryIntent
        {
            Query = new GqlObjectQuery
            {
                DbTable = members,
                SchemaName = members.TableSchema,
                TableName = members.DbName,
                GraphQlName = members.GraphQlName,
                Path = members.GraphQlName,
                ScalarColumns = { new GqlObjectColumn("id"), new GqlObjectColumn("cost_rate") },
            },
            UserContext = new Dictionary<string, object?> { ["roles"] = new[] { "member" } },
            Endpoint = EndpointPath,
        });

        result.Rows.Should().ContainSingle();
        result.Rows[0].Should().ContainKey("cost_rate")
            .WhoseValue.Should().BeNull();
    }

    [Fact]
    public async Task IntentExecutor_Member_FilterOnMaskedColumn_Rejected()
    {
        var executor = BuildIntentExecutor();
        var model = await executor.GetModelAsync(EndpointPath);
        var members = model.GetTableFromDbName("members");

        var act = () => executor.ExecuteAsync(new QueryIntent
        {
            Query = new GqlObjectQuery
            {
                DbTable = members,
                SchemaName = members.TableSchema,
                TableName = members.DbName,
                GraphQlName = members.GraphQlName,
                Path = members.GraphQlName,
                ScalarColumns = { new GqlObjectColumn("id") },
                Filter = TableFilter.FromObject(
                    new Dictionary<string, object?>
                    {
                        ["cost_rate"] = new Dictionary<string, object?> { ["_eq"] = 250.5 },
                    },
                    members),
            },
            UserContext = new Dictionary<string, object?> { ["roles"] = new[] { "member" } },
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>()
            .WithMessage("*not permitted by authorization policy*");
    }

    [Fact]
    public async Task IntentExecutor_GrantHolder_GetsValue()
    {
        var executor = BuildIntentExecutor();
        var model = await executor.GetModelAsync(EndpointPath);
        var members = model.GetTableFromDbName("members");

        var result = await executor.ExecuteAsync(new QueryIntent
        {
            Query = new GqlObjectQuery
            {
                DbTable = members,
                SchemaName = members.TableSchema,
                TableName = members.DbName,
                GraphQlName = members.GraphQlName,
                Path = members.GraphQlName,
                ScalarColumns = { new GqlObjectColumn("id"), new GqlObjectColumn("cost_rate") },
            },
            UserContext = new Dictionary<string, object?> { ["roles"] = new[] { "rates.view_cost" } },
            Endpoint = EndpointPath,
        });

        result.Rows.Should().ContainSingle();
        Convert.ToDouble(result.Rows[0]["cost_rate"]).Should().Be(250.5);
    }
}
