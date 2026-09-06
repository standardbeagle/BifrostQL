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
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// The trail read field derives its projection from the request's selection set,
/// so task 01M1QT7MGM129G7X6NNNMBFJNC asked whether it drops columns the way the
/// aggregate resolver did (M8): <see cref="IResolveFieldContext.SubFields"/> is
/// keyed by RESPONSE KEY, so a resolver that LOOKS UP a schema name in it sees one
/// node and loses every other node's sub-selection.
///
/// It does not, for two independent reasons, and these facts pin both: the resolver
/// iterates every SubFields value rather than looking one up (so aliased
/// <c>data</c> nodes are all seen), and the execution engine merges same-response-key
/// selection sets before the resolver runs (so a flat <c>data</c> plus a fragment
/// spread arrives already unioned). Both are assumptions a future edit can break
/// silently, which is what these facts exist to catch — every one asserts the
/// GENERATED SQL TEXT, not only the response shape
/// (<c>.claude/rules/regression-test-non-vacuous.md</c>). They were written and run
/// against the pre-change resolver first: all five passed, which is the recorded
/// evidence that the reported defect is not present.
/// </summary>
public sealed class HistoryTrailSelectedColumnTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_history_selected_test;Mode=Memory;Cache=Shared";

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS orders");
        await Exec("DROP TABLE IF EXISTS audit_trail");
        await Exec("CREATE TABLE orders (id INTEGER PRIMARY KEY, status TEXT NULL)");
        await Exec(
            """
            CREATE TABLE audit_trail (
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
        await Exec("INSERT INTO orders(id, status) VALUES (1, 'packing')");

        _model = await new DbModelLoader(
            new SqliteDbConnFactory(ConnString),
            new MetadataLoader(new[]
            {
                "main.orders { history: enabled }",
                ":root { history-table: main.audit_trail }",
            })).LoadAsync();

        (await MutateAsync("mutation { orders(update: { id: 1, status: \"shipped\" }) }"))
            .Errors.Should().BeNullOrEmpty();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<ExecutionResult> MutateAsync(string mutation)
    {
        var schema = DbSchema.FromModel(_model);
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = Array.Empty<IMutationTransformer>(),
        });
        services.AddSingleton<HistoryMutationHook>();
        services.AddSingleton<IBeforeCommitMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<IInTransactionMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton(sp => new BeforeCommitMutationHooks(sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        services.AddSingleton(sp => new InTransactionMutationHooks(sp.GetServices<IInTransactionMutationHook>().ToArray()));
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, NullQueryTransformerService.Instance),
            });
        });
    }

    private async Task<ExecutionResult> QueryAsync(string query, List<string>? sqlLog = null)
    {
        var schema = DbSchema.FromModel(_model);
        IDbConnFactory factory = new SqliteDbConnFactory(ConnString);
        if (sqlLog != null)
            factory = new SqlLoggingConnFactory(factory, sqlLog);

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, NullQueryTransformerService.Instance),
            });
        });
    }

    /// <summary>
    /// The trail read's ROW statement. The reader batches the row SELECT and its
    /// COUNT into one command text, and the COUNT mentions columns the projection
    /// does not, so every fact asserts on the row statement alone.
    /// </summary>
    private static string RowStatement(List<string> sqlLog) =>
        sqlLog.Should().ContainSingle(s => s.Contains("\"audit_trail\"")).Which
            .Split("SELECT COUNT(*)")[0];

    private static JsonElement Payload(ExecutionResult result, string responseKey)
    {
        result.Errors.Should().BeNullOrEmpty();
        var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        return doc.RootElement.GetProperty("data").GetProperty(responseKey).Clone();
    }

    private static JsonElement SingleRow(ExecutionResult result, string responseKey) =>
        Payload(result, responseKey).GetProperty("data").EnumerateArray().Single();

    // ---------------------------------------------------------------------------
    // The response-key trap: every `data` node's sub-selection must be projected
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DataSelectedFlatAndViaFragmentSpread_ProjectsTheUnion()
    {
        var sql = new List<string>();
        var result = await QueryAsync(
            """
            query { ordersHistory { data { id op } ...Trail } }
            fragment Trail on audit_trail_paged { data { entity changed_at } }
            """, sql);

        var statement = RowStatement(sql);
        statement.Should().Contain("\"id\"").And.Contain("\"op\"")
            .And.Contain("\"entity\"").And.Contain("\"changed_at\"");

        var row = SingleRow(result, "ordersHistory");
        row.GetProperty("id").ValueKind.Should().NotBe(JsonValueKind.Null);
        row.GetProperty("op").GetString().Should().Be("update");
        row.GetProperty("entity").GetString().Should().Be("main.orders");
        row.GetProperty("changed_at").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task DataSelectedFlatAndViaInlineFragment_ProjectsTheUnion()
    {
        var sql = new List<string>();
        var result = await QueryAsync(
            "query { ordersHistory { data { id } ... on audit_trail_paged { data { op entity } } } }", sql);

        var statement = RowStatement(sql);
        statement.Should().Contain("\"id\"").And.Contain("\"op\"").And.Contain("\"entity\"");

        var row = SingleRow(result, "ordersHistory");
        row.GetProperty("id").ValueKind.Should().NotBe(JsonValueKind.Null);
        row.GetProperty("op").GetString().Should().Be("update");
        row.GetProperty("entity").GetString().Should().Be("main.orders");
    }

    [Fact]
    public async Task DataUnderTwoAliases_ProjectsEachAliasOwnSubSelection()
    {
        // Two response keys, so this shape already worked; it is the population the
        // broad path served and must keep working after the walk changes.
        var sql = new List<string>();
        var result = await QueryAsync(
            "query { ordersHistory { h1: data { id } h2: data { op entity } } }", sql);

        var statement = RowStatement(sql);
        statement.Should().Contain("\"id\"").And.Contain("\"op\"").And.Contain("\"entity\"");

        var payload = Payload(result, "ordersHistory");
        payload.GetProperty("h1").EnumerateArray().Single()
            .GetProperty("id").ValueKind.Should().NotBe(JsonValueKind.Null);
        var h2 = payload.GetProperty("h2").EnumerateArray().Single();
        h2.GetProperty("op").GetString().Should().Be("update");
        h2.GetProperty("entity").GetString().Should().Be("main.orders");
    }

    [Fact]
    public async Task DuplicateTrailColumnAcrossMergedNodes_ProjectsOneSqlColumn()
    {
        // `id` selected under both nodes is one result-set alias: a duplicate alias
        // collides in the reader's column index and surfaces as a generic DB error.
        var sql = new List<string>();
        var result = await QueryAsync(
            """
            query { ordersHistory { data { id op } ...Dup } }
            fragment Dup on audit_trail_paged { data { id entity } }
            """, sql);

        var statement = RowStatement(sql);
        // The projection renders `"<column>" "<alias>"`, so the pair is what counts —
        // a bare "id" also appears in the ORDER BY.
        statement.Split("\"id\" \"id\"").Should().HaveCount(2, "the id column is projected exactly once");

        var row = SingleRow(result, "ordersHistory");
        row.GetProperty("op").GetString().Should().Be("update");
        row.GetProperty("entity").GetString().Should().Be("main.orders");
    }

    [Fact]
    public async Task TypenameInsideData_IsSkipped_NotProjectedAsAColumn()
    {
        var sql = new List<string>();
        var result = await QueryAsync(
            "query { ordersHistory { data { __typename op } } }", sql);

        RowStatement(sql).Should().NotContain("__typename");
        var row = SingleRow(result, "ordersHistory");
        row.GetProperty("__typename").GetString().Should().Be("audit_trail");
        row.GetProperty("op").GetString().Should().Be("update");
    }
}
