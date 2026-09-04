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
/// A write addressed by primary key must carry EVERY primary-key column, or it
/// addresses no row. The key/standard split (<see cref="MutationArgumentBinder"/>,
/// <see cref="BatchMutationPipeline"/>) collected whichever key columns happened
/// to be present and the pipelines only asked whether the key set was NON-EMPTY,
/// so on a composite key <c>(id, region)</c> a write supplying only
/// <c>region</c> emitted <c>... WHERE region = @v</c> — a bulk rewrite/delete of
/// every row in scope wearing the shape of a single-row write.
///
/// <para>The exercised surface is <see cref="IMutationIntentExecutor"/>, the
/// protocol-adapter write seam (MCP, RESP, S3, OData, gRPC): its
/// <see cref="MutationIntent.Data"/> is a free dictionary built from the wire, so
/// a partial key reaches the pipeline directly. The GraphQL front door types its
/// update/delete inputs with the key columns required, which is a front-door
/// check, not the pipeline invariant — the guard belongs where the predicate is
/// built.</para>
///
/// <para>Revert-proof (per .claude/rules/regression-test-non-vacuous.md): the RED
/// signature is MULTIPLE ROWS REWRITTEN/REMOVED, not merely a missing exception —
/// every fact asserts the whole table's state. The fixture spans a composite key,
/// a key value of <c>0</c> (present-but-falsy, so presence cannot be decided by
/// truthiness), a single-column key, and a predicate-only delete that carries no
/// key column at all and must keep working.</para>
/// </summary>
public sealed class PartialCompositeKeyPredicateTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_partial_composite_key_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS ledger");
        await Exec(
            """
            CREATE TABLE ledger (
                id INTEGER NOT NULL,
                region TEXT NOT NULL,
                note TEXT NOT NULL,
                PRIMARY KEY (id, region)
            )
            """);
        // Several rows share `region`, so a predicate built from that key column
        // alone spans more than one row — the condition the bug needs to show.
        await Exec(
            """
            INSERT INTO ledger(id, region, note) VALUES
                (0, 'west', 'zero-west'),
                (1, 'west', 'one-west'),
                (2, 'west', 'two-west'),
                (3, 'east', 'three-east')
            """);

        await Exec("DROP TABLE IF EXISTS widgets");
        await Exec(
            """
            CREATE TABLE widgets (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL
            )
            """);
        await Exec("INSERT INTO widgets(id, name) VALUES (1, 'first'), (2, 'second')");
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task Update_WithPartialCompositeKey_IsRejectedAndRewritesNoRow()
    {
        var thrown = await Record.ExceptionAsync(() => BuildExecutor().ExecuteAsync(new MutationIntent
        {
            Table = "ledger",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["region"] = "west", ["note"] = "pwned" },
            Endpoint = EndpointPath,
        }));

        // Table state is asserted FIRST so the RED signature is the damage — three
        // sibling rows carrying "pwned" — not merely a missing exception.
        (await LedgerNotesAsync()).Should().Equal("zero-west", "one-west", "two-west", "three-east");
        thrown.Should().BeOfType<BifrostExecutionError>(
            "a partial primary key identifies no row and must be refused, not run as a bulk update");
    }

    [Fact]
    public async Task Delete_WithPartialCompositeKey_IsRejectedAndRemovesNoRow()
    {
        var thrown = await Record.ExceptionAsync(() => BuildExecutor().ExecuteAsync(new MutationIntent
        {
            Table = "ledger",
            Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?> { ["region"] = "west" },
            Endpoint = EndpointPath,
        }));

        // RED signature: three rows gone.
        (await LedgerNotesAsync()).Should().Equal("zero-west", "one-west", "two-west", "three-east");
        thrown.Should().BeOfType<BifrostExecutionError>(
            "a partial primary key must not be accepted as a delete predicate");
    }

    [Fact]
    public async Task BatchUpdate_WithPartialCompositeKey_IsRejectedAndRewritesNoRow()
    {
        var thrown = await Record.ExceptionAsync(() => BuildExecutor().ExecuteBatchAsync(new MutationBatchIntent
        {
            Table = "ledger",
            Actions = new[]
            {
                new MutationBatchAction(
                    MutationIntentAction.Update,
                    new Dictionary<string, object?> { ["region"] = "west", ["note"] = "batch-pwned" }),
            },
            Endpoint = EndpointPath,
        }));

        // RED signature: three sibling rows carrying "batch-pwned".
        (await LedgerNotesAsync()).Should().Equal("zero-west", "one-west", "two-west", "three-east");
        thrown.Should().BeOfType<BifrostExecutionError>(
            "the batch pipeline re-derives the key split and must apply the same rule");
    }

    [Fact]
    public async Task BatchDelete_WithPartialCompositeKey_IsRejectedAndRemovesNoRow()
    {
        var thrown = await Record.ExceptionAsync(() => BuildExecutor().ExecuteBatchAsync(new MutationBatchIntent
        {
            Table = "ledger",
            Actions = new[]
            {
                new MutationBatchAction(
                    MutationIntentAction.Delete,
                    new Dictionary<string, object?> { ["region"] = "west" }),
            },
            Endpoint = EndpointPath,
        }));

        // RED signature: three rows gone.
        (await LedgerNotesAsync()).Should().Equal("zero-west", "one-west", "two-west", "three-east");
        thrown.Should().BeOfType<BifrostExecutionError>();
    }

    [Fact]
    public async Task Update_WithCompleteCompositeKeyOfValueZero_UpdatesOnlyThatRow()
    {
        // Positive control, and the falsy-key case: key value 0 IS supplied, so the
        // write must proceed and touch exactly its own row.
        var result = await BuildExecutor().ExecuteAsync(new MutationIntent
        {
            Table = "ledger",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["id"] = 0, ["region"] = "west", ["note"] = "zero-updated" },
            Endpoint = EndpointPath,
        });

        result.AffectedRows.Should().Be(1);
        (await LedgerNotesAsync()).Should().Equal("zero-updated", "one-west", "two-west", "three-east");
    }

    [Fact]
    public async Task Update_WithSingleColumnKey_StillUpdatesItsRow()
    {
        // The guard must not regress single-column keys, where the one supplied key
        // column is already the whole key.
        await BuildExecutor().ExecuteAsync(new MutationIntent
        {
            Table = "widgets",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["id"] = 2, ["name"] = "renamed" },
            Endpoint = EndpointPath,
        });

        (await WidgetNamesAsync()).Should().Equal("first", "renamed");
    }

    [Fact]
    public async Task Delete_WithNoKeyColumnAtAll_StillScopesByItsPredicate()
    {
        // Positive control for the other side of the rule: a delete carrying NO key
        // column is a predicate delete, which the pipeline has always allowed. The
        // guard rejects a PARTIAL key, not the absence of one.
        var result = await BuildExecutor().ExecuteAsync(new MutationIntent
        {
            Table = "ledger",
            Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?> { ["note"] = "three-east" },
            Endpoint = EndpointPath,
        });

        result.AffectedRows.Should().Be(1);
        (await LedgerNotesAsync()).Should().Equal("zero-west", "one-west", "two-west");
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> QueryColumnAsync(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await using var reader = await cmd.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }

    private Task<List<string>> LedgerNotesAsync() =>
        QueryColumnAsync("SELECT note FROM ledger ORDER BY region DESC, id");

    private Task<List<string>> WidgetNamesAsync() =>
        QueryColumnAsync("SELECT name FROM widgets ORDER BY id");

    private static MutationIntentExecutor BuildExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>())).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["connFactory"] = factory,
            });
        });

        return new MutationIntentExecutor(
            pathCache,
            new MutationTransformersWrap { Transformers = Array.Empty<IMutationTransformer>() });
    }
}
