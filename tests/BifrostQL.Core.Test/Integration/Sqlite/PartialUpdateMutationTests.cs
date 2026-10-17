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

    [Fact]
    public async Task Update_SupplyingOnlyTheKey_IsANoOpReturningZero()
    {
        // Characterizes shipped behaviour, not a design choice made here:
        // validation now admits a key-only update (every non-key field is
        // optional), and TableMutationPipeline returns (0, 0) when there is
        // nothing to SET — no statement runs, no error, the field reads 0 (not
        // the key). The mutations guide documents this; if the pipeline starts
        // refusing it, update both.
        var result = await ExecuteMutationAsync("mutation { gadgets(update: { id: 3 }) }");

        result.Errors.Should().BeNullOrEmpty();
        var json = new GraphQL.SystemTextJson.GraphQLSerializer().Serialize(result);
        json.Should().Contain("\"gadgets\":0");
        (await ScalarAsync("SELECT name || '|' || sku || '|' || status FROM gadgets WHERE id = 3"))
            .Should().Be("original|SKU-3|live");
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
