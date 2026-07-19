using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Integration.Test.Infrastructure;
using BifrostQL.Mcp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Integration.Test.SchemaLoading;

/// <summary>
/// Proves the shipped sample declarative MCP tool document
/// (<c>Infrastructure/sample-mcp-tools.json</c>) loads and validates against the
/// sample schema at host startup, and that a document referencing a table the
/// model does not have fails the host START (not later, at first tool call) — the
/// bad-reference-is-a-startup-failure contract from mcp-tools slice 2, exercised
/// end to end through <see cref="DeclarativeToolServiceCollectionExtensions.AddBifrostMcpTools(IServiceCollection, string)"/>
/// and the registered <see cref="DeclarativeToolDocumentValidator"/> hosted service.
/// </summary>
public sealed class DeclarativeMcpToolDocumentLoadingTests
{
    /// <summary>
    /// A minimal executor that hands back the sample schema model. The validator
    /// only reads the model to resolve declared tables/columns/relations; it never
    /// executes a query, so no live database is required to prove startup validation.
    /// </summary>
    private sealed class StubExecutor : IQueryIntentExecutor
    {
        private readonly IDbModel _model = TestSchema.BuildDbModel();
        public Task<IDbModel> GetModelAsync(string? endpoint = null) => Task.FromResult(_model);
        public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The tool-document validator must not execute queries.");
    }

    private static string SampleDocumentPath() =>
        Path.Combine(AppContext.BaseDirectory, "Infrastructure", "sample-mcp-tools.json");

    private static IHost BuildHost(Action<IServiceCollection> configure)
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IQueryIntentExecutor, StubExecutor>();
                configure(services);
            })
            .Build();
    }

    [Fact]
    public async Task SampleToolDocument_LoadsAndValidates_AtStartup()
    {
        using var host = BuildHost(services => services.AddBifrostMcpTools(SampleDocumentPath()));

        // The validator runs as an IHostedService: a clean start proves every declared
        // tool resolves its root table, projected columns, relations, and aggregates
        // against the live model.
        var start = async () => await host.StartAsync();
        await start.Should().NotThrowAsync();

        // The loaded document is the shipped 4-tool consolidation sample.
        var document = host.Services.GetRequiredService<DeclarativeToolDocument>();
        document.Version.Should().Be(1);
        document.Tools.Select(tool => tool.Name).Should().BeEquivalentTo(new[]
        {
            "get_customer_context", "get_order_detail", "get_product_overview", "get_category_catalog",
        });

        await host.StopAsync();
    }

    [Fact]
    public async Task ToolDocument_WithUnknownTableReference_FailsHostStartup()
    {
        // Shape-valid but references a table the model does not have: this is exactly
        // the "bad reference" a validated startup must reject before serving any tool.
        const string badDocument = """
            {
              "version": 1,
              "tools": [
                {
                  "name": "get_ghost",
                  "description": "References a table that does not exist in the model.",
                  "params": { "ghostId": { "type": "id", "description": "id" } },
                  "root": { "table": ".Ghosts", "byId": "ghostId", "fields": ["Id"] }
                }
              ]
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(badDocument));
        using var host = BuildHost(services => services.AddBifrostMcpTools(stream, "bad-reference-document"));

        var start = async () => await host.StartAsync();
        (await start.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("validation failed").And.Contain(".Ghosts");
    }
}
