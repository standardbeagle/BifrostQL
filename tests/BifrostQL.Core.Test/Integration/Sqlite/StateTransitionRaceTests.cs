using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// A state-machine UPDATE must still find the row in the state it was validated
/// against. The current state is loaded, the transition validated, and only then is the
/// UPDATE issued; before this fix the UPDATE's WHERE carried the key and row scope but
/// not the state, so a transition that landed between the load and the write (two
/// concurrent valid transitions under READ COMMITTED both pass validation) was silently
/// overwritten — the second write applied a transition nobody validated from the state
/// the row was actually in.
///
/// <para>The race is made deterministic, with no sleeps or second connection: a
/// transformer that runs AFTER the state-machine transformer moves the row to another
/// state through the write's own connection and transaction
/// (<see cref="MutationTransformContext.Connection"/>) — exactly the interleaving a
/// concurrent committed transition produces — before the pipeline issues its UPDATE.
/// The write must then affect zero rows and answer CONFLICT, rolling back.</para>
///
/// <para>Both allowed branches are covered (a real transition and a same-state write),
/// on both the single-row and the batch pipeline: a same-state write over a row that has
/// moved is itself an unvalidated transition back.</para>
/// </summary>
public sealed class StateTransitionRaceTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_state_transition_race_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;

    private static readonly string[] Rules =
    {
        "*.tickets { state-column: status; initial-state: open; states: open,review,closed; transitions: open->review|review->closed }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS tickets");
        await Exec("CREATE TABLE tickets (id INTEGER PRIMARY KEY, status TEXT NOT NULL, title TEXT NOT NULL)");
        await Exec("INSERT INTO tickets(id, status, title) VALUES (1, 'open', 'first')");
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

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

    [Fact]
    public async Task SingleRow_TransitionFromAStateTheRowLeft_IsAConflict()
    {
        var executor = BuildExecutor(raceTo: "closed");

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "tickets",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["status"] = "review" },
            PrimaryKey = new object[] { 1 },
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        (await act.Should().ThrowAsync<BifrostExecutionError>()).Which.ErrorCode.Should().Be("CONFLICT");
        (await ScalarAsync("SELECT status FROM tickets WHERE id = 1")).Should().Be("open",
            "the conflicting write rolls back with its transaction");
    }

    [Fact]
    public async Task SingleRow_SameStateWriteOverARowThatMoved_IsAConflict()
    {
        var executor = BuildExecutor(raceTo: "review");

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "tickets",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["status"] = "open", ["title"] = "renamed" },
            PrimaryKey = new object[] { 1 },
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        (await act.Should().ThrowAsync<BifrostExecutionError>()).Which.ErrorCode.Should().Be("CONFLICT");
        (await ScalarAsync("SELECT title FROM tickets WHERE id = 1")).Should().Be("first");
    }

    [Fact]
    public async Task Batch_TransitionFromAStateTheRowLeft_IsAConflict()
    {
        var executor = BuildExecutor(raceTo: "closed");

        var act = () => executor.ExecuteBatchAsync(new MutationBatchIntent
        {
            Table = "tickets",
            Actions = new[]
            {
                new MutationBatchAction(MutationIntentAction.Update,
                    new Dictionary<string, object?> { ["id"] = 1, ["status"] = "review" }),
            },
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        (await act.Should().ThrowAsync<BifrostExecutionError>()).Which.ErrorCode.Should().Be("CONFLICT");
        (await ScalarAsync("SELECT status FROM tickets WHERE id = 1")).Should().Be("open");
    }

    [Fact]
    public async Task SingleRow_NoRace_TransitionStillApplies()
    {
        // Positive control: the from-state guard must not refuse the ordinary case.
        var executor = BuildExecutor(raceTo: null);

        var result = await executor.ExecuteAsync(new MutationIntent
        {
            Table = "tickets",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["status"] = "review" },
            PrimaryKey = new object[] { 1 },
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        result.AffectedRows.Should().Be(1);
        (await ScalarAsync("SELECT status FROM tickets WHERE id = 1")).Should().Be("review");
    }

    private static MutationIntentExecutor BuildExecutor(string? raceTo)
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["connFactory"] = factory,
            });
        });

        var transformers = new List<IMutationTransformer> { new StateMachineMutationTransformer() };
        if (raceTo is not null)
            transformers.Add(new ConcurrentTransition(raceTo));
        return new MutationIntentExecutor(pathCache, new MutationTransformersWrap { Transformers = transformers }, services: null);
    }

    /// <summary>
    /// Plays the concurrent writer: after the state-machine transformer validated the
    /// transition against the loaded state, moves the row to <c>raceTo</c> on the write's
    /// own connection and transaction. Fires only on the real chain run (the one carrying
    /// the state column) — the row-scope probe ahead of the state load carries the key only.
    /// </summary>
    private sealed class ConcurrentTransition : IMutationTransformer
    {
        private readonly string _raceTo;

        public ConcurrentTransition(string raceTo) => _raceTo = raceTo;

        public int Priority => 250;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => mutationType == MutationType.Update;

        public async ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            if (data.ContainsKey("status"))
            {
                var conn = context.Connection ?? throw new InvalidOperationException("no write connection");
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = context.Transaction;
                cmd.CommandText = "UPDATE tickets SET status = $s WHERE id = 1";
                cmd.Parameters.Add(new SqliteParameter("$s", _raceTo));
                await cmd.ExecuteNonQueryAsync();
            }
            return new MutationTransformResult { MutationType = mutationType, Data = data };
        }
    }
}
