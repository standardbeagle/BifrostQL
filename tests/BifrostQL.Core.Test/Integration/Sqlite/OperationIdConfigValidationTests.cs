using BifrostQL.Core.Model;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Model-load validation of the operation-id contract, through the real loader so the store's
/// <c>visibility: hidden</c> puts it where a valid store lives. A table that opts in with
/// <c>operation-id</c> needs a model-level <c>operation-id-store</c> naming a hidden table with
/// every store column; a bad mode value or an approval-gated table is refused at load, not on
/// the first write.
/// </summary>
public sealed class OperationIdConfigValidationTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_operation_id_validation;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS notes");
        await Exec("DROP TABLE IF EXISTS operation_ids");
        await Exec("DROP TABLE IF EXISTS short_store");
        await Exec("CREATE TABLE notes (id INTEGER PRIMARY KEY, title TEXT NOT NULL)");
        await Exec(
            """
            CREATE TABLE operation_ids (
                tenant TEXT NOT NULL, caller TEXT NOT NULL, operation_id TEXT NOT NULL, table_name TEXT NOT NULL,
                action TEXT NOT NULL, target_key TEXT NOT NULL, input_hash TEXT NOT NULL,
                result TEXT NULL, created_at TEXT NOT NULL,
                PRIMARY KEY (tenant, caller, operation_id))
            """);
        // Every store column but result.
        await Exec(
            """
            CREATE TABLE short_store (
                tenant TEXT NOT NULL, caller TEXT NOT NULL, operation_id TEXT NOT NULL, table_name TEXT NOT NULL,
                action TEXT NOT NULL, target_key TEXT NOT NULL, input_hash TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (tenant, caller, operation_id))
            """);
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Theory]
    [InlineData("optional")]
    [InlineData("required")]
    public async Task HiddenStoreWithEveryColumn_Loads(string mode)
    {
        var load = () => LoadAsync(
            ":root { operation-id-store: main.operation_ids }",
            "main.operation_ids { visibility: hidden }",
            $"main.notes {{ operation-id: {mode} }}");

        await load.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("optional")]
    [InlineData("required")]
    public async Task OptedInTable_WithoutStore_IsRefused(string mode)
    {
        var load = () => LoadAsync($"main.notes {{ operation-id: {mode} }}");

        (await load.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("[operation-id-store]: 1 table(s) set 'operation-id' but no operation-id store is configured.");
    }

    [Fact]
    public async Task StoreNamingNoTable_IsRefused()
    {
        var load = () => LoadAsync(
            ":root { operation-id-store: main.missing_store }",
            "main.notes { operation-id: optional }");

        (await load.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("'main.missing_store' does not name an existing table.");
    }

    [Fact]
    public async Task ExposedStore_IsRefused()
    {
        var load = () => LoadAsync(
            ":root { operation-id-store: main.operation_ids }",
            "main.notes { operation-id: optional }");

        (await load.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("store table 'main.operation_ids' must be 'visibility: hidden'");
    }

    [Fact]
    public async Task StoreMissingAColumn_IsRefused_NamingIt()
    {
        var load = () => LoadAsync(
            ":root { operation-id-store: main.short_store }",
            "main.short_store { visibility: hidden }",
            "main.notes { operation-id: optional }");

        (await load.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("store table 'main.short_store' is missing required column(s): result.");
    }

    [Fact]
    public async Task UnknownModeValue_IsRefused()
    {
        var load = () => LoadAsync(
            ":root { operation-id-store: main.operation_ids }",
            "main.operation_ids { visibility: hidden }",
            "main.notes { operation-id: sometimes }");

        (await load.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("main.notes [operation-id]: 'operation-id' must be 'optional' or 'required'.");
    }

    [Theory]
    [InlineData("optional")]
    [InlineData("required")]
    public async Task ApprovalGatedTable_CannotRecordOperationIds(string mode)
    {
        var load = () => LoadAsync(
            ":root { operation-id-store: main.operation_ids }",
            "main.operation_ids { visibility: hidden }",
            $"main.notes {{ operation-id: {mode}; approval: enabled; approver-role: reviewer }}");

        (await load.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("main.notes [operation-id]: a table that requires 'approval' cannot record operation ids.");
    }

    private static async Task LoadAsync(params string[] rules)
    {
        var loader = new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(rules));
        await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }
}
