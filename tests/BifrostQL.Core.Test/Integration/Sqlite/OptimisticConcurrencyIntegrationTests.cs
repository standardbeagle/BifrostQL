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

    private const string SeedTimestamp = "2020-01-01T00:00:00Z";

    private static readonly string[] Rules =
    {
        "*.widgets { concurrency-token: version }",
        "*.events { concurrency-token: updated_at }",
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

        // A datetime-token table exercises the restamp branch (token type DateTime)
        // against the same race the numeric table exercises against increment.
        await Exec("DROP TABLE IF EXISTS events");
        await Exec(
            """
            CREATE TABLE events (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                updated_at DATETIME NOT NULL
            )
            """);
        await Exec($"INSERT INTO events(id, name, updated_at) VALUES (3, 'original', '{SeedTimestamp}')");

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

    /// <summary>
    /// The stale-token failure must surface a stable, branchable shape — a
    /// <see cref="BifrostExecutionError"/> carrying <c>ErrorCode == "CONFLICT"</c> —
    /// not a generic error and not a silent zero-row success. This pins the
    /// ErrorCode string as the caller's contract for detecting a lost-update reject.
    /// </summary>
    [Fact]
    public async Task StaleVersion_ConflictShape_IsErrorCodeConflict()
    {
        (await UpdateAsync("mutation { widgets(update: { id: 7, name: \"v2\", version: 1 }) }"))
            .Errors.Should().BeNullOrEmpty();

        var stale = await UpdateAsync("mutation { widgets(update: { id: 7, name: \"stomp\", version: 1 }) }");

        stale.Errors.Should().NotBeNullOrEmpty();
        stale.Errors!
            .Select(e => e.InnerException as BifrostExecutionError)
            .Where(e => e != null)
            .Should().Contain(e => e!.ErrorCode == "CONFLICT",
                "a lost-update reject must be a CONFLICT the caller can branch on, not a generic error");
    }

    /// <summary>
    /// The conflict error must leak nothing the caller could not otherwise read: the
    /// generic message is the contract. Here writer A stamps a value the losing writer B
    /// never read; the CONFLICT surfaced to B must not echo that current value.
    /// </summary>
    [Fact]
    public async Task Conflict_Message_LeaksNoCurrentRowValues()
    {
        const string secret = "VALUE_B_NEVER_READ";
        (await UpdateAsync($"mutation {{ widgets(update: {{ id: 7, name: \"{secret}\", version: 1 }}) }}"))
            .Errors.Should().BeNullOrEmpty();

        var stale = await UpdateAsync("mutation { widgets(update: { id: 7, name: \"stomp\", version: 1 }) }");

        var conflict = stale.Errors!
            .Select(e => e.InnerException as BifrostExecutionError)
            .First(e => e?.ErrorCode == "CONFLICT")!;
        // The current stored name and the advanced token value are NOT disclosed.
        conflict.Message.Should().NotContain(secret);
        conflict.Message.Should().NotContain("version = 2");
        conflict.Message.Should().Contain("concurrency token no longer matches");
    }

    // ---- datetime token: single-row race --------------------------------

    [Fact]
    public async Task DatetimeToken_ConcurrentWriters_StaleConflicts_NoClobber()
    {
        // Writer A holds the seed timestamp and wins; the token restamps to now.
        var winner = await UpdateAsync($"mutation {{ events(update: {{ id: 3, name: \"A\", updated_at: \"{SeedTimestamp}\" }}) }}");
        winner.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT name FROM events WHERE id = 3")).Should().Be("A");
        (await ScalarAsync("SELECT updated_at FROM events WHERE id = 3")).Should().NotBe(SeedTimestamp,
            "a datetime token restamps on every successful write");

        // Writer B still holds the seed timestamp — now stale — and must be rejected.
        var stale = await UpdateAsync($"mutation {{ events(update: {{ id: 3, name: \"B\", updated_at: \"{SeedTimestamp}\" }}) }}");
        stale.Errors!
            .Select(e => e.InnerException as BifrostExecutionError)
            .Should().Contain(e => e!.ErrorCode == "CONFLICT");
        // Writer A's value survives — no lost update.
        (await ScalarAsync("SELECT name FROM events WHERE id = 3")).Should().Be("A");
    }

    // ---- batch update path race -----------------------------------------

    [Fact]
    public async Task Batch_StaleVersion_Conflicts_And_DoesNotClobber()
    {
        // Writer A advances the token through the batch update path.
        (await UpdateAsync("mutation { widgets_batch(actions: [{ update: { id: 7, name: \"A\", version: 1 } }]) }"))
            .Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT version FROM widgets WHERE id = 7")).Should().Be("2");

        // Writer B, still on the stale version, is rejected as CONFLICT and rolls back.
        var stale = await UpdateAsync("mutation { widgets_batch(actions: [{ update: { id: 7, name: \"B\", version: 1 } }]) }");
        stale.Errors.Should().NotBeNullOrEmpty();
        stale.Errors!
            .Select(e => e.InnerException as BifrostExecutionError)
            .Should().Contain(e => e!.ErrorCode == "CONFLICT");

        // A's write is intact — the batch path enforces the same no-clobber guarantee.
        (await ScalarAsync("SELECT name FROM widgets WHERE id = 7")).Should().Be("A");
        (await ScalarAsync("SELECT version FROM widgets WHERE id = 7")).Should().Be("2");
    }

    // ---- batch single-statement upsert path refuses token tables --------

    [Fact]
    public async Task Batch_Upsert_OnTokenTable_IsRefused_And_CannotResurrect()
    {
        // The row is gone; a naive upsert would INSERT it back. On a token table the
        // single-statement upsert path (SQLite ON CONFLICT DO UPDATE) cannot render the
        // token WHERE, so it must refuse rather than silently resurrect the row.
        await Exec("DELETE FROM widgets WHERE id = 7");

        var result = await UpdateAsync("mutation { widgets_batch(actions: [{ upsert: { id: 7, name: \"ghost\", version: 5 } }]) }");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!
            .Select(e => e.InnerException as BifrostExecutionError)
            .Should().Contain(e => e!.ErrorCode == "CONFLICT");
        // The refusal rolled back — no row was resurrected.
        (await ScalarAsync("SELECT COUNT(*) FROM widgets WHERE id = 7")).Should().Be("0",
            "a refused upsert must not degrade into an INSERT that resurrects the row");
    }
}
