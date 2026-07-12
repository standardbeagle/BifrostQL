using System.Text.Json;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Crypto;
using BifrostQL.Core.Modules.History;
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
/// The shape of what lands in the trail, for the cases a reader of the trail has to be
/// able to trust: a composite key names its row losslessly, a null↔value transition is a
/// change like any other, a soft delete is recorded as what it is at the row level, a
/// per-table history table receives its own table's rows, and an encrypted column is
/// recorded as the ciphertext at rest — the trail must not become a plaintext
/// side-channel around field encryption.
/// </summary>
public sealed class HistoryTrailShapeTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_history_shape_test;Mode=Memory;Cache=Shared";
    private const string KeyRef = "config:pii";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        foreach (var drop in new[] { "orders", "line_items", "secrets", "__history", "orders_history" })
            await Exec($"DROP TABLE IF EXISTS {drop}");

        await Exec("CREATE TABLE orders (id INTEGER PRIMARY KEY, status TEXT NULL, deleted_at TEXT NULL)");
        // Composite primary key: the trail must name such a row by BOTH components.
        await Exec(
            """
            CREATE TABLE line_items (
                order_id INTEGER NOT NULL,
                line_no  INTEGER NOT NULL,
                qty      INTEGER NULL,
                PRIMARY KEY (order_id, line_no)
            )
            """);
        await Exec("CREATE TABLE secrets (id INTEGER PRIMARY KEY, ssn TEXT NULL)");
        await Exec(HistoryDdl("__history"));
        await Exec(HistoryDdl("orders_history"));

        await Exec("INSERT INTO orders(id, status) VALUES (1, 'packing')");
        await Exec("INSERT INTO line_items(order_id, line_no, qty) VALUES (7, 2, 5)");
    }

    private static string HistoryDdl(string table) =>
        $"""
        CREATE TABLE {table} (
            id              INTEGER PRIMARY KEY,
            entity          TEXT NOT NULL,
            entity_id       TEXT NOT NULL,
            op              TEXT NOT NULL,
            actor           TEXT NULL,
            changed_at      TEXT NOT NULL,
            before          TEXT NULL,
            after           TEXT NULL,
            changed_columns TEXT NULL
        )
        """;

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<IDbModel> LoadModelAsync(params string[] metadata) =>
        await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(metadata)).LoadAsync();

    private sealed record HistoryRow(
        string Entity, string EntityId, string Op, string? Actor, string? Before, string? After, string ChangedColumns);

    private async Task<List<HistoryRow>> HistoryRowsAsync(string table = "__history")
    {
        var rows = new List<HistoryRow>();
        await using var cmd = new SqliteCommand(
            $"SELECT entity, entity_id, op, actor, before, after, changed_columns FROM {table} ORDER BY id",
            _keepAlive);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new HistoryRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6)));
        }
        return rows;
    }

    private async Task<long> RowCountAsync(string table, string where)
    {
        await using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM {table} WHERE {where}", _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static Dictionary<string, JsonElement> Json(string? value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value!)!;

    private static string[] JsonArray(string value) =>
        JsonSerializer.Deserialize<string[]>(value)!;

    [Fact]
    public async Task CompositeKey_IsRecordedAsAnObject_WithEveryKeyColumn()
    {
        var model = await LoadModelAsync(
            "main.line_items { history: enabled }",
            ":root { history-table: main.__history }");

        var result = await ExecuteMutationAsync(
            "mutation { line_items(update: { order_id: 7, line_no: 2, qty: 9 }) }", model);

        result.Errors.Should().BeNullOrEmpty();
        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle();

        // entity_id is a JSON OBJECT, not a scalar — a composite key records losslessly.
        var entityId = Json(rows[0].EntityId);
        entityId["order_id"].GetInt64().Should().Be(7);
        entityId["line_no"].GetInt64().Should().Be(2);
        JsonArray(rows[0].ChangedColumns).Should().Equal("qty");
    }

    [Fact]
    public async Task NullTransitions_AreRecordedAsChanges_InBothDirections()
    {
        var model = await LoadModelAsync(
            "main.orders { history: enabled; history-columns: status }",
            ":root { history-table: main.__history }");

        // value → null
        var cleared = await ExecuteMutationAsync("mutation { orders(update: { id: 1, status: null }) }", model);
        cleared.Errors.Should().BeNullOrEmpty();

        // null → value
        var set = await ExecuteMutationAsync("mutation { orders(update: { id: 1, status: \"shipped\" }) }", model);
        set.Errors.Should().BeNullOrEmpty();

        var rows = await HistoryRowsAsync();
        rows.Should().HaveCount(2);

        Json(rows[0].Before)["status"].GetString().Should().Be("packing");
        Json(rows[0].After)["status"].ValueKind.Should().Be(JsonValueKind.Null, "the value was cleared");
        JsonArray(rows[0].ChangedColumns).Should().Equal("status");

        Json(rows[1].Before)["status"].ValueKind.Should().Be(JsonValueKind.Null);
        Json(rows[1].After)["status"].GetString().Should().Be("shipped");
        JsonArray(rows[1].ChangedColumns).Should().Equal("status");
    }

    [Fact]
    public async Task SoftDelete_IsRecordedAsAnUpdate_ShowingTheDeletedAtColumnMove()
    {
        // A soft delete is an UPDATE at the row level (deleted_at moves), so that is what
        // the trail records — 'history: delete' alone would record nothing here.
        var model = await LoadModelAsync(
            "main.orders { soft-delete: deleted_at; history: enabled }",
            ":root { history-table: main.__history }");

        var result = await ExecuteMutationAsync(
            "mutation { orders(delete: { id: 1 }) }", model,
            transformers: new IMutationTransformer[] { new SoftDeleteMutationTransformer() });

        result.Errors.Should().BeNullOrEmpty();
        (await RowCountAsync("orders", "id = 1")).Should().Be(1, "the row survives a soft delete");
        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle();
        rows[0].Op.Should().Be("update", "a soft delete is an update of deleted_at, not a row removal");
        Json(rows[0].Before)["deleted_at"].ValueKind.Should().Be(JsonValueKind.Null);
        Json(rows[0].After)["deleted_at"].GetString().Should().NotBeNullOrEmpty();
        JsonArray(rows[0].ChangedColumns).Should().Contain("deleted_at");
    }

    [Fact]
    public async Task PerTableHistoryTable_ReceivesItsOwnTablesRows_NotTheSharedDefault()
    {
        var model = await LoadModelAsync(
            "main.orders { history: enabled; history-table: main.orders_history }",
            ":root { history-table: main.__history }");

        var result = await ExecuteMutationAsync("mutation { orders(update: { id: 1, status: \"shipped\" }) }", model);

        result.Errors.Should().BeNullOrEmpty();
        (await HistoryRowsAsync("orders_history")).Should().ContainSingle("the table's own override wins");
        (await HistoryRowsAsync("__history")).Should().BeEmpty("the shared default is not used by this table");
    }

    [Fact]
    public async Task EncryptedColumn_IsRecordedAsCiphertext_NeverPlaintext()
    {
        // The trail reads the row back from the database, where an encrypted column holds
        // ciphertext. It must stay ciphertext in the trail, or the history table would be a
        // plaintext side-channel around field encryption.
        const string plaintext = "123-45-6789";
        var manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(FixedRootKey()), new InMemoryDataEncryptionKeyStore());
        var model = await LoadModelAsync(
            "main.secrets.ssn { encrypt: aes-256-gcm; key-ref: config:pii }",
            "main.secrets { history: enabled }",
            ":root { history-table: main.__history }");

        var result = await ExecuteMutationAsync(
            $"mutation {{ secrets(insert: {{ ssn: \"{plaintext}\" }}) }}", model,
            transformers: new IMutationTransformer[] { new EncryptOnWriteMutationTransformer() },
            keyManager: manager);

        result.Errors.Should().BeNullOrEmpty();
        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle();

        var recorded = Json(rows[0].After)["ssn"].GetString();
        recorded.Should().NotBeNull().And.NotBe(plaintext, "the trail records the value as stored: ciphertext");

        // And it is the very envelope stored in the column — decryptable, not garbage.
        FieldCipher.Decrypt(manager.GetDataKey(KeyRef), recorded!, CryptoAad.Build("main", "secrets", "ssn"))
            .Should().Be(plaintext);
    }

    [Fact]
    public async Task PredicateScopedDelete_IsVetoed_BeforeAnythingIsWritten()
    {
        // A write scoped only by a predicate can match an unbounded set, which the writer
        // cannot enumerate into per-row before-images, so the hook vetoes it: better a
        // refused write than a tracked table changed with nothing in the trail to show for
        // it. The GraphQL delete field requires the key, so this reaches the hook only
        // through an entry point that can supply arbitrary predicate data (a mutation
        // intent from a protocol adapter) — exercise the guard at the hook itself.
        var model = await LoadModelAsync(
            "main.orders { history: enabled }",
            ":root { history-table: main.__history }");
        var factory = new SqliteDbConnFactory(ConnString);

        await using var conn = (SqliteConnection)factory.GetConnection();
        await conn.OpenAsync();
        await using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync();

        var errors = await new HistoryMutationHook().BeforeCommitAsync(new MutationObserverContext
        {
            Table = model.GetTableFromDbName("orders"),
            MutationType = MutationType.Delete,
            Data = new Dictionary<string, object?> { ["status"] = "packing" }, // no primary key
            Result = null,
            UserContext = new Dictionary<string, object?>(),
            Connection = conn,
            Transaction = transaction,
            Model = model,
            Dialect = factory.Dialect,
            MutationState = MutationObserverContext.NewMutationState(),
        });

        errors.Should().ContainSingle()
            .Which.Should().Contain("primary key");
        (await HistoryRowsAsync()).Should().BeEmpty("the write is vetoed before anything is recorded");
    }

    [Fact]
    public async Task NoUserAuditKeyConfigured_RecordsNullActor_NotAClientSuppliedValue()
    {
        // Without a model-level user-audit-key there is no trustworthy actor, so the trail
        // records none — it must never adopt a client-supplied identity.
        var model = await LoadModelAsync(
            "main.orders { history: enabled }",
            ":root { history-table: main.__history }");

        var result = await ExecuteMutationAsync(
            "mutation { orders(update: { id: 1, status: \"shipped\" }) }", model,
            userContext: new Dictionary<string, object?> { ["sub"] = "impersonated" });

        result.Errors.Should().BeNullOrEmpty();
        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle();
        rows[0].Actor.Should().BeNull();
    }

    private static byte[] FixedRootKey()
    {
        var key = new byte[FieldCipher.KeySize];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
        return key;
    }

    private async Task<ExecutionResult> ExecuteMutationAsync(
        string mutation,
        IDbModel model,
        IMutationTransformer[]? transformers = null,
        EnvelopeKeyManager? keyManager = null,
        IDictionary<string, object?>? userContext = null)
    {
        var schema = DbSchema.FromModel(model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = transformers ?? Array.Empty<IMutationTransformer>(),
        });
        if (keyManager != null)
            services.AddSingleton(keyManager);
        services.AddSingleton<HistoryMutationHook>();
        services.AddSingleton<IBeforeCommitMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<IInTransactionMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<BeforeCommitMutationHooks>(sp => new BeforeCommitMutationHooks(
            sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        services.AddSingleton<InTransactionMutationHooks>(sp => new InTransactionMutationHooks(
            sp.GetServices<IInTransactionMutationHook>().ToArray()));
        await using var provider = services.BuildServiceProvider();

        var executor = new DocumentExecuter();
        return await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = userContext ?? new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema),
            });
        });
    }
}
