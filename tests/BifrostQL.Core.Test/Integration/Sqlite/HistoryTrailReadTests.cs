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
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// The generated trail read field (<c>&lt;table&gt;History</c>): it exists only for
/// history-enabled tables, always narrows a shared history table to the tracked table's
/// own rows (the entity discriminator is server-side and cannot be widened by client
/// filters), authorizes tenant-scoped trails fail-closed by the materialized scope
/// column, and passes recorded before/after images through the same decrypt/mask
/// policy as base-table reads — never raw ciphertext, never plaintext beyond the
/// caller's rights.
/// </summary>
public sealed class HistoryTrailReadTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_history_read_test;Mode=Memory;Cache=Shared";
    private const string KeyRef = "config:pii";
    private const string Plaintext = "123-45-6789";
    private SqliteConnection _keepAlive = null!;
    private EnvelopeKeyManager _keyManager = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        foreach (var drop in new[]
                 {
                     "orders", "gadgets", "widgets", "tenant_docs", "secrets", "gizmos", "gizmosHistory",
                     "audit_trail", "scoped_history",
                 })
            await Exec($"DROP TABLE IF EXISTS {drop}");

        await Exec("CREATE TABLE orders (id INTEGER PRIMARY KEY, status TEXT NULL)");
        await Exec("CREATE TABLE gadgets (id INTEGER PRIMARY KEY, name TEXT NULL)");
        await Exec("CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NULL)");
        await Exec("CREATE TABLE tenant_docs (id INTEGER PRIMARY KEY, body TEXT NULL, tenant_id INTEGER NULL)");
        await Exec("CREATE TABLE secrets (id INTEGER PRIMARY KEY, ssn TEXT NULL)");
        // A real table whose generated query field collides with gizmos' trail read field.
        await Exec("CREATE TABLE gizmos (id INTEGER PRIMARY KEY, name TEXT NULL)");
        await Exec("CREATE TABLE gizmosHistory (id INTEGER PRIMARY KEY, name TEXT NULL)");
        await Exec(HistoryDdl("audit_trail"));
        await Exec(HistoryDdl("scoped_history", extraColumns: ",\n    tenant_id INTEGER NULL"));

        await Exec("INSERT INTO orders(id, status) VALUES (1, 'packing')");
        await Exec("INSERT INTO gadgets(id, name) VALUES (1, 'sprocket')");

        _keyManager = new EnvelopeKeyManager(
            new ConfigRootKeyProvider(FixedRootKey()), new InMemoryDataEncryptionKeyStore());
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

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

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<IDbModel> LoadModelAsync(params string[] metadata) =>
        await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(metadata)).LoadAsync();

    private static byte[] FixedRootKey()
    {
        var key = new byte[FieldCipher.KeySize];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
        return key;
    }

    private async Task<ExecutionResult> ExecuteMutationAsync(
        string mutation, IDbModel model, IDictionary<string, object?>? userContext = null)
    {
        var schema = DbSchema.FromModel(model);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new EncryptOnWriteMutationTransformer() },
        });
        services.AddSingleton(_keyManager);
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
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema),
            });
        });
    }

    private async Task<ExecutionResult> ExecuteQueryAsync(
        string query, IDbModel model, IDictionary<string, object?>? userContext = null)
    {
        var schema = DbSchema.FromModel(model);

        var services = new ServiceCollection();
        services.AddSingleton(_keyManager);
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.RequestServices = provider;
            options.UserContext = userContext ?? new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema),
            });
        });
    }

    private static JsonElement Data(ExecutionResult result, string field)
    {
        result.Errors.Should().BeNullOrEmpty();
        var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        return doc.RootElement.GetProperty("data").GetProperty(field).Clone();
    }

    private static List<JsonElement> Rows(ExecutionResult result, string field) =>
        Data(result, field).GetProperty("data").EnumerateArray().ToList();

    // ---------------------------------------------------------------------------
    // Field generation
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HistoryField_IsGeneratedOnlyForHistoryEnabledTables()
    {
        var model = await LoadModelAsync(
            "main.orders { history: enabled }",
            ":root { history-table: main.audit_trail }");

        var enabled = await ExecuteQueryAsync("query { ordersHistory { total } }", model);
        enabled.Errors.Should().BeNullOrEmpty("orders records history, so its trail read field exists");

        var disabled = await ExecuteQueryAsync("query { widgetsHistory { total } }", model);
        disabled.Errors.Should().NotBeNullOrEmpty("widgets records no history, so no trail read field is generated");
        disabled.Errors!.Single().Message.Should().Contain("widgetsHistory");
    }

    [Fact]
    public async Task FieldNameCollision_WithARealTable_FailsModelLoadNamingBoth()
    {
        var act = async () => await LoadModelAsync(
            "main.gizmos { history: enabled }",
            ":root { history-table: main.audit_trail }");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Contain("gizmosHistory", "the error must name the generated field / colliding table")
            .And.Contain("main.gizmos", "the error must name the history-enabled table");
    }

    // ---------------------------------------------------------------------------
    // Row shape, filters, sort, paging
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TrailRows_CarryTheHistoryContract_AndTheEntityDiscriminator()
    {
        var model = await LoadModelAsync(
            "main.orders { history: enabled }",
            ":root { history-table: main.audit_trail }");

        (await ExecuteMutationAsync("mutation { orders(update: { id: 1, status: \"shipped\" }) }", model))
            .Errors.Should().BeNullOrEmpty();

        var result = await ExecuteQueryAsync(
            "query { ordersHistory { total data { id entity entity_id op actor changed_at before after changed_columns } } }",
            model);

        var rows = Rows(result, "ordersHistory");
        rows.Should().ContainSingle();
        var row = rows[0];
        row.GetProperty("entity").GetString().Should().Be("main.orders");
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.GetProperty("entity_id").GetString()!)!
            ["id"].GetInt64().Should().Be(1);
        row.GetProperty("op").GetString().Should().Be("update");
        row.GetProperty("actor").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("changed_at").GetString().Should().NotBeNullOrEmpty();
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.GetProperty("before").GetString()!)!
            ["status"].GetString().Should().Be("packing");
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.GetProperty("after").GetString()!)!
            ["status"].GetString().Should().Be("shipped");
        JsonSerializer.Deserialize<string[]>(row.GetProperty("changed_columns").GetString()!)
            .Should().Contain("status");
    }

    [Fact]
    public async Task Filters_EntityIdEquality_OpEquality_AndChangedAtRange_Narrow()
    {
        var model = await LoadModelAsync(
            "main.orders { history: enabled }",
            ":root { history-table: main.audit_trail }");

        foreach (var mutation in new[]
                 {
                     "mutation { orders(insert: { status: \"new\" }) }", // becomes id 2
                     "mutation { orders(update: { id: 1, status: \"shipped\" }) }",
                     "mutation { orders(update: { id: 2, status: \"packing\" }) }",
                 })
            (await ExecuteMutationAsync(mutation, model)).Errors.Should().BeNullOrEmpty();

        var byOp = await ExecuteQueryAsync(
            "query { ordersHistory(filter: { op: { _eq: \"update\" } }) { total data { op } } }", model);
        Rows(byOp, "ordersHistory").Should().HaveCount(2)
            .And.OnlyContain(r => r.GetProperty("op").GetString() == "update");

        var byEntityId = await ExecuteQueryAsync(
            """query { ordersHistory(filter: { entity_id: { _eq: "{\"id\":2}" } }) { data { entity_id op } } }""",
            model);
        Rows(byEntityId, "ordersHistory").Should().HaveCount(2)
            .And.OnlyContain(r => r.GetProperty("entity_id").GetString() == "{\"id\":2}");

        var inRange = await ExecuteQueryAsync(
            "query { ordersHistory(filter: { changed_at: { _gte: \"2000-01-01\" } }) { total } }", model);
        Data(inRange, "ordersHistory").GetProperty("total").GetInt32().Should().Be(3);

        var outOfRange = await ExecuteQueryAsync(
            "query { ordersHistory(filter: { changed_at: { _lt: \"2000-01-01\" } }) { total } }", model);
        Data(outOfRange, "ordersHistory").GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task SortAndPaging_WorkLikeAnyGeneratedTableField()
    {
        var model = await LoadModelAsync(
            "main.orders { history: enabled }",
            ":root { history-table: main.audit_trail }");

        foreach (var status in new[] { "a", "b", "c" })
            (await ExecuteMutationAsync($"mutation {{ orders(update: {{ id: 1, status: \"{status}\" }}) }}", model))
                .Errors.Should().BeNullOrEmpty();

        var result = await ExecuteQueryAsync(
            "query { ordersHistory(sort: [id_desc], limit: 2, offset: 1) { total offset limit data { id } } }",
            model);

        var payload = Data(result, "ordersHistory");
        payload.GetProperty("total").GetInt32().Should().Be(3);
        payload.GetProperty("offset").GetInt32().Should().Be(1);
        payload.GetProperty("limit").GetInt32().Should().Be(2);
        var ids = payload.GetProperty("data").EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).ToList();
        ids.Should().HaveCount(2).And.BeInDescendingOrder();
    }

    // ---------------------------------------------------------------------------
    // Entity discriminator on a shared history table
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SharedHistoryTable_EachTablesField_ReturnsOnlyItsOwnRows()
    {
        var model = await LoadModelAsync(
            "main.orders { history: enabled }",
            "main.gadgets { history: enabled }",
            ":root { history-table: main.audit_trail }");

        (await ExecuteMutationAsync("mutation { orders(update: { id: 1, status: \"shipped\" }) }", model))
            .Errors.Should().BeNullOrEmpty();
        (await ExecuteMutationAsync("mutation { gadgets(update: { id: 1, name: \"cog\" }) }", model))
            .Errors.Should().BeNullOrEmpty();

        var orders = await ExecuteQueryAsync("query { ordersHistory { data { entity } } }", model);
        Rows(orders, "ordersHistory").Should().ContainSingle()
            .Which.GetProperty("entity").GetString().Should().Be("main.orders");

        var gadgets = await ExecuteQueryAsync("query { gadgetsHistory { data { entity } } }", model);
        Rows(gadgets, "gadgetsHistory").Should().ContainSingle()
            .Which.GetProperty("entity").GetString().Should().Be("main.gadgets");

        // The discriminator is ANDed server-side: a client filter can narrow but never
        // widen it to another table's trail rows.
        var crossEntity = await ExecuteQueryAsync(
            "query { ordersHistory(filter: { entity: { _eq: \"main.gadgets\" } }) { total } }", model);
        Data(crossEntity, "ordersHistory").GetProperty("total").GetInt32().Should().Be(0);
    }

    // ---------------------------------------------------------------------------
    // Tenant authorization (fail-closed)
    // ---------------------------------------------------------------------------

    private async Task<IDbModel> LoadTenantModelAsync() =>
        await LoadModelAsync(
            "main.tenant_docs { history: enabled; tenant-filter: tenant_id; history-table: main.scoped_history }");

    private async Task SeedTenantTrailAsync(IDbModel model)
    {
        foreach (var mutation in new[]
                 {
                     "mutation { tenant_docs(insert: { body: \"a\", tenant_id: 42 }) }",
                     "mutation { tenant_docs(insert: { body: \"b\", tenant_id: 7 }) }",
                 })
            (await ExecuteMutationAsync(mutation, model)).Errors.Should().BeNullOrEmpty();

        // A legacy trail row that predates the scope column: NULL scope.
        await Exec(
            "INSERT INTO scoped_history(entity, entity_id, op, changed_at, after, changed_columns, tenant_id) " +
            "VALUES ('main.tenant_docs', '{\"id\":99}', 'insert', '2001-01-01', '{\"body\":\"legacy\"}', '[\"body\"]', NULL)");
    }

    [Fact]
    public async Task TenantScopedTrail_IsInvisibleAcrossTenants_InBothDirections()
    {
        var model = await LoadTenantModelAsync();
        await SeedTenantTrailAsync(model);

        var tenant42 = await ExecuteQueryAsync(
            "query { tenant_docsHistory { total data { after } } }", model,
            new Dictionary<string, object?> { ["tenant_id"] = 42 });
        var rows42 = Rows(tenant42, "tenant_docsHistory");
        rows42.Should().ContainSingle().Which.GetProperty("after").GetString().Should().Contain("\"a\"");

        var tenant7 = await ExecuteQueryAsync(
            "query { tenant_docsHistory { total data { after } } }", model,
            new Dictionary<string, object?> { ["tenant_id"] = 7 });
        var rows7 = Rows(tenant7, "tenant_docsHistory");
        rows7.Should().ContainSingle().Which.GetProperty("after").GetString().Should().Contain("\"b\"");
    }

    [Fact]
    public async Task CallerWithNoTenantClaim_SeesZeroRows_FailClosed()
    {
        var model = await LoadTenantModelAsync();
        await SeedTenantTrailAsync(model);

        var result = await ExecuteQueryAsync(
            "query { tenant_docsHistory { total data { id } } }", model);

        var payload = Data(result, "tenant_docsHistory");
        payload.GetProperty("total").GetInt32().Should().Be(0);
        payload.GetProperty("data").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task NullScopeLegacyRows_AreInvisibleToScopedCallers()
    {
        var model = await LoadTenantModelAsync();
        await SeedTenantTrailAsync(model);

        var result = await ExecuteQueryAsync(
            "query { tenant_docsHistory { data { after } } }", model,
            new Dictionary<string, object?> { ["tenant_id"] = 42 });

        Rows(result, "tenant_docsHistory").Should().OnlyContain(
            r => !r.GetProperty("after").GetString()!.Contains("legacy"),
            "a NULL-scope row's tenant is unknown, so it fails closed for scoped callers");
    }

    // ---------------------------------------------------------------------------
    // Policy row scope (fail-fast at model load)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task PolicyRowScopedTable_EnablingHistory_FailsModelLoad()
    {
        var act = async () => await LoadModelAsync(
            "main.orders { history: enabled; policy-row-scope: status = {status} }",
            ":root { history-table: main.audit_trail }");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Contain("policy-row-scope").And.Contain("history")
            .And.Contain("main.orders");
    }

    // ---------------------------------------------------------------------------
    // Encrypted images (decrypt/mask per caller policy)
    // ---------------------------------------------------------------------------

    private async Task<(IDbModel Model, string Ciphertext)> SeedEncryptedTrailAsync()
    {
        var model = await LoadModelAsync(
            "main.secrets.ssn { encrypt: aes-256-gcm; key-ref: config:pii; mask: last4; unmask-role: compliance }",
            "main.secrets { history: enabled }",
            ":root { history-table: main.audit_trail }");

        (await ExecuteMutationAsync($"mutation {{ secrets(insert: {{ ssn: \"{Plaintext}\" }}) }}", model))
            .Errors.Should().BeNullOrEmpty();

        await using var cmd = new SqliteCommand("SELECT ssn FROM secrets WHERE id = 1", _keepAlive);
        var ciphertext = (string)(await cmd.ExecuteScalarAsync())!;
        return (model, ciphertext);
    }

    private static string ImageSsn(ExecutionResult result)
    {
        var rows = Rows(result, "secretsHistory");
        rows.Should().ContainSingle();
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            rows[0].GetProperty("after").GetString()!)!["ssn"].GetString()!;
    }

    [Fact]
    public async Task EncryptedImageValue_DecryptsForUnmaskRoleHolder()
    {
        var (model, _) = await SeedEncryptedTrailAsync();

        var result = await ExecuteQueryAsync(
            "query { secretsHistory { data { after } } }", model,
            new Dictionary<string, object?> { ["roles"] = new[] { "compliance" } });

        ImageSsn(result).Should().Be(Plaintext);
    }

    [Fact]
    public async Task EncryptedImageValue_IsMaskedForOtherCallers_AndNeverCiphertext()
    {
        var (model, ciphertext) = await SeedEncryptedTrailAsync();

        var result = await ExecuteQueryAsync(
            "query { secretsHistory { data { after } } }", model,
            new Dictionary<string, object?> { ["roles"] = Array.Empty<string>() });

        var value = ImageSsn(result);
        value.Should().Be("••••6789", "mask: last4 applies inside the image exactly as on a base-table read");
        value.Should().NotBe(Plaintext).And.NotBe(ciphertext);
    }
}
