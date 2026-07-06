using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
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
/// The update mutation field is typed <c>Int</c>. For a single-key table the
/// resolver returns the key VALUE (identifies the affected row). For a
/// composite-key table a single Int cannot carry the whole key, so returning
/// <c>keyData.Values.First()</c> would silently surface only the FIRST key
/// component — misleading. The composite case instead returns the affected row
/// count. These end-to-end mutations pin both behaviours.
/// </summary>
public sealed class MutateUpdateReturnTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_update_return_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS widgets");
        await Exec("DROP TABLE IF EXISTS enrollments");
        await Exec(
            """
            CREATE TABLE widgets (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL
            )
            """);
        await Exec("INSERT INTO widgets(id, name) VALUES (7, 'original')");
        await Exec(
            """
            CREATE TABLE enrollments (
                sid INTEGER NOT NULL,
                cid INTEGER NOT NULL,
                grade TEXT,
                PRIMARY KEY (sid, cid)
            )
            """);
        await Exec("INSERT INTO enrollments(sid, cid, grade) VALUES (1, 2, 'B')");

        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(System.Array.Empty<string>()));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task SingleKeyUpdate_ReturnsKeyValue()
    {
        var result = await ExecuteMutationAsync(
            "mutation { widgets(update: { id: 7, name: \"changed\" }) }");

        result.Errors.Should().BeNullOrEmpty();
        // The single-key table returns the key VALUE (7), not the affected-row
        // count (1) — the two differ here, so this pins the behaviour.
        ReadInt(result, "widgets").Should().Be(7);
        (await ScalarAsync("SELECT name FROM widgets WHERE id = 7")).Should().Be("changed");
    }

    [Fact]
    public async Task CompositeKeyUpdate_ReturnsAffectedRowCount()
    {
        var result = await ExecuteMutationAsync(
            "mutation { enrollments(update: { sid: 1, cid: 2, grade: \"A\" }) }");

        result.Errors.Should().BeNullOrEmpty();
        // A composite key cannot be represented by a single Int, so the resolver
        // returns the affected row count (1) rather than the first key component.
        ReadInt(result, "enrollments").Should().Be(1);
        (await ScalarAsync("SELECT grade FROM enrollments WHERE sid = 1 AND cid = 2")).Should().Be("A");
    }

    private static int ReadInt(ExecutionResult result, string field)
    {
        var json = new GraphQLSerializer().Serialize(result);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").GetProperty(field).GetInt32();
    }

    private async Task<ExecutionResult> ExecuteMutationAsync(string mutation)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = System.Array.Empty<IMutationTransformer>(),
        });
        await using var provider = services.BuildServiceProvider();

        var executor = new DocumentExecuter();
        return await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            });
        });
    }
}
