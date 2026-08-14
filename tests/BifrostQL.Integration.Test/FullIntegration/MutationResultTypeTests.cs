using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using RootExecutionNode = GraphQL.Execution.RootExecutionNode;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// A table's mutation field is ONE field shared by insert/update/upsert/delete, and
/// <c>TableMutationPipeline</c> returns the affected row's KEY from it for a
/// single-key table (an affected-row count for a composite key, which has no single
/// scalar to return). Its declared scalar therefore has to carry that key — a key
/// the declared type cannot represent throws while SERIALIZING, i.e. AFTER the write
/// has already landed, which is the worst possible moment to fail.
///
/// One table per key shape, because the failure is per key type and a fixture with
/// only an int key cannot show any of it.
/// </summary>
[Collection("MutationResultType")]
public class MutationResultTypeTests : FullIntegrationTestBase, IAsyncLifetime
{
    private SqliteConnection? _keepAliveConnection;

    public async Task InitializeAsync()
    {
        var connectionString = "Data Source=bifrost_mutation_result_test;Mode=Memory;Cache=Shared";
        _keepAliveConnection = new SqliteConnection(connectionString);
        await _keepAliveConnection.OpenAsync();

        var factory = new SqliteDbConnFactory(connectionString);
        await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync);
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();
        if (_keepAliveConnection != null)
            await _keepAliveConnection.DisposeAsync();
    }

    private static async Task CreateSchemaAsync(System.Data.Common.DbConnection conn)
    {
        var statements = new[]
        {
            "DROP TABLE IF EXISTS notes",
            "DROP TABLE IF EXISTS prices",
            "DROP TABLE IF EXISTS counters",
            "DROP TABLE IF EXISTS pairs",
            @"CREATE TABLE notes (code TEXT PRIMARY KEY, body TEXT NOT NULL)",
            @"CREATE TABLE prices (sku DECIMAL(18,4) PRIMARY KEY, label TEXT NOT NULL)",
            @"CREATE TABLE counters (id INTEGER PRIMARY KEY AUTOINCREMENT, label TEXT NOT NULL)",
            @"CREATE TABLE pairs (left_id INTEGER NOT NULL, right_id INTEGER NOT NULL, note TEXT NOT NULL, PRIMARY KEY (left_id, right_id))",
        };

        foreach (var sql in statements)
        {
            var cmd = new SqliteCommand(sql, (SqliteConnection)conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedDataAsync(System.Data.Common.DbConnection conn)
    {
        foreach (var sql in new[]
        {
            "INSERT INTO notes (code, body) VALUES ('alpha', 'first'), ('beta', 'second')",
            "INSERT INTO prices (sku, label) VALUES (10.5000, 'ten-fifty'), (20.2500, 'twenty-quarter')",
            "INSERT INTO counters (label) VALUES ('one'), ('two')",
            "INSERT INTO pairs (left_id, right_id, note) VALUES (1, 2, 'pair')",
        })
        {
            var cmd = new SqliteCommand(sql, (SqliteConnection)conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<object?> RunMutationAsync(string field, string mutation, Dictionary<string, object?> variables)
    {
        var result = await ExecuteQueryAsync(mutation, variables);
        result.Errors.Should().BeNullOrEmpty(
            $"a write on '{field}' must return a value its declared scalar can carry");
        var data = ((RootExecutionNode)result.Data!).ToValue() as Dictionary<string, object?>;
        return data![field];
    }

    /// <summary>
    /// A string key (TEXT, and by the same mapping uniqueidentifier) is returned by
    /// the resolver as text. Declared `Int`, serializing it threw.
    /// </summary>
    [Fact]
    public async Task Update_StringKeyedTable_ReturnsTheKey()
    {
        var value = await RunMutationAsync("notes",
            "mutation ($code: String!, $body: String!) { notes(update: {code: $code, body: $body}) }",
            new Dictionary<string, object?> { ["code"] = "alpha", ["body"] = "edited" });

        value.Should().Be("alpha");
    }

    [Fact]
    public async Task Insert_StringKeyedTable_ReturnsTheKey()
    {
        var value = await RunMutationAsync("notes",
            "mutation ($code: String!, $body: String!) { notes(insert: {code: $code, body: $body}) }",
            new Dictionary<string, object?> { ["code"] = "gamma", ["body"] = "third" });

        value.Should().Be("gamma");
    }

    /// <summary>
    /// Delete shares the same field but returns an affected-row COUNT, so whatever
    /// scalar the field declares has to carry both. This pins the shape callers see.
    /// </summary>
    [Fact]
    public async Task Delete_StringKeyedTable_ReturnsTheAffectedCount()
    {
        var value = await RunMutationAsync("notes",
            "mutation ($code: String!) { notes(delete: {code: $code}) }",
            new Dictionary<string, object?> { ["code"] = "beta" });

        // Serialized through the field's declared scalar — a string-keyed table's
        // count arrives as text, not as a JSON number.
        value.Should().Be("1");
    }

    [Fact]
    public async Task Update_DecimalKeyedTable_ReturnsTheKey()
    {
        var value = await RunMutationAsync("prices",
            "mutation ($sku: Decimal!, $label: String!) { prices(update: {sku: $sku, label: $label}) }",
            new Dictionary<string, object?> { ["sku"] = "10.5000", ["label"] = "edited" });

        Convert.ToDecimal(value).Should().Be(10.5000m);
    }

    /// <summary>An int key still returns a JSON number — the common case must not shift.</summary>
    [Fact]
    public async Task Update_IntKeyedTable_StillReturnsAnIntKey()
    {
        var value = await RunMutationAsync("counters",
            "mutation ($id: Int!, $label: String!) { counters(update: {id: $id, label: $label}) }",
            new Dictionary<string, object?> { ["id"] = 1, ["label"] = "edited" });

        value.Should().Be(1);
    }

    /// <summary>
    /// A composite key has no single scalar to return, so the pipeline returns the
    /// affected-row count and the field stays Int.
    /// </summary>
    [Fact]
    public async Task Update_CompositeKeyedTable_ReturnsTheAffectedCount()
    {
        var value = await RunMutationAsync("pairs",
            "mutation ($left: Int!, $right: Int!, $note: String!) " +
            "{ pairs(update: {left_id: $left, right_id: $right, note: $note}) }",
            new Dictionary<string, object?> { ["left"] = 1, ["right"] = 2, ["note"] = "edited" });

        value.Should().Be(1);
    }
}
