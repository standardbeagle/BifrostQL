using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Server;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// The filtered set-update under the REAL <see cref="BifrostServiceCollectionExtensions.AddBifrostQL"/>
/// container — the wiring every host actually runs. The sibling
/// <see cref="FilteredUpdateExecutionTests"/> builds a bare <see cref="ServiceCollection"/> and
/// therefore never sees the built-in hook registrations (history, approval, deferred, CDC), which
/// production DI adds unconditionally; that blind spot is what let <c>updateWhere</c> ship dead
/// (finding H5). These tests execute against a provider built by <c>AddBifrostQL</c> so the
/// hook gate is exercised exactly as a host exercises it.
///
/// The gate is per-TABLE applicability, never global registration: a table that no registered
/// hook can act on takes the set-based path, and a table any hook DOES act on is still refused
/// with the unchanged message.
/// </summary>
public sealed class FilteredUpdateProductionDiTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_filtered_update_prod_di;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS orders");
        await Exec("DROP TABLE IF EXISTS __history");
        await Exec("""
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
        await Exec("""
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                status TEXT NOT NULL,
                total REAL NOT NULL
            )
            """);
        await Exec("""
            INSERT INTO orders(id, tenant_id, status, total) VALUES
                (0, 1, 'new', 5.0),
                (1, 1, 'new', 10.0),
                (2, 1, 'old', 20.0)
            """);
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string where)
    {
        await using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM orders WHERE {where}", _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// The production container: <c>AddBifrostQL</c> with auth disabled and a SQLite endpoint.
    /// Everything the mutation path resolves from request services — the transformer wraps and,
    /// crucially, <see cref="BeforeCommitMutationHooks"/> / <see cref="InTransactionMutationHooks"/>
    /// — comes from this registration, not from a hand-rolled subset.
    /// </summary>
    internal static ServiceProvider BuildProductionServices()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bifrost:DisableAuth"] = "true",
                ["Bifrost:Path"] = "/graphql",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBifrostQL(o => o
            .BindConfiguration(config.GetSection("Bifrost"))
            .BindConnectionString(ConnString)
            .BindProvider("sqlite"));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Loads the model with <paramref name="metadataRules"/> applied AT LOAD TIME. Module
    /// configs (history among them) are parsed and cached per table instance while the model
    /// loads, so metadata poked into a table afterwards is invisible to them — the rules are
    /// the only honest way to give a table its module opt-in here.
    /// </summary>
    private static async Task<IDbModel> LoadModelAsync(params string[] metadataRules)
    {
        var model = await new DbModelLoader(
            new SqliteDbConnFactory(ConnString), new MetadataLoader(metadataRules)).LoadAsync();
        model.GetTableFromDbName("orders").Metadata[MetadataKeys.FilteredUpdate.Enabled] =
            FilteredUpdateConfig.EnabledValue;
        return model;
    }

    private static async Task<ExecutionResult> ExecuteAsync(IDbModel model, string mutation)
    {
        var schema = DbSchema.FromModel(model);
        var factory = new SqliteDbConnFactory(ConnString);
        await using var provider = BuildProductionServices();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema, NullQueryTransformerService.Instance),
            });
        });
    }

    [Fact]
    public async Task UpdateWhere_UnderProductionDi_ExecutesOnATableNoHookActsOn()
    {
        var model = await LoadModelAsync();

        var result = await ExecuteAsync(model,
            "mutation { orders(updateWhere: { set: { status: \"paid\" }, where: { status: { _eq: \"new\" } } }) }");

        result.Errors.Should().BeNullOrEmpty(
            $"errors: {string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>())}");
        (await CountAsync("status = 'paid'")).Should().Be(2);
        (await CountAsync("id = 0 AND status = 'paid'")).Should().Be(1, "PK value 0 is a legitimate matching row");
        (await CountAsync("id = 2 AND status = 'old'")).Should().Be(1, "non-matching rows are untouched");
    }

    [Fact]
    public async Task UpdateWhere_UnderProductionDi_StillRefusesAHistoryTable()
    {
        var model = await LoadModelAsync(
            "main.orders { history: enabled }",
            ":root { history-table: main.__history }");

        var result = await ExecuteAsync(model,
            "mutation { orders(updateWhere: { set: { status: \"paid\" }, where: { status: { _eq: \"new\" } } }) }");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors![0].Message.Should().Contain(
            "not available while a mutation hook (approval, history, CDC) applies to this table",
            "the per-row refusal message is unchanged for a table a hook DOES act on");
        (await CountAsync("status = 'paid'")).Should().Be(0);
    }

    [Fact]
    public async Task UpdateWhere_UnderProductionDi_StillRefusesAnApprovalTable()
    {
        var model = await LoadModelAsync("main.orders { approval: enabled; approver-role: approver }");

        var result = await ExecuteAsync(model,
            "mutation { orders(updateWhere: { set: { status: \"paid\" }, where: { status: { _eq: \"new\" } } }) }");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors![0].Message.Should().Contain(
            "not available while a mutation hook (approval, history, CDC) applies to this table");
        (await CountAsync("status = 'paid'")).Should().Be(0);
    }
}
