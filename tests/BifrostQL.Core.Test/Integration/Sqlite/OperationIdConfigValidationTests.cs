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
        await Exec("DROP TABLE IF EXISTS ledger");
        await Exec("DROP TABLE IF EXISTS operation_ids");
        await Exec("DROP TABLE IF EXISTS short_store");
        await Exec("DROP TABLE IF EXISTS unkeyed_store");
        await Exec("DROP TABLE IF EXISTS id_only_store");
        await Exec("DROP TABLE IF EXISTS indexed_store");
        await Exec("CREATE TABLE notes (id INTEGER PRIMARY KEY, title TEXT NOT NULL)");
        await Exec(
            "CREATE TABLE ledger (id INTEGER PRIMARY KEY, title TEXT NOT NULL, secret TEXT NULL, " +
            "created_at DATETIME NOT NULL, deleted_at DATETIME NULL)");
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
        // Every column, but nothing makes (tenant, caller, operation_id) unique: two concurrent
        // claims of one id would both insert, and both writes would commit.
        await Exec("CREATE TABLE unkeyed_store (id INTEGER PRIMARY KEY, " +
            "tenant TEXT NOT NULL, caller TEXT NOT NULL, operation_id TEXT NOT NULL, table_name TEXT NOT NULL, " +
            "action TEXT NOT NULL, target_key TEXT NOT NULL, input_hash TEXT NOT NULL, result TEXT NULL, created_at TEXT NOT NULL)");
        // Unique, but over the id alone: two callers' ids would collide instead of staying apart.
        await Exec("CREATE TABLE id_only_store (id INTEGER PRIMARY KEY, " +
            "tenant TEXT NOT NULL, caller TEXT NOT NULL, operation_id TEXT NOT NULL, table_name TEXT NOT NULL, " +
            "action TEXT NOT NULL, target_key TEXT NOT NULL, input_hash TEXT NOT NULL, result TEXT NULL, created_at TEXT NOT NULL)");
        await Exec("CREATE UNIQUE INDEX ux_id_only ON id_only_store (operation_id)");
        // A unique index over the three key columns, in another order, instead of the primary key.
        await Exec("CREATE TABLE indexed_store (id INTEGER PRIMARY KEY, " +
            "tenant TEXT NOT NULL, caller TEXT NOT NULL, operation_id TEXT NOT NULL, table_name TEXT NOT NULL, " +
            "action TEXT NOT NULL, target_key TEXT NOT NULL, input_hash TEXT NOT NULL, result TEXT NULL, created_at TEXT NOT NULL)");
        await Exec("CREATE UNIQUE INDEX ux_indexed ON indexed_store (operation_id, caller, tenant)");
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task StoreKeyedByAUniqueIndexOverTheOwnerAndId_Loads()
    {
        var load = () => LoadAsync(
            ":root { operation-id-store: main.indexed_store }",
            "main.indexed_store { visibility: hidden }",
            "main.notes { operation-id: optional }");

        await load.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("unkeyed_store")]
    [InlineData("id_only_store")]
    public async Task StoreWithoutAUniqueKeyOverTheOwnerAndId_IsRefused(string store)
    {
        var load = () => LoadAsync(
            $":root {{ operation-id-store: main.{store} }}",
            $"main.{store} {{ visibility: hidden }}",
            "main.notes { operation-id: optional }");

        (await load.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(
                $"store table 'main.{store}' needs a primary key or unique index over exactly (tenant, caller, operation_id)");
    }

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

    // The engine writes these features run (retention purge, deferred undo, key-rotation
    // sweep) go through the mutation pipeline with no operation id, so a required table
    // would refuse them on every pass.
    [Theory]
    [InlineData("ttl", "main.ledger { operation-id: required; ttl: 1d after created_at }")]
    [InlineData("retain", "main.ledger { operation-id: required; soft-delete: deleted_at; retain: 30d }")]
    [InlineData("deferrable", "main.ledger { operation-id: required; deferrable: enabled; undo-window: 1h }")]
    [InlineData("encrypt", "main.ledger { operation-id: required }", "main.ledger.secret { encrypt: aes-256-gcm; key-ref: config:pii }")]
    [InlineData("chat-conversations", "main.ledger { operation-id: required; chat-conversations: enabled }")]
    [InlineData("chat-messages", "main.ledger { operation-id: required; chat-messages: enabled; chat-role: title; chat-content: secret; chat-conversation-fk: id; chat-created-at: created_at }")]
    public async Task RequiredTable_WithAnEngineWriter_IsRefused(string key, params string[] tableRules)
    {
        var load = () => LoadAsync(
            [":root { operation-id-store: main.operation_ids }", "main.operation_ids { visibility: hidden }", .. tableRules]);

        (await load.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(
                $"main.ledger [operation-id]: a 'required' table cannot set '{key}'; its engine writes carry no operation id.");
    }

    [Theory]
    [InlineData("main.ledger { operation-id: optional; ttl: 1d after created_at }")]
    [InlineData("main.ledger { operation-id: optional; soft-delete: deleted_at; retain: 30d }")]
    [InlineData("main.ledger { operation-id: optional }", "main.ledger.secret { encrypt: aes-256-gcm; key-ref: config:pii }")]
    [InlineData("main.ledger { operation-id: optional; deferrable: enabled; undo-window: 1h }")]
    [InlineData("main.ledger { operation-id: optional; chat-conversations: enabled }")]
    public async Task OptionalTable_WithAnEngineWriter_DoesNotTripTheRequiredRefusal(params string[] tableRules)
    {
        var errors = await LoadErrorsAsync(
            [":root { operation-id-store: main.operation_ids }", "main.operation_ids { visibility: hidden }", .. tableRules]);

        // A deferrable or chat fixture also reports its own unrelated store/pair errors, so the
        // control asserts only that the required-mode refusal is absent.
        errors.Should().NotContain("cannot set '");
    }

    [Theory]
    [InlineData("optional")]
    [InlineData("required")]
    [InlineData("sometimes")]
    public async Task OperationIdMode_OnAHiddenTable_IsRefused(string mode)
    {
        var load = () => LoadAsync(
            ":root { operation-id-store: main.operation_ids }",
            "main.operation_ids { visibility: hidden }",
            $"main.ledger {{ visibility: hidden; operation-id: {mode} }}");

        (await load.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("main.ledger [operation-id]: a hidden table cannot record operation ids; no client write can reach it.");
    }

    // The store is hidden, and the purge sweeps hidden tables, so its ttl must be checked at load
    // like an exposed table's: an invalid one would otherwise load and purge nothing, skipped
    // with a log line on every pass. This store's created_at is TEXT, not a timestamp.
    [Theory]
    [InlineData("0d after created_at", "the magnitude must be a positive integer.")]
    [InlineData("1d after created_at", "but a retention anchor must be a timestamp type")]
    [InlineData("1d after expires_at", "which does not exist on the table")]
    public async Task Invalid_retention_is_refused_at_model_load(string ttl, string reason)
    {
        var load = () => LoadAsync(
            ":root { operation-id-store: main.operation_ids }",
            $"main.operation_ids {{ visibility: hidden; ttl: {ttl} }}",
            "main.notes { operation-id: optional }");

        (await load.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain($"main.operation_ids [ttl]: '{ttl}' - ").And.Contain(reason);
    }

    private static async Task<string> LoadErrorsAsync(params string[] rules)
    {
        try
        {
            await LoadAsync(rules);
            return string.Empty;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
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
