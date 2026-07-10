using System.Net;
using System.Text;
using BifrostQL.Core.SavedObjects;
using BifrostQL.Server;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// End-to-end coverage for the <c>/_saved-objects</c> CRUD endpoint over a real
/// TestServer pipeline: create/fetch/list/delete round-trip, the stale-version 409,
/// the validation 400, and the not-found 404. Backed by a file store in a temp dir.
/// </summary>
public sealed class SavedObjectsEndpointTests : IDisposable
{
    private readonly string _dir;

    public SavedObjectsEndpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bifrost-so-endpoint-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private async Task<HttpClient> BuildClientAsync()
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
                services.AddBifrostSavedObjects(new FileSavedObjectStore(_dir)));
            web.Configure(app => app.UseBifrostSavedObjects());
        });
        var host = await builder.StartAsync();
        return host.GetTestClient();
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Crud_RoundTrips_ThroughEndpoint()
    {
        var client = await BuildClientAsync();

        // Create (version 0 -> 1).
        var create = await client.PutAsync("/_saved-objects/query/q1",
            Body("""{"id":"q1","type":"query","name":"Sales","definition":{"groupBy":["region"]},"version":0}"""));
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        (await create.Content.ReadAsStringAsync()).Should().Contain("\"version\":1");

        // Fetch one.
        var get = await client.GetAsync("/_saved-objects/query/q1");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await get.Content.ReadAsStringAsync()).Should().Contain("Sales");

        // List the type.
        var list = await client.GetAsync("/_saved-objects/query");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await list.Content.ReadAsStringAsync()).Should().Contain("q1");

        // Delete -> 204, then gone -> 404.
        (await client.DeleteAsync("/_saved-objects/query/q1")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync("/_saved-objects/query/q1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StaleVersionPut_Returns409()
    {
        var client = await BuildClientAsync();
        await client.PutAsync("/_saved-objects/form/f1", Body("""{"id":"f1","type":"form","name":"v1","definition":{},"version":0}"""));
        await client.PutAsync("/_saved-objects/form/f1", Body("""{"id":"f1","type":"form","name":"v2","definition":{},"version":1}"""));

        var stale = await client.PutAsync("/_saved-objects/form/f1",
            Body("""{"id":"f1","type":"form","name":"stale","definition":{},"version":1}"""));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PutWithBodyPathMismatch_Returns400()
    {
        var client = await BuildClientAsync();
        // Body id ("other") disagrees with the URL id ("q1").
        var resp = await client.PutAsync("/_saved-objects/query/q1",
            Body("""{"id":"other","type":"query","name":"x","definition":{},"version":0}"""));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetUnknownType_Returns400()
    {
        var client = await BuildClientAsync();
        (await client.GetAsync("/_saved-objects/bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
