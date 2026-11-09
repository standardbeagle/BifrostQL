using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BifrostQL.Core.Model;
using BifrostQL.Core.Operations;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// Pins the error replies the React client's auth decision reads to what a real server sends, so
/// the client's tests are fed real wire shapes rather than ones written to suit them: the vitest
/// suite pins the client to this file, this suite pins the server to it.
/// </summary>
public sealed class ReactGraphQlErrorWireContractTests : IAsyncLifetime
{
    private static readonly string ContractPath = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "graphql-error-wire-contract.json");

    private string _dbPath = null!;
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"react-error-contract-{Guid.NewGuid():N}.db");
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL);";
            await cmd.ExecuteNonQueryAsync();
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
            ["Bifrost:Metadata:0"] = ":root { operations: refuse }",
        }).Build();

        _host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddBifrostQL(o => o
                    .BindConfiguration(config.GetSection("Bifrost"))
                    .BindConnectionString($"Data Source={_dbPath}")
                    .BindProvider("sqlite")
                    .AddOperation<Refuse>());
            });
            web.Configure(app => app.UseBifrostQL());
        }).StartAsync();
        _client = _host.GetTestClient();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _host.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("codedRefusal")]
    [InlineData("authCode")]
    [InlineData("uncodedRefusal")]
    public async Task Refusal_RepliesWithTheShapeTheClientIsTestedAgainst(string entry)
    {
        var contract = JsonNode.Parse(await File.ReadAllTextAsync(ContractPath))![entry]!;

        using var content = new StringContent(contract["request"]!.ToJsonString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _client.PostAsync("/graphql", content);
        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        JsonNode.DeepEquals(reply, contract["reply"]).Should().BeTrue(reply!.ToJsonString());
    }

    private sealed class Refuse : IBifrostOperation
    {
        public string Name => "refuse";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => "(message: String!, code: String): Int";

        public ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
            => throw new BifrostExecutionError(context.GetArgument<string>("message")!)
            {
                ErrorCode = context.GetArgument<string>("code"),
            };
    }
}
