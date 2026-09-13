using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
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
/// S9 <c>_can</c> against a real SQLite database: selecting <c>_can</c> WITHOUT
/// its scope column projects that column for the provider but never surfaces it
/// in the response, and the per-row answer follows the caller — own row, a
/// colleague's row, and an exempt grant holder.
/// </summary>
public sealed class RowCapabilityExecutionTests : IAsyncLifetime
{
    private readonly string _connString = $"Data Source=bifrost_row_capability_exec_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(_connString);
        await _keepAlive.OpenAsync();
        await using (var ddl = new SqliteCommand(
            "CREATE TABLE members (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL, note TEXT NULL);" +
            "INSERT INTO members(id, user_id, note) VALUES (1, 1, 'mine'), (2, 2, 'theirs');", _keepAlive))
            await ddl.ExecuteNonQueryAsync();

        _model = await new DbModelLoader(new SqliteDbConnFactory(_connString), new MetadataLoader(new[]
        {
            "main.members { policy-actions: read,update,delete; policy-row-scope-exempt: time.edit_others }",
        })).LoadAsync();
        // '{ }' is reserved in the rule grammar, so the scope expression is applied after load.
        _model.GetTableFromDbName("members").Metadata[MetadataKeys.Policy.RowScope] = "user_id = {user_id}";
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task Member_SeesOwnRowCapableAndColleagueRowNot_WithoutTheScopeColumnInTheOutput()
    {
        var rows = await QueryRows(new Dictionary<string, object?> { ["user_id"] = 1, ["roles"] = new[] { "member" } });

        Can(rows[0]).Should().Be((true, true), "row 1 is the caller's own row");
        Can(rows[1]).Should().Be((false, false), "row 2 belongs to a colleague");
        foreach (var row in rows)
        {
            row.TryGetProperty("userId", out _).Should().BeFalse("the scope column was projected for the provider, not selected");
            row.TryGetProperty("user_id", out _).Should().BeFalse();
            row.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "_can");
        }
    }

    [Fact]
    public async Task ExemptGrantHolder_IsCapableOnEveryRow()
    {
        var rows = await QueryRows(new Dictionary<string, object?>
        {
            ["user_id"] = 1,
            [MetadataKeys.Auth.DefaultPermissionsContextKey] = new[] { "time.edit_others" },
        });

        Can(rows[0]).Should().Be((true, true));
        Can(rows[1]).Should().Be((true, true));
    }

    private static (bool Update, bool Delete) Can(JsonElement row)
    {
        var can = row.GetProperty("_can");
        return (can.GetProperty("update").GetBoolean(), can.GetProperty("delete").GetBoolean());
    }

    private async Task<JsonElement[]> QueryRows(IDictionary<string, object?> userContext)
    {
        var schema = DbSchema.FromModel(_model);
        var services = new ServiceCollection();
        services.AddSingleton<IComputedColumnProvider>(_ => new RowCapabilityProvider());
        services.AddSingleton<IComputedColumnProviders>(sp => new ComputedColumnProviders(sp.GetServices<IComputedColumnProvider>()));
        await using var provider = services.BuildServiceProvider();

        var result = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = "{ members(sort: [id_asc]) { data { id _can { update delete } } } }";
            options.RequestServices = provider;
            options.UserContext = userContext;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(_connString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, NullQueryTransformerService.Instance),
            });
        });

        result.Errors.Should().BeNullOrEmpty();
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        return doc.RootElement.GetProperty("data").GetProperty("members").GetProperty("data")
            .EnumerateArray().Select(e => e.Clone()).ToArray();
    }
}
