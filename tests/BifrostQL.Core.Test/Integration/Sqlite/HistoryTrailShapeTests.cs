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

        foreach (var drop in new[] { "orders", "line_items", "secrets", "attachments", "tenant_docs", "__history", "orders_history", "scoped_history" })
            await Exec($"DROP TABLE IF EXISTS {drop}");

        await Exec("CREATE TABLE orders (id INTEGER PRIMARY KEY, status TEXT NULL, deleted_at TEXT NULL)");
        // A tenant-filtered table: its trail rows must materialize tenant_id.
        await Exec("CREATE TABLE tenant_docs (id INTEGER PRIMARY KEY, body TEXT NULL, tenant_id INTEGER NULL)");
        // A binary column: byte[] images come back as fresh array instances on every
        // read, so change detection must compare contents, not references.
        await Exec("CREATE TABLE attachments (id INTEGER PRIMARY KEY, payload BLOB NULL, note TEXT NULL)");
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
        // The history-table contract plus the tenant scope column (nullable: it also
        // serves tables without tenant metadata).
        await Exec(HistoryDdl("scoped_history", extraColumns: ",\n    tenant_id INTEGER NULL"));

        await Exec("INSERT INTO orders(id, status) VALUES (1, 'packing')");
        await Exec("INSERT INTO line_items(order_id, line_no, qty) VALUES (7, 2, 5)");
        await Exec("INSERT INTO attachments(id, payload, note) VALUES (1, X'DEADBEEF', 'v1')");
    }

    private static string HistoryDdl(string table, string extraColumns = "") =>
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
            changed_columns TEXT NULL{extraColumns}
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
    public async Task WritePathThatSkippedBeforeCommitPhase_FailsClosed_RatherThanRecordWithoutBeforeImage()
    {
        // Arrange: a write path that never ran the before-commit phase hands the
        // after-write phase an EMPTY mutation-state bag. Recording anyway would invent a
        // before-image the writer never read, so the hook must throw and roll the write
        // back — the fail-closed contract of RequireCapturedBeforeImage.
        var model = await LoadModelAsync(
            "main.orders { history: enabled }",
            ":root { history-table: main.__history }");
        var factory = new SqliteDbConnFactory(ConnString);

        await using var conn = (SqliteConnection)factory.GetConnection();
        await conn.OpenAsync();
        await using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync();

        // Act: the after-write phase alone, with a fresh (never-populated) state bag.
        var act = async () => await new HistoryMutationHook().AfterWriteInTransactionAsync(new MutationObserverContext
        {
            Table = model.GetTableFromDbName("orders"),
            MutationType = MutationType.Update,
            Data = new Dictionary<string, object?> { ["id"] = 1L, ["status"] = "shipped" },
            Result = 1,
            UserContext = new Dictionary<string, object?>(),
            Connection = conn,
            Transaction = transaction,
            Model = model,
            Dialect = factory.Dialect,
            MutationState = MutationObserverContext.NewMutationState(),
        });

        // Assert: the throw quotes the error contract.
        (await act.Should().ThrowAsync<BifrostExecutionError>())
            .WithMessage("*No before-image was captured*")
            .WithMessage("*Refusing to record a change without the row it replaced*");
        (await HistoryRowsAsync()).Should().BeEmpty("nothing may be recorded from a phase that fails closed");
    }

    [Fact]
    public async Task AfterImageReadBackFindsNoRow_FailsClosed_RefusingToRecord()
    {
        // Arrange: the after-image is READ BACK from the database rather than assembled
        // from the write inputs. If the key the writer resolves does not match a stored
        // row (here: an insert whose row never landed), the trail cannot show what was
        // actually stored — so the hook must throw rather than record a fabricated image.
        var model = await LoadModelAsync(
            "main.orders { history: enabled }",
            ":root { history-table: main.__history }");
        var factory = new SqliteDbConnFactory(ConnString);

        await using var conn = (SqliteConnection)factory.GetConnection();
        await conn.OpenAsync();
        await using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync();

        // Act: an insert of id 999 that never actually wrote a row.
        var act = async () => await new HistoryMutationHook().AfterWriteInTransactionAsync(new MutationObserverContext
        {
            Table = model.GetTableFromDbName("orders"),
            MutationType = MutationType.Insert,
            Data = new Dictionary<string, object?> { ["id"] = 999L, ["status"] = "ghost" },
            Result = null,
            UserContext = new Dictionary<string, object?>(),
            Connection = conn,
            Transaction = transaction,
            Model = model,
            Dialect = factory.Dialect,
            MutationState = MutationObserverContext.NewMutationState(),
        });

        // Assert
        (await act.Should().ThrowAsync<BifrostExecutionError>())
            .WithMessage("*refusing to record a change whose result cannot be read*");
        (await HistoryRowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task UnchangedBinaryColumn_IsNotReportedAsChanged()
    {
        // Arrange: the before- and after-images read the BLOB back as two distinct
        // byte[] instances with identical content. Reference equality would report the
        // payload as changed on every write; content comparison must keep it out.
        var model = await LoadModelAsync(
            "main.attachments { history: enabled }",
            ":root { history-table: main.__history }");

        // Act: move only the text column; the binary column is untouched.
        await RunHookedWriteCycleAsync(
            model, "attachments", MutationType.Update,
            new Dictionary<string, object?> { ["id"] = 1L, ["note"] = "v2" },
            "UPDATE attachments SET note = 'v2' WHERE id = 1");

        // Assert
        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle();
        JsonArray(rows[0].ChangedColumns).Should().Equal(new[] { "note" },
            "the unchanged binary column must not appear — byte arrays compare by content, not reference");
    }

    [Fact]
    public async Task ChangedBinaryColumn_IsReportedAsChanged()
    {
        // Arrange
        var model = await LoadModelAsync(
            "main.attachments { history: enabled }",
            ":root { history-table: main.__history }");

        // Act: move only the binary column.
        await RunHookedWriteCycleAsync(
            model, "attachments", MutationType.Update,
            new Dictionary<string, object?> { ["id"] = 1L, ["payload"] = new byte[] { 0xC0, 0xFF, 0xEE } },
            "UPDATE attachments SET payload = X'C0FFEE' WHERE id = 1");

        // Assert
        var rows = await HistoryRowsAsync();
        rows.Should().ContainSingle();
        JsonArray(rows[0].ChangedColumns).Should().Equal("payload");
    }

    [Fact]
    public async Task KeylessUpdate_OnUpdateRecordingTable_IsVetoedFailClosed()
    {
        // Arrange: a keyless (predicate-scoped) update on a table that RECORDS updates
        // can match an unbounded set the writer cannot enumerate into before-images, so
        // the hook must veto it — the same fail-closed guard as the predicate delete.
        var model = await LoadModelAsync(
            "main.orders { history: update }",
            ":root { history-table: main.__history }");
        var factory = new SqliteDbConnFactory(ConnString);

        await using var conn = (SqliteConnection)factory.GetConnection();
        await conn.OpenAsync();
        await using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync();

        // Act
        var errors = await new HistoryMutationHook().BeforeCommitAsync(new MutationObserverContext
        {
            Table = model.GetTableFromDbName("orders"),
            MutationType = MutationType.Update,
            Data = new Dictionary<string, object?> { ["status"] = "bulk" }, // no primary key
            Result = null,
            UserContext = new Dictionary<string, object?>(),
            Connection = conn,
            Transaction = transaction,
            Model = model,
            Dialect = factory.Dialect,
            MutationState = MutationObserverContext.NewMutationState(),
        });

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("must be scoped by its full primary key");
        (await HistoryRowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task KeylessUpdate_OnInsertOnlyTable_SkipsCaptureSilently_WriteSucceedsWithNoTrail()
    {
        // Arrange: an insert-only table reaches the capture phase for updates solely
        // because an upsert might flip to an insert. A KEYLESS update can never be an
        // upsert, and updates are not recorded here — so there is nothing to capture and
        // nothing to veto: the write proceeds and the trail stays empty.
        var model = await LoadModelAsync(
            "main.orders { history: insert }",
            ":root { history-table: main.__history }");

        // Act: the full two-phase cycle around a real keyless UPDATE.
        var errors = await RunHookedWriteCycleAsync(
            model, "orders", MutationType.Update,
            new Dictionary<string, object?> { ["status"] = "bulk" }, // no primary key
            "UPDATE orders SET status = 'bulk'");

        // Assert
        errors.Should().BeEmpty("a keyless update the table does not record has nothing to veto");
        (await RowCountAsync("orders", "status = 'bulk'")).Should().Be(1, "the write itself succeeds");
        (await HistoryRowsAsync()).Should().BeEmpty("updates are not opted in, so nothing is recorded");
    }

    /// <summary>
    /// Drives both in-transaction hook phases around a real SQL write, exactly as the
    /// single-row write path does: capture, write, record, commit — one shared
    /// mutation-state bag. Returns the before-commit veto errors; a veto skips the write.
    /// </summary>
    private async Task<IReadOnlyList<string>> RunHookedWriteCycleAsync(
        IDbModel model,
        string tableName,
        MutationType mutationType,
        Dictionary<string, object?> data,
        string writeSql)
    {
        var factory = new SqliteDbConnFactory(ConnString);
        var hook = new HistoryMutationHook();
        var state = MutationObserverContext.NewMutationState();

        await using var conn = (SqliteConnection)factory.GetConnection();
        await conn.OpenAsync();
        await using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync();

        MutationObserverContext Context(object? result) => new()
        {
            Table = model.GetTableFromDbName(tableName),
            MutationType = mutationType,
            Data = data,
            Result = result,
            UserContext = new Dictionary<string, object?>(),
            Connection = conn,
            Transaction = transaction,
            Model = model,
            Dialect = factory.Dialect,
            MutationState = state,
        };

        var errors = await hook.BeforeCommitAsync(Context(result: null));
        if (errors.Count > 0)
            return errors; // Vetoed: the write path would abort before any SQL runs.

        int affected;
        await using (var cmd = new SqliteCommand(writeSql, conn, transaction))
        {
            affected = await cmd.ExecuteNonQueryAsync();
        }

        await hook.AfterWriteInTransactionAsync(Context(result: affected));
        await transaction.CommitAsync();
        return errors;
    }

    private async Task<List<(string Op, long? TenantId)>> ScopedHistoryRowsAsync()
    {
        var rows = new List<(string, long?)>();
        await using var cmd = new SqliteCommand(
            "SELECT op, tenant_id FROM scoped_history ORDER BY id", _keepAlive);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1)));
        return rows;
    }

    [Fact]
    public async Task TenantFilteredTable_TrailRowsCarryTheTenantValue_OnInsertUpdateAndDelete()
    {
        // History reads are authorized by plain column predicates, so every trail row of
        // a tenant-filtered table must physically carry the tracked row's tenant value:
        // the after-image value on insert/update, the before-image value on delete. The
        // copy is independent of history-columns — body is the only tracked column here,
        // yet the scope still lands in the trail's own tenant_id column.
        var model = await LoadModelAsync(
            "main.tenant_docs { history: enabled; history-columns: body; tenant-filter: tenant_id; history-table: main.scoped_history }");

        foreach (var mutation in new[]
        {
            "mutation { tenant_docs(insert: { body: \"draft\", tenant_id: 42 }) }",
            "mutation { tenant_docs(update: { id: 1, body: \"final\" }) }",
            "mutation { tenant_docs(delete: { id: 1 }) }",
        })
        {
            var result = await ExecuteMutationAsync(mutation, model);
            result.Errors.Should().BeNullOrEmpty();
        }

        var rows = await ScopedHistoryRowsAsync();
        rows.Should().Equal(("insert", 42L), ("update", 42L), ("delete", 42L));

        // The scope is the physical column, not part of the (narrowed) JSON images.
        var images = await HistoryRowsAsync("scoped_history");
        Json(images[0].After).Keys.Should().BeEquivalentTo(new[] { "body" },
            "history-columns narrows the images; the tenant scope travels in its own column");
    }

    [Fact]
    public async Task NonTenantTable_OnAScopeCapableHistoryTable_LeavesTheScopeColumnNull()
    {
        // A tracked table WITHOUT tenant metadata copies nothing: the shared target's
        // scope column stays NULL and the trail shape is otherwise unchanged.
        var model = await LoadModelAsync(
            "main.orders { history: enabled; history-table: main.scoped_history }");

        var result = await ExecuteMutationAsync(
            "mutation { orders(update: { id: 1, status: \"shipped\" }) }", model);

        result.Errors.Should().BeNullOrEmpty();
        var rows = await ScopedHistoryRowsAsync();
        rows.Should().Equal(("update", (long?)null));
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
