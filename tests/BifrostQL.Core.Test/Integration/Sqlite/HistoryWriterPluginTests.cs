using System.Data.Common;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.History;
using BifrostQL.Core.QueryModel;
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
/// A host-registered <see cref="IHistoryWriter"/> selected by <c>history-writer</c> stores
/// the changes <see cref="HistoryMutationHook"/> records, in the mutation's transaction,
/// using only the public hook context — the shape an app-owned audit table needs.
/// </summary>
public sealed class HistoryWriterPluginTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_history_writer_plugin_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS orders");
        await Exec("DROP TABLE IF EXISTS app_audit");
        await Exec("DROP TABLE IF EXISTS __history");
        await Exec(
            """
            CREATE TABLE orders (
                id     INTEGER PRIMARY KEY,
                status TEXT NULL CHECK (status <> 'boom'),
                total  REAL NULL
            )
            """);
        // An app-shaped audit table: not the history-table contract.
        await Exec(
            """
            CREATE TABLE app_audit (
                audit_id   INTEGER PRIMARY KEY,
                table_name TEXT NOT NULL,
                row_id     INTEGER NOT NULL,
                action     TEXT NOT NULL,
                who        TEXT NULL,
                old_value  TEXT NULL,
                new_value  TEXT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE __history (
                id INTEGER PRIMARY KEY, entity TEXT NOT NULL, entity_id TEXT NOT NULL, op TEXT NOT NULL,
                actor TEXT NULL, changed_at TEXT NOT NULL, before TEXT NULL, after TEXT NULL, changed_columns TEXT NULL
            )
            """);
        await Exec("INSERT INTO orders(id, status, total) VALUES (1, 'packing', 240.0)");
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    /// <summary>Writes one app_audit row per change with a plain command on the hook's transaction.</summary>
    private sealed class AppAuditWriter : IHistoryWriter
    {
        public string Name => "app-audit";

        public async ValueTask WriteAsync(HistoryRecord record, MutationObserverContext context)
        {
            await using var cmd = context.Connection!.CreateCommand();
            cmd.Transaction = context.Transaction;
            cmd.CommandText =
                "INSERT INTO app_audit(table_name, row_id, action, who, old_value, new_value) " +
                "VALUES (@t, @id, @a, @w, @o, @n)";
            Add(cmd, "@t", record.Table.DbName);
            Add(cmd, "@id", Convert.ToInt64(record.Key["id"]));
            Add(cmd, "@a", record.Operation.ToString().ToUpperInvariant());
            Add(cmd, "@w", record.Actor?.ToString());
            Add(cmd, "@o", record.Before is null ? null : JsonSerializer.Serialize(record.Before));
            Add(cmd, "@n", record.After is null ? null : JsonSerializer.Serialize(record.After));
            await cmd.ExecuteNonQueryAsync();
        }

        public ValueTask EraseAsync(HistoryRecord record, MutationObserverContext context) =>
            throw new InvalidOperationException("app_audit is append-only");

        private static void Add(DbCommand cmd, string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }

    private sealed class ThrowingWriter : IHistoryWriter
    {
        public string Name => "app-audit";
        public ValueTask WriteAsync(HistoryRecord record, MutationObserverContext context) =>
            throw new BifrostExecutionError("audit store unavailable");
        public ValueTask EraseAsync(HistoryRecord record, MutationObserverContext context) =>
            throw new BifrostExecutionError("audit store unavailable");
    }

    private sealed class NamedWriter(string name) : IHistoryWriter
    {
        public string Name => name;
        public ValueTask WriteAsync(HistoryRecord record, MutationObserverContext context) => ValueTask.CompletedTask;
        public ValueTask EraseAsync(HistoryRecord record, MutationObserverContext context) => ValueTask.CompletedTask;
    }

    private sealed record AuditRow(string Table, long RowId, string Action, string? Who, string? Old, string? New);

    private async Task<List<AuditRow>> AuditRowsAsync()
    {
        var rows = new List<AuditRow>();
        await using var cmd = new SqliteCommand(
            "SELECT table_name, row_id, action, who, old_value, new_value FROM app_audit ORDER BY audit_id", _keepAlive);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new AuditRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        return rows;
    }

    private static Dictionary<string, JsonElement> Json(string? value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value!)!;

    [Fact]
    public async Task PluginWriter_StoresUpdateInsertAndDelete_InItsOwnShape()
    {
        var model = await LoadModelAsync("main.orders { history: enabled; history-writer: app-audit }",
            ":root { user-audit-key: sub }");
        var user = new Dictionary<string, object?> { ["sub"] = "u_7" };

        (await ExecuteMutationAsync("mutation { orders(update: { id: 1, status: \"shipped\" }) }", model, user))
            .Errors.Should().BeNullOrEmpty();
        (await ExecuteMutationAsync("mutation { orders(insert: { status: \"new\", total: 5 }) }", model, user))
            .Errors.Should().BeNullOrEmpty();
        (await ExecuteMutationAsync("mutation { orders(delete: { id: 1 }) }", model, user))
            .Errors.Should().BeNullOrEmpty();

        var rows = await AuditRowsAsync();
        rows.Select(r => r.Action).Should().Equal("UPDATE", "INSERT", "DELETE");
        rows.Should().OnlyContain(r => r.Table == "orders" && r.Who == "u_7");

        Json(rows[0].Old)["status"].GetString().Should().Be("packing");
        Json(rows[0].New)["status"].GetString().Should().Be("shipped");
        rows[1].RowId.Should().BeGreaterThan(1, "the insert's generated key reaches the writer");
        rows[1].Old.Should().BeNull();
        Json(rows[2].Old)["status"].GetString().Should().Be("shipped");
        rows[2].New.Should().BeNull();
    }

    [Fact]
    public async Task ModelLevelWriter_IsTheDefault_AndATableCanSelectTheBuiltInWriter()
    {
        await Exec("CREATE TABLE IF NOT EXISTS notes (id INTEGER PRIMARY KEY, body TEXT NULL)");
        await Exec("DELETE FROM notes");
        await Exec("INSERT INTO notes(id, body) VALUES (1, 'a')");
        var model = await LoadModelAsync(
            "main.orders { history: enabled }",
            "main.notes { history: enabled; history-writer: table }",
            ":root { history-writer: app-audit; history-table: main.__history }");

        (await ExecuteMutationAsync("mutation { orders(update: { id: 1, status: \"shipped\" }) }", model))
            .Errors.Should().BeNullOrEmpty();
        (await ExecuteMutationAsync("mutation { notes(update: { id: 1, body: \"b\" }) }", model))
            .Errors.Should().BeNullOrEmpty();

        (await AuditRowsAsync()).Should().ContainSingle().Which.Table.Should().Be("orders");
        (await CountAsync("__history", "entity = 'main.notes'")).Should().Be(1);
        (await CountAsync("__history", "entity = 'main.orders'")).Should().Be(0);
        await Exec("DROP TABLE notes");
    }

    [Fact]
    public async Task WriterFailure_RollsBackTheChange()
    {
        var model = await LoadModelAsync("main.orders { history: enabled; history-writer: app-audit }");

        var result = await ExecuteMutationAsync(
            "mutation { orders(update: { id: 1, status: \"shipped\" }) }", model, writer: new ThrowingWriter());

        result.Errors.Should().NotBeNullOrEmpty();
        (await CountAsync("orders", "status = 'packing'")).Should().Be(1, "a change that cannot be recorded must not commit");
    }

    [Fact]
    public async Task UnknownWriterName_RefusesTheWrite()
    {
        var model = await LoadModelAsync("main.orders { history: enabled; history-writer: nobody }");

        var result = await ExecuteMutationAsync("mutation { orders(update: { id: 1, status: \"shipped\" }) }", model);

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Select(e => e.Message).Should().Contain(m => m.Contains("history-writer: nobody"));
        (await CountAsync("orders", "status = 'packing'")).Should().Be(1);
    }

    [Fact]
    public async Task HistoryTable_OnAPluginWriterTable_IsRejectedAtModelLoad()
    {
        var act = () => LoadModelAsync(
            "main.orders { history: enabled; history-writer: app-audit; history-table: main.__history }");

        (await act.Should().ThrowAsync<Exception>())
            .Which.Message.Should().Contain("history-table").And.Contain("has no effect");
    }

    [Fact]
    public async Task PluginWriterTable_NeedsNoHistoryTable_AndGetsNoTrailReadField()
    {
        var model = await LoadModelAsync("main.orders { history: enabled; history-writer: app-audit }");
        var orders = model.GetTableFromDbName("orders");

        HistorySurface.ResolveReadTarget(model, orders).Should().BeNull();
        DbSchema.FromModel(model).Query.Fields.Should().NotContain(f => f.Name == HistorySurface.HistoryFieldName(orders));
    }

    [Fact]
    public void WriterNames_AreUnique_AndTheBuiltInNameIsReserved()
    {
        var builtIn = () => new HistoryMutationHook(new IHistoryWriter[] { new NamedWriter("TABLE") });
        var duplicate = () => new HistoryMutationHook(new IHistoryWriter[] { new NamedWriter("x"), new NamedWriter("X") });

        builtIn.Should().Throw<InvalidOperationException>().WithMessage("*named 'TABLE'*");
        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*named 'X'*");
    }

    private static async Task<IDbModel> LoadModelAsync(params string[] metadata) =>
        await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(metadata)).LoadAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string table, string where)
    {
        await using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM {table} WHERE {where}", _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<ExecutionResult> ExecuteMutationAsync(
        string mutation, IDbModel model, IDictionary<string, object?>? userContext = null, IHistoryWriter? writer = null)
    {
        var schema = DbSchema.FromModel(model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = Array.Empty<IMutationTransformer>(),
        });
        // The host's writer is an ordinary IHistoryWriter singleton; the hook receives it
        // through DI exactly as BifrostServiceRegistrar wires it.
        services.AddSingleton<IHistoryWriter>(writer ?? new AppAuditWriter());
        services.AddSingleton<HistoryMutationHook>();
        services.AddSingleton<IBeforeCommitMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<IInTransactionMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<BeforeCommitMutationHooks>(sp => new BeforeCommitMutationHooks(
            sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        services.AddSingleton<InTransactionMutationHooks>(sp => new InTransactionMutationHooks(
            sp.GetServices<IInTransactionMutationHook>().ToArray()));
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = userContext ?? new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema, NullQueryTransformerService.Instance),
            });
        });
    }
}
