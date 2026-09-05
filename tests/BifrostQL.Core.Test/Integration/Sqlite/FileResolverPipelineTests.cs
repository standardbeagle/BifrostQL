using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Approval;
using BifrostQL.Core.Modules.Cdc;
using BifrostQL.Core.Modules.History;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Storage;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Proves finding H2: the file upload/delete resolvers must execute their pointer
/// write through <c>TableMutationPipeline</c>, not through hand-rolled SQL on a
/// fresh connection. The hand-rolled write skipped every gate the pipeline owns —
/// the history-target guard, before-commit hooks (the approval gate), the
/// in-transaction hooks (change history, the CDC outbox), the transaction itself
/// and the cancellation token — so a file column was the one writable surface on
/// which an approval-gated table applied immediately, a history-tracked table left
/// no trail, and an event-emitting table emitted nothing.
///
/// Each case drives the REAL resolvers against a real SQLite database with the
/// hooks wired exactly as the host DI composes them, and asserts on committed row
/// state (pending_changes, __history, __outbox) plus the storage provider's
/// recorded calls — never on resolver internals.
/// </summary>
public sealed class FileResolverPipelineTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private RecordingStorageProvider _storage = null!;
    private FileStorageService _storageService = null!;

    // One fixture spanning the gate classes the hand-rolled write bypassed, plus the
    // key shapes protocol-adapter-security invariant 8's fixture rule requires:
    // composite key, single key, key value 0, a key column whose name is not a valid
    // ADO parameter identifier, and a target that already holds an object.
    private static readonly string[] Rules =
    {
        ":root { history-table: main.__history; outbox-table: main.__outbox }",
        "main.gated_docs { approval: enabled; approver-role: manager }",
        "main.pending_changes { state-column: state; initial-state: pending; states: pending, approved, rejected, expired; transitions: pending->approved|pending->rejected|pending->expired }",
        "main.hist_docs { history: enabled }",
        "main.cdc_docs { emit-events: insert,update,delete; event-payload: changed }",
        "main.ver_docs { concurrency-token: row_version }",
        "main.dec_docs { concurrency-token: row_version }",
        "main.dt_docs { concurrency-token: row_version }",
    };

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_file_pipeline_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
        _connFactory = new SqliteDbConnFactory(_connectionString);

        await Exec("CREATE TABLE gated_docs (id INTEGER PRIMARY KEY, file_data TEXT NULL)");
        await Exec("CREATE TABLE hist_docs (id INTEGER PRIMARY KEY, file_data TEXT NULL)");
        await Exec("CREATE TABLE cdc_docs (id INTEGER PRIMARY KEY, file_data TEXT NULL)");
        await Exec("CREATE TABLE ver_docs (id INTEGER PRIMARY KEY, row_version INTEGER NOT NULL, file_data TEXT NULL)");
        await Exec("CREATE TABLE dec_docs (id INTEGER PRIMARY KEY, row_version NUMERIC NOT NULL, file_data TEXT NULL)");
        await Exec("CREATE TABLE dt_docs (id INTEGER PRIMARY KEY, row_version DATETIME NOT NULL, file_data TEXT NULL)");
        // A key column whose name is not a valid ADO parameter identifier: the raw
        // `@{k}` placeholders the hand-rolled SQL built could never bind it.
        await Exec("CREATE TABLE space_docs (\"doc id\" INTEGER PRIMARY KEY, file_data TEXT NULL)");
        // Composite key, and a key value of 0 — the value at which a guard that reads
        // a returned KEY as an affected-row count misfires.
        await Exec("CREATE TABLE part_docs (doc_id INTEGER NOT NULL, part_id INTEGER NOT NULL, file_data TEXT NULL, PRIMARY KEY (doc_id, part_id))");

        await Exec(
            """
            CREATE TABLE pending_changes (
                id                INTEGER PRIMARY KEY,
                "table"           TEXT NOT NULL,
                op                TEXT NOT NULL,
                intended_payload  TEXT NOT NULL,
                requester         TEXT NULL,
                tenant            TEXT NULL,
                requester_context TEXT NULL,
                "state"           TEXT NOT NULL,
                approver          TEXT NULL,
                decided_at        TEXT NULL,
                reason            TEXT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE __history (
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
            """);
        await Exec(
            """
            CREATE TABLE __outbox (
                id            INTEGER PRIMARY KEY,
                aggregate     TEXT NOT NULL,
                op            TEXT NOT NULL,
                payload       TEXT NOT NULL,
                tenant        TEXT NULL,
                created_at    TEXT NOT NULL DEFAULT (datetime('now')),
                dispatched_at TEXT NULL,
                attempts      INTEGER NOT NULL DEFAULT 0,
                dead          INTEGER NOT NULL DEFAULT 0
            )
            """);

        _storage = new RecordingStorageProvider("recording");
        var providerFactory = new StorageProviderFactory();
        providerFactory.RegisterProvider(_storage);
        _storageService = new FileStorageService(providerFactory);
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> Scalar(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private async Task<long> CountAsync(string table, string where = "1 = 1")
        => Convert.ToInt64(await Scalar($"SELECT COUNT(*) FROM {table} WHERE {where}"));

    private static string Pointer(string fileKey) => new FileMetadata
    {
        FileKey = fileKey,
        ProviderType = "recording",
        BucketName = "bucket",
        Size = 10,
    }.ToJson();

    /// <summary>
    /// Loads the model and points every file column at the recording provider. The
    /// storage config is applied post-load because its value carries ';', the rule
    /// separator.
    /// </summary>
    private async Task<IDbModel> LoadModelAsync()
    {
        var model = await new DbModelLoader(_connFactory, new MetadataLoader(Rules)).LoadAsync();
        foreach (var table in new[] { "gated_docs", "hist_docs", "cdc_docs", "ver_docs", "dec_docs", "dt_docs", "space_docs", "part_docs" })
        {
            var column = model.GetTableFromDbName(table).ColumnLookup["file_data"];
            column.Metadata[MetadataKeys.Storage.Config] = "provider:recording;bucket:bucket";
        }
        return model;
    }

    // The built-in write chain plus every hook class the hand-rolled SQL bypassed,
    // composed as the host DI composes them.
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        });
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new PolicyMutationTransformer(),
                new SoftDeleteMutationTransformer(),
                new TenantMutationTransformer(),
                new ConcurrencyMutationTransformer(),
            },
        });
        services.AddSingleton<IBeforeCommitMutationHook, ApprovalInterceptMutationHook>();
        services.AddSingleton<HistoryMutationHook>();
        services.AddSingleton<IBeforeCommitMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<IInTransactionMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<IInTransactionMutationHook, OutboxMutationHook>();
        services.AddSingleton(sp => new BeforeCommitMutationHooks(
            sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        services.AddSingleton(sp => new InTransactionMutationHooks(
            sp.GetServices<IInTransactionMutationHook>().ToArray()));
        return services.BuildServiceProvider();
    }

    private FileResolverTestContext Context(
        IDbModel model, IServiceProvider services, string table, string recordId,
        Dictionary<string, object?>? extraArguments = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["table"] = table,
            ["column"] = "file_data",
            ["recordId"] = recordId,
        };
        foreach (var kv in extraArguments ?? new Dictionary<string, object?>())
            arguments[kv.Key] = kv.Value;

        return new FileResolverTestContext(_connFactory, model, arguments, services,
            new SqlExecutionManager(model, DbSchema.FromModel(model),
                new QueryTransformerService(services.GetRequiredService<IFilterTransformers>())));
    }

    private static Dictionary<string, object?> UploadArguments(string content = "hello") => new()
    {
        ["file"] = System.Text.Encoding.UTF8.GetBytes(content),
        ["filename"] = "note.txt",
        ["contentType"] = "text/plain",
    };

    // ---- the approval gate: a gated table takes NO immediate write ----

    [Fact]
    public async Task Delete_OnApprovalGatedTable_EnqueuesPendingChange_AndLeavesTheRowUntouched()
    {
        await Exec($"INSERT INTO gated_docs(id, file_data) VALUES (1, '{Pointer("gated.bin")}')");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileDeleteResolver(_storageService);

        var act = async () => await resolver.ResolveAsync(Context(model, services, "gated_docs", "1"));

        (await act.Should().ThrowAsync<BifrostExecutionError>())
            .Which.ErrorCode.Should().Be(ApprovalInterceptMutationHook.PendingApprovalCode);
        (await CountAsync("pending_changes")).Should().Be(1, "the gated clear must be enqueued, not applied");
        (await Scalar("SELECT file_data FROM gated_docs WHERE id = 1")).Should().Be(Pointer("gated.bin"));
        _storage.DeletedKeys.Should().BeEmpty("the blob must survive a write the gate has only enqueued");
    }

    [Fact]
    public async Task Upload_OnApprovalGatedTable_EnqueuesPendingChange_AndLeavesTheRowUntouched()
    {
        await Exec("INSERT INTO gated_docs(id, file_data) VALUES (2, NULL)");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileUploadResolver(_storageService);

        var act = async () => await resolver.ResolveAsync(
            Context(model, services, "gated_docs", "2", UploadArguments()));

        (await act.Should().ThrowAsync<BifrostExecutionError>())
            .Which.ErrorCode.Should().Be(ApprovalInterceptMutationHook.PendingApprovalCode);
        (await CountAsync("pending_changes")).Should().Be(1);
        (await Scalar("SELECT file_data FROM gated_docs WHERE id = 2")).Should().BeNull();
    }

    // ---- change history: the trail row the hand-rolled UPDATE never wrote ----

    [Fact]
    public async Task Delete_OnHistoryTrackedTable_WritesATrailRow()
    {
        await Exec($"INSERT INTO hist_docs(id, file_data) VALUES (1, '{Pointer("hist.bin")}')");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileDeleteResolver(_storageService);

        var result = await resolver.ResolveAsync(Context(model, services, "hist_docs", "1"));

        result.Should().Be(true);
        (await Scalar("SELECT file_data FROM hist_docs WHERE id = 1")).Should().BeNull();
        (await CountAsync("__history", "entity = 'main.hist_docs' AND op = 'update' AND entity_id LIKE '%1%'"))
            .Should().Be(1, "clearing a file pointer is a tracked change like any other update");
        _storage.DeletedKeys.Should().ContainSingle().Which.Should().Be("hist.bin");
    }

    [Fact]
    public async Task Upload_OverAnExistingObject_WritesATrailRow_AndRepointsTheRow()
    {
        await Exec($"INSERT INTO hist_docs(id, file_data) VALUES (2, '{Pointer("previous.bin")}')");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileUploadResolver(_storageService);

        var result = await resolver.ResolveAsync(
            Context(model, services, "hist_docs", "2", UploadArguments()));

        var upload = result.Should().BeOfType<FileUploadResult>().Subject;
        upload.Success.Should().BeTrue();
        (await Scalar("SELECT file_data FROM hist_docs WHERE id = 2")).As<string>()
            .Should().Contain(upload.FileKey, "the row must point at the newly uploaded object");
        (await CountAsync("__history", "entity = 'main.hist_docs' AND op = 'update' AND entity_id LIKE '%2%'")).Should().Be(1);
    }

    // ---- CDC outbox: the event the hand-rolled UPDATE never emitted ----

    [Fact]
    public async Task Delete_OnEventEmittingTable_WritesAnOutboxRow()
    {
        await Exec($"INSERT INTO cdc_docs(id, file_data) VALUES (1, '{Pointer("cdc.bin")}')");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileDeleteResolver(_storageService);

        await resolver.ResolveAsync(Context(model, services, "cdc_docs", "1"));

        (await CountAsync("__outbox", "aggregate = 'main.cdc_docs' AND op = 'update'"))
            .Should().Be(1, "a file-pointer clear is a data change subscribers must see");
    }

    [Fact]
    public async Task Upload_OnEventEmittingTable_WritesAnOutboxRow()
    {
        await Exec("INSERT INTO cdc_docs(id, file_data) VALUES (2, NULL)");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileUploadResolver(_storageService);

        await resolver.ResolveAsync(Context(model, services, "cdc_docs", "2", UploadArguments()));

        (await CountAsync("__outbox", "aggregate = 'main.cdc_docs' AND op = 'update'")).Should().Be(1);
    }

    // ---- key shapes the hand-rolled predicate could not address ----

    [Fact]
    public async Task Delete_KeyColumnWithASpace_ClearsThePointer()
    {
        await Exec($"INSERT INTO space_docs(\"doc id\", file_data) VALUES (7, '{Pointer("spaced.bin")}')");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileDeleteResolver(_storageService);

        var result = await resolver.ResolveAsync(Context(model, services, "space_docs", "7"));

        result.Should().Be(true);
        (await Scalar("SELECT file_data FROM space_docs WHERE \"doc id\" = 7")).Should().BeNull();
        _storage.DeletedKeys.Should().ContainSingle().Which.Should().Be("spaced.bin");
    }

    [Fact]
    public async Task Delete_KeyValueZero_ClearsThePointer()
    {
        await Exec($"INSERT INTO hist_docs(id, file_data) VALUES (0, '{Pointer("zero.bin")}')");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileDeleteResolver(_storageService);

        var result = await resolver.ResolveAsync(Context(model, services, "hist_docs", "0"));

        result.Should().Be(true, "key value 0 is a real row, not a zero-affected-rows signal");
        (await Scalar("SELECT file_data FROM hist_docs WHERE id = 0")).Should().BeNull();
    }

    [Fact]
    public async Task Delete_CompositeKey_ClearsOnlyTheAddressedRow()
    {
        await Exec($"INSERT INTO part_docs(doc_id, part_id, file_data) VALUES (4, 5, '{Pointer("target.bin")}')");
        await Exec($"INSERT INTO part_docs(doc_id, part_id, file_data) VALUES (4, 6, '{Pointer("sibling.bin")}')");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileDeleteResolver(_storageService);

        var result = await resolver.ResolveAsync(Context(model, services, "part_docs", "4-5"));

        result.Should().Be(true);
        (await Scalar("SELECT file_data FROM part_docs WHERE doc_id = 4 AND part_id = 5")).Should().BeNull();
        (await Scalar("SELECT file_data FROM part_docs WHERE doc_id = 4 AND part_id = 6")).Should().Be(Pointer("sibling.bin"));
        _storage.DeletedKeys.Should().ContainSingle().Which.Should().Be("target.bin");
    }

    // ---- M6: the concurrency-token table ----

    [Fact]
    public async Task Delete_OnConcurrencyTokenTable_WithTheCurrentToken_Succeeds_AndAdvancesTheToken()
    {
        await Exec($"INSERT INTO ver_docs(id, row_version, file_data) VALUES (1, 3, '{Pointer("ver.bin")}')");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileDeleteResolver(_storageService);

        var result = await resolver.ResolveAsync(Context(model, services, "ver_docs", "1",
            new Dictionary<string, object?> { ["concurrencyToken"] = "3" }));

        result.Should().Be(true);
        (await Scalar("SELECT file_data FROM ver_docs WHERE id = 1")).Should().BeNull();
        Convert.ToInt64(await Scalar("SELECT row_version FROM ver_docs WHERE id = 1"))
            .Should().Be(4, "a guarded write advances the token in the same statement");
        _storage.DeletedKeys.Should().ContainSingle().Which.Should().Be("ver.bin");
    }

    [Fact]
    public async Task Delete_OnConcurrencyTokenTable_WithAStaleToken_ConflictsAndKeepsTheObject()
    {
        await Exec($"INSERT INTO ver_docs(id, row_version, file_data) VALUES (1, 5, '{Pointer("ver.bin")}')");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileDeleteResolver(_storageService);

        var act = async () => await resolver.ResolveAsync(Context(model, services, "ver_docs", "1",
            new Dictionary<string, object?> { ["concurrencyToken"] = "3" }));

        var error = (await act.Should().ThrowAsync<BifrostExecutionError>()).Which;
        error.ErrorCode.Should().Be("CONFLICT", "a lost update is branchable, not a generic failure");
        // The message tells the loser THAT it lost, never the winning value (invariant 3).
        error.Message.Should().Contain("no longer matches").And.NotContain("5");
        (await Scalar("SELECT file_data FROM ver_docs WHERE id = 1")).Should().Be(Pointer("ver.bin"));
        _storage.DeletedKeys.Should().BeEmpty("a rejected write must not have destroyed the object first");
    }

    [Fact]
    public async Task Delete_OnConcurrencyTokenTable_WithoutAToken_IsRejected()
    {
        await Exec($"INSERT INTO ver_docs(id, row_version, file_data) VALUES (1, 3, '{Pointer("ver.bin")}')");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileDeleteResolver(_storageService);

        var act = async () => await resolver.ResolveAsync(Context(model, services, "ver_docs", "1"));

        (await act.Should().ThrowAsync<BifrostExecutionError>())
            .Which.Message.Should().Contain("concurrency token");
        (await Scalar("SELECT file_data FROM ver_docs WHERE id = 1")).Should().Be(Pointer("ver.bin"));
        _storage.DeletedKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_OnConcurrencyTokenTable_WithTheCurrentToken_Succeeds()
    {
        await Exec("INSERT INTO ver_docs(id, row_version, file_data) VALUES (2, 7, NULL)");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileUploadResolver(_storageService);

        var arguments = UploadArguments();
        arguments["concurrencyToken"] = "7";
        var result = await resolver.ResolveAsync(Context(model, services, "ver_docs", "2", arguments));

        var upload = result.Should().BeOfType<FileUploadResult>().Subject;
        (await Scalar("SELECT file_data FROM ver_docs WHERE id = 2")).As<string>()
            .Should().Contain(upload.FileKey);
        Convert.ToInt64(await Scalar("SELECT row_version FROM ver_docs WHERE id = 2")).Should().Be(8);
    }

    [Fact]
    public async Task Upload_OnConcurrencyTokenTable_WithAStaleToken_ConflictsAndRemovesTheJustUploadedObject()
    {
        await Exec("INSERT INTO ver_docs(id, row_version, file_data) VALUES (2, 9, NULL)");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileUploadResolver(_storageService);

        var arguments = UploadArguments();
        arguments["concurrencyToken"] = "7";
        var act = async () => await resolver.ResolveAsync(Context(model, services, "ver_docs", "2", arguments));

        (await act.Should().ThrowAsync<BifrostExecutionError>()).Which.ErrorCode.Should().Be("CONFLICT");
        (await Scalar("SELECT file_data FROM ver_docs WHERE id = 2")).Should().BeNull();
        _storage.DeletedKeys.Should().ContainSingle(
            "the object this call uploaded is unreferenced once the guarded write is rejected");
    }

    // ---- culture-invariant token parsing (CoerceToken) ----

    /// <summary>
    /// The token arrives on the GraphQL wire in invariant (dot-decimal) form regardless
    /// of host culture. Under a comma-decimal culture (de-DE), culture-sensitive
    /// <c>decimal.Parse</c> reads "1.5" as 15, so the guard predicate misses the row
    /// and a CORRECT token reads as CONFLICT — a lost write for the client. Parse must
    /// be invariant-culture.
    /// </summary>
    [Fact]
    public async Task Delete_OnDecimalTokenTable_UnderCommaDecimalCulture_TheCurrentDotDecimalToken_Succeeds()
    {
        await Exec($"INSERT INTO dec_docs(id, row_version, file_data) VALUES (1, 1.5, '{Pointer("dec.bin")}')");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileDeleteResolver(_storageService);

        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var result = await resolver.ResolveAsync(Context(model, services, "dec_docs", "1",
                new Dictionary<string, object?> { ["concurrencyToken"] = "1.5" }));

            result.Should().Be(true, "\"1.5\" is the invariant wire form of the stored token");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
        (await Scalar("SELECT file_data FROM dec_docs WHERE id = 1")).Should().BeNull();
        Convert.ToDecimal(await Scalar("SELECT row_version FROM dec_docs WHERE id = 1"),
                System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(2.5m, "a guarded write advances the decimal token in the same statement");
    }

    /// <summary>
    /// Temporal family: .NET parses ISO-8601 tokens culture-independently, so the
    /// culture-divergent wire shape is the day/month-ambiguous form a dot-decimal
    /// (en-US-style) client legitimately sends. Under de-DE, culture-sensitive
    /// <c>DateTimeOffset.Parse</c> reads "09/05/2026" as 9 May instead of 5 September,
    /// shifting the guard off the row — a correct token becomes a wrong one and the
    /// write reads as CONFLICT. Invariant parsing keeps the guard on the stored value.
    /// </summary>
    [Fact]
    public async Task Delete_OnDateTimeTokenTable_UnderCommaDecimalCulture_TheCurrentToken_Succeeds()
    {
        await using (var cmd = new SqliteCommand(
            "INSERT INTO dt_docs(id, row_version, file_data) VALUES (1, $ts, $p)", _keepAlive))
        {
            cmd.Parameters.AddWithValue("$ts", new DateTimeOffset(2026, 9, 5, 12, 34, 56, TimeSpan.Zero));
            cmd.Parameters.AddWithValue("$p", Pointer("dt.bin"));
            await cmd.ExecuteNonQueryAsync();
        }
        var stored = Convert.ToString(
            await Scalar("SELECT row_version FROM dt_docs WHERE id = 1"),
            System.Globalization.CultureInfo.InvariantCulture);
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileDeleteResolver(_storageService);

        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var result = await resolver.ResolveAsync(Context(model, services, "dt_docs", "1",
                new Dictionary<string, object?> { ["concurrencyToken"] = "09/05/2026 12:34:56 +00:00" }));

            result.Should().Be(true, "the token names 5 September 2026, the stored value");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
        (await Scalar("SELECT file_data FROM dt_docs WHERE id = 1")).Should().BeNull();
        Convert.ToString(await Scalar("SELECT row_version FROM dt_docs WHERE id = 1"),
                System.Globalization.CultureInfo.InvariantCulture)
            .Should().NotBe(stored, "a guarded write restamps the datetime token");
    }

    /// <summary>
    /// A table with no <c>concurrency-token</c> must reject the argument rather than
    /// ignore it: a client that believes it is guarding a write, and is not, is the
    /// exact lost update the token exists to prevent.
    /// </summary>
    [Fact]
    public async Task Delete_TokenSuppliedForATableWithoutOne_IsRejected()
    {
        await Exec($"INSERT INTO hist_docs(id, file_data) VALUES (5, '{Pointer("untokened.bin")}')");
        var model = await LoadModelAsync();
        var services = BuildServices();
        var resolver = new FileDeleteResolver(_storageService);

        var act = async () => await resolver.ResolveAsync(Context(model, services, "hist_docs", "5",
            new Dictionary<string, object?> { ["concurrencyToken"] = "3" }));

        (await act.Should().ThrowAsync<BifrostExecutionError>())
            .Which.Message.Should().Contain("does not use a concurrency token");
        (await Scalar("SELECT file_data FROM hist_docs WHERE id = 5")).Should().Be(Pointer("untokened.bin"));
    }

    // ---- compensation touches only what this call created (invariant 8a) ----

    [Fact]
    public async Task Upload_WhenThePointerWriteIsScopedAway_RemovesOnlyTheJustUploadedObject()
    {
        await Exec($"INSERT INTO hist_docs(id, file_data) VALUES (9, '{Pointer("pre-existing.bin")}')");
        var model = await LoadModelAsync();
        var services = new ServiceCollection()
            .AddSingleton<IFilterTransformers>(new FilterTransformersWrap
            {
                Transformers = Array.Empty<IFilterTransformer>(),
            })
            .AddSingleton<IMutationTransformers>(new MutationTransformersWrap
            {
                Transformers = new IMutationTransformer[] { new ScopeAwayTransformer() },
            })
            .BuildServiceProvider();
        var resolver = new FileUploadResolver(_storageService);

        var act = async () => await resolver.ResolveAsync(
            Context(model, services, "hist_docs", "9", UploadArguments()));

        await act.Should().ThrowAsync<BifrostExecutionError>();
        // The row still points at the object it held, and that object was NOT the
        // compensation's target: only the key this call just wrote is removed.
        (await Scalar("SELECT file_data FROM hist_docs WHERE id = 9")).Should().Be(Pointer("pre-existing.bin"));
        _storage.DeletedKeys.Should().NotContain("pre-existing.bin");
        _storage.DeletedKeys.Should().ContainSingle("only the object this call uploaded may be compensated");
    }

    /// <summary>Narrows every update to no rows, standing in for a policy/tenant scope-away.</summary>
    private sealed class ScopeAwayTransformer : IMutationTransformer
    {
        public string ModuleName => "test-scope-away";
        public int Priority => 500;
        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context) => true;

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data,
            MutationTransformContext context)
            => ValueTask.FromResult(new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data,
                AdditionalFilter = TableFilterFactory.Equals(table.DbName, "id", -1),
            });
    }
}
