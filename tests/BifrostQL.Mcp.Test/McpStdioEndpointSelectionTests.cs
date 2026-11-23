using System.IO.Pipelines;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace BifrostQL.Mcp.Test;

/// <summary>
/// The stdio <see cref="BifrostMcpAdapter"/> chooses its GraphQL endpoint through
/// <see cref="McpAuthOptions.Endpoint"/>, checked as the host resolves the adapter, like every
/// other front door (McpHttpEndpointSelectionTests, MountEndpointSelectionTests). It used to take
/// no endpoint at all, so on a multi-endpoint host it started and every tool call failed.
/// </summary>
public sealed class McpStdioEndpointSelectionTests : IAsyncLifetime
{
    private readonly string _graphqlDb = Path.Combine(Path.GetTempPath(), $"mcp-stdio-a-{Guid.NewGuid():N}.db");
    private readonly string _otherDb = Path.Combine(Path.GetTempPath(), $"mcp-stdio-b-{Guid.NewGuid():N}.db");
    private IHost? _host;

    public Task InitializeAsync()
    {
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        CreateTable(_graphqlDb, "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NULL);");
        CreateTable(_otherDb,
            "CREATE TABLE gadgets (id INTEGER PRIMARY KEY, name TEXT NULL); " +
            "INSERT INTO gadgets (id, name) VALUES (1, 'sprocket');");
        return Task.CompletedTask;
    }

    private static void CreateTable(string path, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        SqliteConnection.ClearAllPools();
        if (File.Exists(_graphqlDb)) File.Delete(_graphqlDb);
        if (File.Exists(_otherDb)) File.Delete(_otherDb);
    }

    private async Task<IServiceProvider> StartHostAsync(bool withOther)
    {
        _host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services => services.AddBifrostEndpoints(o =>
            {
                o.AddEndpoint(e =>
                {
                    e.ConnectionString = $"Data Source={_graphqlDb}";
                    e.Provider = "sqlite";
                    e.Path = "/graphql";
                    e.DisableAuth = true;
                });
                if (withOther)
                    o.AddEndpoint(e =>
                    {
                        e.ConnectionString = $"Data Source={_otherDb}";
                        e.Provider = "sqlite";
                        e.Path = "/other";
                        e.DisableAuth = true;
                    });
            }));
            web.Configure(_ => { });
        }).StartAsync();
        return _host.Services;
    }

    private static BifrostMcpAdapter CreateAdapter(IServiceProvider services, string? endpoint)
        => new(
            services.GetRequiredService<IQueryIntentExecutor>(),
            services.GetRequiredService<IBifrostAuthContextFactory>(),
            services,
            authOptions: new McpAuthOptions { Mode = McpAuthMode.AnonymousDev, Endpoint = endpoint });

    [Fact]
    public async Task StdioAdapter_RefusesToStart_WhenSeveralEndpointsAreRegisteredAndNoneIsNamed()
    {
        var services = await StartHostAsync(withOther: true);

        var create = () => CreateAdapter(services, endpoint: null);

        create.Should().Throw<InvalidOperationException>().WithMessage("BifrostMcpAdapter:*McpAuthOptions.Endpoint*");
    }

    [Fact]
    public async Task StdioAdapter_RefusesToStart_WhenTheNamedEndpointIsNotRegistered()
    {
        var services = await StartHostAsync(withOther: false);

        var create = () => CreateAdapter(services, endpoint: "/grapql");

        create.Should().Throw<InvalidOperationException>().WithMessage("BifrostMcpAdapter:*'/grapql'*");
    }

    [Fact]
    public async Task StdioAdapter_Starts_WhenNoneIsNamed_OnASingleEndpointHost()
    {
        var services = await StartHostAsync(withOther: false);

        CreateAdapter(services, endpoint: null).Should().NotBeNull();
    }

    [Fact]
    public async Task StdioAdapter_ToolCall_RunsAgainstTheNamedEndpoint_OnAMultiEndpointHost()
    {
        var services = await StartHostAsync(withOther: true);
        var adapter = CreateAdapter(services, endpoint: "/other");
        var options = await adapter.CreateServerOptionsAsync(CancellationToken.None);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        using var stop = new CancellationTokenSource();
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            options, loggerFactory: null, serviceProvider: null);
        var run = server.RunAsync(stop.Token);
        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream())))
        {
            var result = await client.CallToolAsync("bifrost_query", new Dictionary<string, object?> { ["table"] = "gadgets" });

            result.IsError.Should().NotBeTrue(result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
            result.StructuredContent!.Value.GetProperty("rows").EnumerateArray()
                .Select(r => r.GetProperty("name").GetString()).Should().Equal("sprocket");
        }
        await stop.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }
    }
}
