using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Pins that the desktop host actually serves <c>/_saved-objects</c>.
///
/// Observed live: opening the visual query designer painted a red
/// "The string did not match the expected pattern." in the Saved Queries rail
/// with no user action. That is WebKit's SyntaxError text for
/// <c>Response.json()</c> on a non-JSON body — the host never registered the
/// saved-objects middleware, so <c>GET /_saved-objects/query</c> fell through to
/// <c>MapFallbackToFile("index.html")</c> and answered 200 text/html. The client
/// saw <c>resp.ok</c>, parsed HTML as JSON, and showed the browser's raw
/// exception string.
/// </summary>
[Trait("Category", "API")]
[Collection(HeadlessUiServerCollection.Name)]
public class SavedObjectsEndpointTests
{
    private readonly HeadlessUiServer _server;
    private HttpClient Client => _server.Client;

    public SavedObjectsEndpointTests(HeadlessUiServer server) => _server = server;

    [Fact]
    public async Task Listing_saved_queries_returns_json_not_the_spa_fallback()
    {
        using var response = await Client.GetAsync("/_saved-objects/query");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/json", "the SPA fallback would answer text/html and break JSON parsing in the rail");

        var body = await response.Content.ReadAsStringAsync();
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Saved_queries_round_trip_through_the_endpoint()
    {
        var id = $"query:test-{System.Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new
        {
            id,
            type = "query",
            name = "Round trip",
            definition = new { tables = System.Array.Empty<string>() },
            version = 0,
        });

        using var put = await Client.PutAsync(
            $"/_saved-objects/query/{System.Uri.EscapeDataString(id)}",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        put.IsSuccessStatusCode.Should().BeTrue(await put.Content.ReadAsStringAsync());

        try
        {
            using var list = await Client.GetAsync("/_saved-objects/query");
            var body = await list.Content.ReadAsStringAsync();
            body.Should().Contain(id);
        }
        finally
        {
            using var _ = await Client.DeleteAsync($"/_saved-objects/query/{System.Uri.EscapeDataString(id)}");
        }
    }
}
