using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BatchAction = BifrostQL.Core.Resolvers.BatchMutationPipeline.BatchAction;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// The collection-diff save (<c>delta: { inserted, updated, deleted }</c>) — a grid or
/// sync diff sent as one document, flattened onto the batch pipeline: applied in
/// inserted→updated→deleted order inside ONE transaction (a failure anywhere applies
/// nothing), capped by batch-max-size, reply is the total affected count.
/// </summary>
public sealed class DeltaMutationTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_delta_mutation_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS items");
        await Exec("CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)");
        await Exec("INSERT INTO items(id, name, qty) VALUES (1, 'one', 10), (2, 'two', 20), (3, 'three', 30)");
        _model = await new DbModelLoader(
            new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string where)
    {
        await using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM items WHERE {where}", _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<ExecutionResult> ExecuteAsync(string mutation)
    {
        var schema = DbSchema.FromModel(_model);
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap());
        await using var provider = services.BuildServiceProvider();
        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            });
        });
    }

    // ---- binder (pure) ----

    [Fact]
    public void Binder_FlattensInInsertedUpdatedDeletedOrder()
    {
        var actions = DeltaArgumentBinder.Bind(new Dictionary<string, object?>
        {
            ["deleted"] = new List<object?> { new Dictionary<string, object?> { ["id"] = 3 } },
            ["inserted"] = new List<object?>
            {
                new Dictionary<string, object?> { ["name"] = "a" },
                new Dictionary<string, object?> { ["name"] = "b" },
            },
            ["updated"] = new List<object?> { new Dictionary<string, object?> { ["id"] = 1, ["name"] = "x" } },
        });

        actions.Select(a => a.Action).Should().Equal(
            MutationAction.Insert, MutationAction.Insert, MutationAction.Update, MutationAction.Delete);
        actions[0].Data["name"].Should().Be("a");
        actions[3].Data["id"].Should().Be(3);
    }

    [Fact]
    public void Binder_MissingAndEmptySections_YieldNothing()
    {
        DeltaArgumentBinder.Bind(new Dictionary<string, object?>()).Should().BeEmpty();
        DeltaArgumentBinder.Bind(new Dictionary<string, object?>
        {
            ["inserted"] = new List<object?>(),
            ["updated"] = null,
        }).Should().BeEmpty();
    }

    // ---- end to end ----

    [Fact]
    public async Task Delta_MixedDocument_AppliesAll_ReturnsTotalAffected()
    {
        var result = await ExecuteAsync("""
            mutation { items(delta: {
                inserted: [ { name: "four", qty: 40 }, { name: "five", qty: 50 } ],
                updated: [ { id: 1, name: "one2", qty: 11 } ],
                deleted: [ { id: 3 } ]
            }) }
            """);

        result.Errors.Should().BeNullOrEmpty(
            $"errors: {string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>())}");
        var json = new GraphQL.SystemTextJson.GraphQLSerializer().Serialize(result);
        System.Text.Json.JsonDocument.Parse(json).RootElement
            .GetProperty("data").GetProperty("items").GetInt32().Should().Be(4);
        (await CountAsync("name = 'four'")).Should().Be(1);
        (await CountAsync("id = 1 AND name = 'one2' AND qty = 11")).Should().Be(1);
        (await CountAsync("id = 3")).Should().Be(0);
        (await CountAsync("1=1")).Should().Be(4);
    }

    [Fact]
    public async Task Delta_FailureAnywhere_RollsBackTheWholeDocument()
    {
        // The second insert violates NOT NULL: the whole delta — including the already-
        // executed first insert and the update — must apply NOTHING.
        var result = await ExecuteAsync("""
            mutation { items(delta: {
                inserted: [ { name: "ok", qty: 1 }, { qty: 2 } ],
                updated: [ { id: 1, name: "changed", qty: 99 } ]
            }) }
            """);

        result.Errors.Should().NotBeNullOrEmpty();
        (await CountAsync("name = 'ok'")).Should().Be(0, "the delta is one transaction");
        (await CountAsync("id = 1 AND name = 'one'")).Should().Be(1, "the update rolled back with it");
    }

    [Fact]
    public async Task Delta_ExceedingBatchMaxSize_IsRefused()
    {
        _model.GetTableFromDbName("items").Metadata[MetadataKeys.Batch.MaxSize] = "2";
        try
        {
            var result = await ExecuteAsync("""
                mutation { items(delta: {
                    inserted: [ { name: "a", qty: 1 }, { name: "b", qty: 2 }, { name: "c", qty: 3 } ]
                }) }
                """);

            result.Errors.Should().NotBeNullOrEmpty();
            result.Errors![0].Message.Should().Contain("maximum allowed size");
            (await CountAsync("name IN ('a','b','c')")).Should().Be(0);
        }
        finally
        {
            _model.GetTableFromDbName("items").Metadata.Remove(MetadataKeys.Batch.MaxSize);
        }
    }

    [Fact]
    public async Task Delta_EmptyDocument_IsZeroAffectedNoOp()
    {
        var result = await ExecuteAsync("mutation { items(delta: { }) }");
        result.Errors.Should().BeNullOrEmpty();
        (await CountAsync("1=1")).Should().Be(3);
    }
}
