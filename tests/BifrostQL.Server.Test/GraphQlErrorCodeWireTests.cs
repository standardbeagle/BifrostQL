using System.Net.Http.Json;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The documented error codes reach a GraphQL client as <c>errors[].extensions.code</c>. Before
/// the fix every Bifrost error left the HTTP endpoint as <c>BIFROST_EXECUTION_ERROR</c>, so a
/// policy denial, a pending approval and a fault were told apart only by message text.
/// </summary>
public sealed class GraphQlErrorCodeWireTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"error-code-wire-{Guid.NewGuid():N}.db");
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL);" +
                "CREATE TABLE drafts (id INTEGER PRIMARY KEY, body TEXT NOT NULL);" +
                "CREATE TABLE pending_changes (id INTEGER PRIMARY KEY, \"table\" TEXT NOT NULL, op TEXT NOT NULL, " +
                "intended_payload TEXT NOT NULL, requester TEXT NULL, tenant TEXT NULL, requester_context TEXT NULL, " +
                "\"state\" TEXT NOT NULL, approver TEXT NULL, decided_at TEXT NULL, reason TEXT NULL);" +
                "CREATE TABLE guarded (id INTEGER PRIMARY KEY, body TEXT NOT NULL);" +
                "CREATE TABLE hooked (id INTEGER PRIMARY KEY, body TEXT NOT NULL);" +
                "CREATE TABLE tags (id INTEGER PRIMARY KEY, name TEXT NOT NULL CONSTRAINT tags_name_unique UNIQUE);" +
                "INSERT INTO tags (id, name) VALUES (1, 'taken');";
            await cmd.ExecuteNonQueryAsync();
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
            ["Bifrost:Metadata:0"] = "main.notes { policy-actions: read }",
            ["Bifrost:Metadata:1"] = "main.drafts { approval: enabled; approver-role: manager }",
        }).Build();

        _host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton<IInTransactionMutationHook, RefusingHook>();
                services.AddBifrostQL(o => o
                    .BindConfiguration(config.GetSection("Bifrost"))
                    .BindConnectionString($"Data Source={_dbPath}")
                    .BindProvider("sqlite")
                    .AddMutationTransformer<RefusingHostTransformer>());
            });
            web.Configure(app => app.UseBifrostQL());
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

    [Theory]
    [InlineData("mutation { guarded(insert: { body: \"x\" }) }", RefusingHostTransformer.Code, RefusingHostTransformer.Message)]
    [InlineData("mutation { guarded_batch(actions: [{ insert: { body: \"x\" } }]) }", RefusingHostTransformer.Code, RefusingHostTransformer.Message)]
    [InlineData("mutation { hooked(insert: { body: \"x\" }) }", RefusingHook.Code, RefusingHook.Message)]
    [InlineData("mutation { tags(insert: { name: \"taken\" }) }", "CONFLICT", "A conflicting record already exists.")]
    public async Task HostRefusalHookThrowAndUniqueViolation_CarryTheirCode_WithTheSanitizedMessage(
        string mutation, string expectedCode, string expectedMessage)
    {
        var response = await _client.PostAsync("/graphql", JsonContent.Create(new { query = mutation }));
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("errors").EnumerateArray().Single();
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be(expectedCode, body);
        error.GetProperty("message").GetString().Should().Be(expectedMessage, body);
    }

    /// <summary>A host transformer that refuses every write to <c>guarded</c> with its own code.</summary>
    private sealed class RefusingHostTransformer : IMutationTransformer
    {
        public const string Code = "PROTECTED_ITEM";
        public const string Message = "The item is protected.";

        public int Priority => 200;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => table.DbName == "guarded";

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
            => ValueTask.FromResult(new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data,
                Errors = new[] { Message },
                ErrorCode = Code,
            });
    }

    /// <summary>An in-transaction hook that throws a coded refusal for every write to <c>hooked</c>.</summary>
    private sealed class RefusingHook : IInTransactionMutationHook
    {
        public const string Code = "BOOKING_REFUSED";
        public const string Message = "The booking was refused.";

        public bool AppliesTo(IDbTable table) => table.DbName == "hooked";

        public ValueTask AfterWriteInTransactionAsync(MutationObserverContext context)
            => throw new BifrostExecutionError(Message) { ErrorCode = Code };
    }

    [Theory]
    [InlineData("mutation { notes(insert: { body: \"x\" }) }", "ACCESS_DENIED")]
    [InlineData("mutation { drafts(insert: { body: \"x\" }) }", "PENDING_APPROVAL")]
    public async Task ErrorCode_IsTheWireExtensionsCode(string mutation, string expectedCode)
    {
        var response = await _client.PostAsync("/graphql", JsonContent.Create(new { query = mutation }));
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("errors").EnumerateArray().Single();
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be(expectedCode, body);
    }
}
