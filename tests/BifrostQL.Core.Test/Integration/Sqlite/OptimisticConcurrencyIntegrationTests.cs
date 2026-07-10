using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// End-to-end optimistic concurrency: a table with <c>concurrency-token</c> metadata,
/// updated through the real mutation pipeline. A matching-version update succeeds and
/// bumps the token; a stale-version update is rejected as a CONFLICT and leaves the
/// row untouched (no silent lost update); the next update at the current version
/// succeeds again.
/// </summary>
public sealed class OptimisticConcurrencyIntegrationTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_optimistic_concurrency_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    private static readonly string[] Rules =
    {
        "*.widgets { concurrency-token: version }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS widgets");
        await Exec(
            """
            CREATE TABLE widgets (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                version INTEGER NOT NULL
            )
            """);
        await Exec("INSERT INTO widgets(id, name, version) VALUES (7, 'original', 1)");

        var factory = new SqliteDbConnFactory(ConnString);
        _model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<ExecutionResult> UpdateAsync(string mutation)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new ConcurrencyMutationTransformer() },
        });
        await using var provider = services.BuildServiceProvider();

        var executor = new DocumentExecuter();
        return await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            });
        });
    }

    [Fact]
    public async Task MatchingVersion_Succeeds_And_BumpsToken()
    {
        var result = await UpdateAsync("mutation { widgets(update: { id: 7, name: \"v2\", version: 1 }) }");

        result.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT name FROM widgets WHERE id = 7")).Should().Be("v2");
        (await ScalarAsync("SELECT version FROM widgets WHERE id = 7")).Should().Be("2");
    }

    [Fact]
    public async Task StaleVersion_IsRejected_AsConflict_And_RowUnchanged()
    {
        // First update moves version 1 -> 2.
        (await UpdateAsync("mutation { widgets(update: { id: 7, name: \"v2\", version: 1 }) }"))
            .Errors.Should().BeNullOrEmpty();

        // A second writer still holding version 1 must be rejected, not silently lost.
        var stale = await UpdateAsync("mutation { widgets(update: { id: 7, name: \"stomp\", version: 1 }) }");
        stale.Errors.Should().NotBeNullOrEmpty();
        stale.Errors!.Any(e => (e.InnerException as BifrostExecutionError)?.ErrorCode == "CONFLICT"
                               || e.Message.Contains("concurrency token")).Should().BeTrue();

        // The row is untouched by the rejected write.
        (await ScalarAsync("SELECT name FROM widgets WHERE id = 7")).Should().Be("v2");
        (await ScalarAsync("SELECT version FROM widgets WHERE id = 7")).Should().Be("2");
    }

    [Fact]
    public async Task UpdateAtCurrentVersion_AfterConflict_Succeeds()
    {
        await UpdateAsync("mutation { widgets(update: { id: 7, name: \"v2\", version: 1 }) }");   // -> version 2
        await UpdateAsync("mutation { widgets(update: { id: 7, name: \"stale\", version: 1 }) }"); // rejected

        var result = await UpdateAsync("mutation { widgets(update: { id: 7, name: \"v3\", version: 2 }) }");
        result.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT name FROM widgets WHERE id = 7")).Should().Be("v3");
        (await ScalarAsync("SELECT version FROM widgets WHERE id = 7")).Should().Be("3");
    }
}
