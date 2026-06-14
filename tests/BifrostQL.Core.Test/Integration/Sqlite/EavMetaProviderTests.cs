using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Modules.Eav;
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
/// End-to-end proof that the dead <c>_meta: String</c> stub on EAV-parent tables
/// now resolves through the provider-computed-column pipeline (the same mechanism
/// as <see cref="StateMachineTransitionsProvider"/>). The <see cref="EavMetaProvider"/>
/// reads each parent row's primary key, queries the linked meta table for that
/// row's attribute rows, and returns them as a JSON object string.
///
/// Covers:
///  - An EAV parent row with attributes returns <c>_meta</c> as a JSON string of
///    that row's key/value pairs.
///  - An EAV parent row with no attribute rows returns <c>_meta == "{}"</c>.
///  - A non-EAV table is unaffected (no <c>_meta</c> field on the type).
/// </summary>
public sealed class EavMetaProviderTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_eav_meta_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS postmeta");
        await Exec("DROP TABLE IF EXISTS posts");
        await Exec("DROP TABLE IF EXISTS widgets");

        await Exec(
            """
            CREATE TABLE posts (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE postmeta (
                id INTEGER PRIMARY KEY,
                post_id INTEGER NOT NULL,
                meta_key TEXT NOT NULL,
                meta_value TEXT,
                FOREIGN KEY (post_id) REFERENCES posts(id)
            )
            """);
        // A plain non-EAV table to prove it stays unaffected.
        await Exec(
            """
            CREATE TABLE widgets (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL
            )
            """);

        await Exec("INSERT INTO posts(id, title) VALUES (1, 'first'), (2, 'second')");
        // Post 1 has two attributes; post 2 has none.
        await Exec(
            """
            INSERT INTO postmeta(id, post_id, meta_key, meta_value) VALUES
                (1, 1, 'color', 'red'),
                (2, 1, 'size', 'L')
            """);
        await Exec("INSERT INTO widgets(id, name) VALUES (1, 'gadget')");
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private static readonly string[] EavRules =
    {
        "*.postmeta { eav-parent: posts; eav-fk: post_id; eav-key: meta_key; eav-value: meta_value }",
    };

    private async Task<IDbModel> LoadModelAsync(params string[] rules)
    {
        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(rules));
        return await loader.LoadAsync();
    }

    [Fact]
    public async Task EavParentRow_WithAttributes_ReturnsMetaAsJsonObject()
    {
        var model = await LoadModelAsync(EavRules);

        var result = await ExecuteQueryAsync(model, "{ posts(filter: { id: { _eq: 1 } }) { data { id _meta } } }");

        result.Errors.Should().BeNullOrEmpty();
        var meta = ExtractMeta(result, postIndex: 0);

        // _meta is a JSON object string with the row's attributes.
        using var doc = JsonDocument.Parse(meta!);
        doc.RootElement.GetProperty("color").GetString().Should().Be("red");
        doc.RootElement.GetProperty("size").GetString().Should().Be("L");
    }

    [Fact]
    public async Task EavParentRow_WithNoAttributes_ReturnsEmptyJsonObject()
    {
        var model = await LoadModelAsync(EavRules);

        var result = await ExecuteQueryAsync(model, "{ posts(filter: { id: { _eq: 2 } }) { data { id _meta } } }");

        result.Errors.Should().BeNullOrEmpty();
        var meta = ExtractMeta(result, postIndex: 0);
        meta.Should().Be("{}");
    }

    [Fact]
    public async Task NonEavTable_DoesNotExposeMetaField()
    {
        var model = await LoadModelAsync(EavRules);
        var schema = DbSchema.FromModel(model);

        // The _meta field is synthesized only for EAV parents; widgets has none.
        var widgetsType = schema.AllTypes["widgets"] as GraphQL.Types.IComplexGraphType;
        widgetsType.Should().NotBeNull();
        widgetsType!.Fields.Any(f => f.Name == "_meta").Should().BeFalse();

        // And posts (the EAV parent) does expose it.
        var postsType = schema.AllTypes["posts"] as GraphQL.Types.IComplexGraphType;
        postsType.Should().NotBeNull();
        postsType!.Fields.Any(f => f.Name == "_meta").Should().BeTrue();
    }

    private static string? ExtractMeta(ExecutionResult result, int postIndex)
    {
        var json = new GraphQLSerializer().Serialize(result);
        using var doc = JsonDocument.Parse(json);
        var row = doc.RootElement
            .GetProperty("data")
            .GetProperty("posts")
            .GetProperty("data")
            .EnumerateArray()
            .ElementAt(postIndex);
        var meta = row.GetProperty("_meta");
        return meta.ValueKind == JsonValueKind.Null ? null : meta.GetString();
    }

    private async Task<ExecutionResult> ExecuteQueryAsync(IDbModel model, string query)
    {
        var schema = DbSchema.FromModel(model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IComputedColumnProvider, EavMetaProvider>();
        services.AddSingleton<IComputedColumnProviders>(sp =>
            new ComputedColumnProviders(sp.GetServices<IComputedColumnProvider>()));
        await using var provider = services.BuildServiceProvider();

        var executor = new DocumentExecuter();
        return await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.RequestServices = provider;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema),
            });
        });
    }
}
