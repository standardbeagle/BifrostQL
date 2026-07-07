using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
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
/// Verifies the fix for the HIGH finding: <see cref="EavMetaProvider"/> read the
/// meta table with a raw <c>SELECT key,value FROM meta WHERE fk=@pk</c>, bypassing
/// every filter transformer. If the meta table itself carries a soft-delete (or
/// tenant) column, a deleted attribute row still surfaced in <c>_meta</c>.
///
/// The fix ANDs the meta table's own combined filter (resolved from
/// <c>IFilterTransformers</c> via <c>ComputedColumnContext.Services</c>) onto the
/// meta-table WHERE clause. This is enforcement only — it does not touch the
/// separately-tracked EAV config/schema-qualification (missing TableSchema) issue.
/// </summary>
public sealed class EavMetaProviderSecurityFilterTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_eav_meta_security_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS postmeta");
        await Exec("DROP TABLE IF EXISTS posts");

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
                deleted_at TEXT,
                FOREIGN KEY (post_id) REFERENCES posts(id)
            )
            """);

        await Exec("INSERT INTO posts(id, title) VALUES (1, 'first')");
        // One live attribute, one soft-deleted attribute on the same post.
        await Exec(
            """
            INSERT INTO postmeta(id, post_id, meta_key, meta_value, deleted_at) VALUES
                (1, 1, 'color', 'red', NULL),
                (2, 1, 'size', 'L', '2020-01-01')
            """);
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private static readonly string[] EavRules =
    {
        "*.postmeta { eav-parent: posts; eav-fk: post_id; eav-key: meta_key; eav-value: meta_value; soft-delete: deleted_at }",
    };

    private async Task<IDbModel> LoadModelAsync(params string[] rules)
    {
        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(rules));
        return await loader.LoadAsync();
    }

    [Fact]
    public async Task EavMeta_ExcludesSoftDeletedAttributeRows()
    {
        var model = await LoadModelAsync(EavRules);

        var result = await ExecuteQueryAsync(model, "{ posts(filter: { id: { _eq: 1 } }) { data { id _meta } } }");

        result.Errors.Should().BeNullOrEmpty();
        var meta = ExtractMeta(result, postIndex: 0);

        meta.ValueKind.Should().Be(JsonValueKind.Object);
        meta.GetProperty("color").GetString().Should().Be("red");
        // The soft-deleted "size" attribute must not surface through _meta —
        // before the fix it did, because the meta-table read bypassed the
        // soft-delete filter transformer entirely.
        meta.TryGetProperty("size", out _).Should().BeFalse();
    }

    private static JsonElement ExtractMeta(ExecutionResult result, int postIndex)
    {
        var json = new GraphQLSerializer().Serialize(result);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("data")
            .GetProperty("posts")
            .GetProperty("data")
            .EnumerateArray()
            .ElementAt(postIndex)
            .GetProperty("_meta")
            .Clone();
    }

    private async Task<ExecutionResult> ExecuteQueryAsync(IDbModel model, string query)
    {
        var schema = DbSchema.FromModel(model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IComputedColumnProvider, EavMetaProvider>();
        services.AddSingleton<IComputedColumnProviders>(sp =>
            new ComputedColumnProviders(sp.GetServices<IComputedColumnProvider>()));
        services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new SoftDeleteFilterTransformer() },
        });
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
