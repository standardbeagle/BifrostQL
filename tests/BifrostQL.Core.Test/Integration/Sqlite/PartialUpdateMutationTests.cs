using System.Collections.Generic;
using System.Threading.Tasks;
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

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// End-to-end through GraphQL validation: an UPDATE sets only the columns it is
/// given, so the update input must not require the table's other NOT NULL
/// columns — before, supplying one column failed validation with "field ... of
/// required type ... was not provided". The insert input likewise lets the
/// database fill a NOT NULL column that declares a default. The fixture has
/// several NOT NULL columns, one of them defaulted, and a composite-key table
/// so the key-addressing half is covered for more than one key column.
/// </summary>
public sealed class PartialUpdateMutationTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_partial_update_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS gadgets");
        await Exec("DROP TABLE IF EXISTS seats");
        await Exec(
            """
            CREATE TABLE gadgets (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                sku TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'new'
            )
            """);
        await Exec("INSERT INTO gadgets(id, name, sku, status) VALUES (3, 'original', 'SKU-3', 'live')");
        await Exec(
            """
            CREATE TABLE seats (
                venue INTEGER NOT NULL,
                seat INTEGER NOT NULL,
                holder TEXT NOT NULL,
                price INTEGER NOT NULL,
                PRIMARY KEY (venue, seat)
            )
            """);
        await Exec("INSERT INTO seats(venue, seat, holder, price) VALUES (1, 9, 'ana', 40)");
        // Every non-key column nullable, so a key-only upsert passes validation
        // (upsert applies the insert rule to non-key fields).
        await Exec("DROP TABLE IF EXISTS labels");
        await Exec("CREATE TABLE labels (id INTEGER PRIMARY KEY, note TEXT NULL)");
        await Exec("INSERT INTO labels(id, note) VALUES (5, 'five'), (6, 'six')");

        var factory = new SqliteDbConnFactory(ConnString);
        _model = await new DbModelLoader(factory, new MetadataLoader(System.Array.Empty<string>())).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task Update_SupplyingOneColumn_SucceedsAndLeavesTheOthers()
    {
        var result = await ExecuteMutationAsync("mutation { gadgets(update: { id: 3, name: \"renamed\" }) }");

        result.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT name || '|' || sku || '|' || status FROM gadgets WHERE id = 3"))
            .Should().Be("renamed|SKU-3|live", "only the supplied column is SET");
    }

    [Fact]
    public async Task CompositeKeyUpdate_SupplyingOneColumn_Succeeds()
    {
        var result = await ExecuteMutationAsync("mutation { seats(update: { venue: 1, seat: 9, price: 55 }) }");

        result.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT holder || '|' || price FROM seats WHERE venue = 1 AND seat = 9"))
            .Should().Be("ana|55");
    }

    [Fact]
    public async Task Update_WithoutTheKey_IsStillRejectedByValidation()
    {
        var result = await ExecuteMutationAsync("mutation { gadgets(update: { name: \"keyless\" }) }");

        result.Errors.Should().NotBeNullOrEmpty("the key addresses the row and stays required");
        (await ScalarAsync("SELECT COUNT(*) FROM gadgets WHERE name = 'keyless'")).Should().Be("0");
    }

    // Spelled as a literal, not read from MutationArgumentBinder.NoUpdateColumnsMessage,
    // so a change to the production constant goes RED here.
    private const string NoUpdateColumnsWireText = "An update must set at least one non-key column.";

    [Fact]
    public async Task Update_SupplyingOnlyTheKey_IsRefusedAndWritesNothing()
    {
        // Validation admits a key-only update (every non-key field is optional), so
        // the pipeline refuses it: there is nothing to SET, and a silent 0 would
        // read as "row not found" to a caller that simply forgot its fields.
        var result = await ExecuteMutationAsync("mutation { gadgets(update: { id: 3 }) }");

        result.Errors.Should().ContainSingle();
        result.Errors![0].Message.Should().Be(NoUpdateColumnsWireText);
        result.Errors[0].Message.Should().NotContain("gadgets").And.NotContain("id", "the wire text names no table or column");
        (await ScalarAsync("SELECT name || '|' || sku || '|' || status FROM gadgets WHERE id = 3"))
            .Should().Be("original|SKU-3|live");
    }

    [Fact]
    public async Task CompositeKeyUpdate_SupplyingOnlyTheKey_IsRefused()
    {
        var result = await ExecuteMutationAsync("mutation { seats(update: { venue: 1, seat: 9 }) }");

        result.Errors.Should().ContainSingle();
        result.Errors![0].Message.Should().Be(NoUpdateColumnsWireText);
        (await ScalarAsync("SELECT holder || '|' || price FROM seats WHERE venue = 1 AND seat = 9"))
            .Should().Be("ana|40");
    }

    [Fact]
    public async Task BatchUpdate_SupplyingOnlyTheKey_IsRefusedAndRollsBackTheBatch()
    {
        // Per-row batch path (SQLite has no bulk executor; the set-based plan's refusal
        // is pinned in BulkBatchPlanTests.KeyOnlyUpdate_IsRefusedWithThePerRowWireText
        // with this same literal). The first action addresses a different row, so the
        // duplicate normalizer cannot merge the two, and proves the whole batch rolls
        // back rather than skipping the key-only row.
        var result = await ExecuteMutationAsync(
            "mutation { labels_batch(actions: [{ update: { id: 5, note: \"batched\" } }, { update: { id: 6 } }]) }");

        result.Errors.Should().ContainSingle();
        result.Errors![0].Message.Should().Be(NoUpdateColumnsWireText);
        (await ScalarAsync("SELECT note FROM labels WHERE id = 5")).Should().Be("five");
    }

    [Fact]
    public async Task Upsert_SupplyingOnlyTheKeyOfAnExistingRow_StaysANoOpReturningZero()
    {
        // Upsert is insert-or-nothing for a key-only payload: the row exists and
        // there is nothing to SET, so it answers 0 without an error.
        var keyOnly = await ExecuteMutationAsync("mutation { labels(upsert: { id: 5 }) }");

        keyOnly.Errors.Should().BeNullOrEmpty();
        new GraphQL.SystemTextJson.GraphQLSerializer().Serialize(keyOnly).Should().Contain("\"labels\":0");
        (await ScalarAsync("SELECT note FROM labels WHERE id = 5")).Should().Be("five");
    }

    [Fact]
    public async Task BatchUpsert_SupplyingOnlyTheKeyOfAnExistingRow_IsSkippedNotRefused()
    {
        var result = await ExecuteMutationAsync(
            "mutation { labels_batch(actions: [{ upsert: { id: 5 } }, { update: { id: 6, note: \"updated\" } }]) }");

        result.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT note FROM labels WHERE id = 5")).Should().Be("five");
        (await ScalarAsync("SELECT note FROM labels WHERE id = 6")).Should().Be("updated");
    }

    [Fact]
    public async Task Insert_OmittingADefaultedNotNullColumn_UsesTheDefault()
    {
        var result = await ExecuteMutationAsync("mutation { gadgets(insert: { name: \"fresh\", sku: \"SKU-4\" }) }");

        result.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT status FROM gadgets WHERE sku = 'SKU-4'")).Should().Be("new");
    }

    [Fact]
    public async Task Insert_OmittingANotNullColumnWithoutDefault_IsRejectedByValidation()
    {
        var result = await ExecuteMutationAsync("mutation { gadgets(insert: { name: \"no-sku\" }) }");

        result.Errors.Should().NotBeNullOrEmpty("sku is NOT NULL with no default");
        (await ScalarAsync("SELECT COUNT(*) FROM gadgets WHERE name = 'no-sku'")).Should().Be("0");
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

    private async Task<ExecutionResult> ExecuteMutationAsync(string mutation)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = System.Array.Empty<IMutationTransformer>(),
        });
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, NullQueryTransformerService.Instance),
            });
        });
    }
}
