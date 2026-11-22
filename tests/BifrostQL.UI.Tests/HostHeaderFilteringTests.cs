using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// DNS rebinding: a page on evil.example re-resolves its own name to 127.0.0.1 and then
/// calls the loopback host as same-origin, so the same-origin CORS policy never fires.
/// The Host header still names the attacker's domain, so the loopback bind must refuse
/// any Host that is not a loopback name. The desktop window loads
/// http://localhost:&lt;port&gt;, so that Host must still be served.
/// </summary>
[Collection(HeadlessUiServerCollection.Name)]
public sealed class HostHeaderFilteringTests
{
    private readonly HeadlessUiServer _server;

    public HostHeaderFilteringTests(HeadlessUiServer server) => _server = server;

    [Fact]
    public async Task ForeignHostHeader_IsRejected_BadRequest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = $"evil.example:{_server.Port}";

        using var response = await _server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public async Task LoopbackHostHeader_IsServed(string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = $"{host}:{_server.Port}";

        using var response = await _server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
