using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Operations;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The host path of server operations: <c>AddOperation&lt;T&gt;()</c> on <c>AddBifrostQL</c> plus the
/// <c>operations</c> metadata key reach the cached per-profile schema, and a request over HTTP
/// combines an operation with a generated field, with the operation's write stamped as the
/// authenticated caller.
/// </summary>
public sealed class ServerOperationHostTests : IAsyncLifetime
{
    private const string UserHeader = "X-Test-User";
    private string _dbPath = null!;
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"server-operation-{Guid.NewGuid():N}.db");
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL, created_by TEXT NULL);";
            await cmd.ExecuteNonQueryAsync();
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
            ["Bifrost:Metadata:0"] = ":root { user-audit-key: user_id; operations: addNote[writer] }",
            ["Bifrost:Metadata:1"] = "main.notes.created_by { populate: created-by }",
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
                    .AddOperation<AddNote>());
            });
            web.Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    var user = context.Request.Headers[UserHeader].ToString();
                    if (user.Length > 0)
                        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, user),
                            new Claim(ClaimTypes.Role, "writer"),
                        }, authenticationType: "test"));
                    await next();
                });
                app.UseBifrostQL();
            });
        }).StartAsync();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _host.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RegisteredOperation_RunsBesideAGeneratedField_AsTheAuthenticatedCaller()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(new
            {
                query = "mutation { addNote(body: \"hello\") notes(insert: { body: \"generated\" }) }",
            }),
        };
        request.Headers.Add(UserHeader, "carol");

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("errors", out _).Should().BeFalse(body);
        doc.RootElement.GetProperty("data").GetProperty("addNote").GetInt64().Should().BeGreaterThan(0);

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT body || ':' || created_by FROM notes ORDER BY id";
        var rows = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                rows.Add(reader.GetString(0));
        rows.Should().Equal("hello:carol", "generated:carol");
    }

    private sealed class AddNote : IBifrostOperation
    {
        public string Name => "addNote";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => "(body: String!): Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
            => Convert.ToInt64(await context.InsertAsync("notes",
                new Dictionary<string, object?> { ["body"] = context.GetArgument<string>("body") }));
    }
}
