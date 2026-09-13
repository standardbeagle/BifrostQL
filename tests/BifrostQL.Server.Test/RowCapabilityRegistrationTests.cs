using System.Text.Json;
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

namespace BifrostQL.Server.Test;

/// <summary>
/// S9 <c>_can</c> resolves through the provider set a real host registers
/// (<see cref="BifrostServiceRegistrar.RegisterComputedColumnServices"/>), not
/// only through a hand-built <c>ComputedColumnProviders</c>. Before the fix a
/// host selecting <c>_can</c> failed every row-scoped read with
/// "Computed column provider 'row-capability' is not registered."
/// </summary>
public sealed class RowCapabilityRegistrationTests : IAsyncLifetime
{
    private readonly string _connString = $"Data Source=bifrost_row_capability_reg_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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
            "main.members { policy-actions: read,update,delete }",
        })).LoadAsync();
        // '{ }' is reserved in the rule grammar, so the scope expression is applied after load.
        _model.GetTableFromDbName("members").Metadata[MetadataKeys.Policy.RowScope] = "user_id = {user_id}";
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task HostRegisteredProviders_ResolveCan()
    {
        var schema = DbSchema.FromModel(_model);
        var services = new ServiceCollection();
        BifrostServiceRegistrar.RegisterComputedColumnServices(services);
        await using var provider = services.BuildServiceProvider();

        var result = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = "{ members(sort: [id_asc]) { data { id _can { update delete } } } }";
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?> { ["user_id"] = 1, ["roles"] = new[] { "member" } };
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(_connString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, NullQueryTransformerService.Instance),
            });
        });

        result.Errors.Should().BeNullOrEmpty();
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        var rows = doc.RootElement.GetProperty("data").GetProperty("members").GetProperty("data").EnumerateArray().ToArray();
        rows[0].GetProperty("_can").GetProperty("update").GetBoolean().Should().BeTrue();
        rows[1].GetProperty("_can").GetProperty("update").GetBoolean().Should().BeFalse();
    }
}
