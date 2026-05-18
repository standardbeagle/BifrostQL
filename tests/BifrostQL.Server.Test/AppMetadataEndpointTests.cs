using System.Net;
using System.Text.Json;
using BifrostQL.Core.AppMetadata;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// End-to-end coverage for the AppMetadata overlay middleware:
///   - GET /_app-metadata returns the loaded overlay as the stable camelCase JSON.
///   - Endpoint never 404s; with no overlay registered it returns `{"entities":{}}`.
///   - Custom path option is honored.
///   - The overlay is loaded independently of the GraphQL schema-metadata
///     pipeline (asserted via DI: AppMetadataModel is registered, no
///     schema-metadata service is required).
/// </summary>
public sealed class AppMetadataEndpointTests
{
    [Fact]
    public async Task GetAppMetadata_WithLoadedOverlay_ReturnsLoadedEntities()
    {
        var overlay = SampleOverlay();
        using var host = await BuildHost(overlay);
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/_app-metadata");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var body = await response.Content.ReadAsStringAsync();
        var roundTrip = AppMetadataJson.Deserialize(body);

        roundTrip.Entities.Should().ContainKey("main.members");
        roundTrip.Entities["main.members"].Label.Should().Be("Members");
        roundTrip.Entities["main.members"].Fields.Should().ContainKey("first_name");
        roundTrip.Entities["main.members"].Fields["first_name"].Widget.Should().Be("text");
    }

    [Fact]
    public async Task GetAppMetadata_WithoutRegisteredOverlay_ReturnsEmptyEntities()
    {
        // No AddBifrostAppMetadata call — the middleware should still serve
        // an empty overlay (never 404) so clients see a stable contract.
        using var host = await BuildHostWithoutOverlay();
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/_app-metadata");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var roundTrip = AppMetadataJson.Deserialize(body);
        roundTrip.Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAppMetadata_WithCustomPath_RespondsOnCustomPathOnly()
    {
        var overlay = SampleOverlay();
        using var host = await BuildHost(overlay, configurePath: "/api/meta");
        using var client = host.GetTestClient();

        var defaultPath = await client.GetAsync("/_app-metadata");
        defaultPath.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the default path is no longer the configured endpoint");

        var customPath = await client.GetAsync("/api/meta");
        customPath.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AppMetadataModel_IsSeparateFromSchemaMetadataPipeline()
    {
        // The overlay is loaded into its own singleton; the schema-metadata
        // pipeline (DbModel, MetadataLoader) is never required to be in DI
        // for the endpoint to serve. Verify both: AppMetadataModel is
        // registered, and no IDbModel/IMetadataLoader service is needed.
        var overlay = SampleOverlay();
        using var host = await BuildHost(overlay);

        host.Services.GetService<AppMetadataModel>().Should().NotBeNull();
        host.Services.GetService<BifrostQL.Core.Model.IDbModel>().Should().BeNull(
            "the overlay endpoint does not depend on the schema-metadata pipeline");
    }

    // ---- helpers ----

    private static AppMetadataModel SampleOverlay()
    {
        return new AppMetadataModel
        {
            Entities = new Dictionary<string, EntityMetadata>
            {
                ["main.members"] = new EntityMetadata
                {
                    Label = "Members",
                    Icon = "person",
                    NavPlacement = "main",
                    Fields = new Dictionary<string, FieldMetadata>
                    {
                        ["first_name"] = new FieldMetadata { Widget = "text", Group = "Identity" },
                        ["status"] = new FieldMetadata { Widget = "select", Group = "Membership" },
                    },
                },
            },
        };
    }

    private static async Task<IHost> BuildHost(AppMetadataModel overlay, string? configurePath = null)
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddBifrostAppMetadata(new IAppMetadataSource[]
                    {
                        new InMemoryAppMetadataSource(overlay),
                    });
                });
                web.Configure(app =>
                {
                    if (configurePath is null)
                        app.UseBifrostAppMetadata();
                    else
                        app.UseBifrostAppMetadata(o => o.Path = configurePath);
                });
            });
        var host = await builder.StartAsync();
        return host;
    }

    private static async Task<IHost> BuildHostWithoutOverlay()
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.Configure(app => app.UseBifrostAppMetadata());
            });
        return await builder.StartAsync();
    }

    private sealed class InMemoryAppMetadataSource : IAppMetadataSource
    {
        private readonly AppMetadataModel _model;
        public InMemoryAppMetadataSource(AppMetadataModel model) => _model = model;
        public int Priority => 0;
        public Task<IDictionary<string, EntityMetadata>> LoadEntityMetadataAsync()
            => Task.FromResult<IDictionary<string, EntityMetadata>>(
                _model.Entities.ToDictionary(kv => kv.Key, kv => kv.Value));
    }
}
