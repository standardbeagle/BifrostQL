using System.Net;
using System.Net.Http;
using System.Text;
using BifrostQL.UI.Web;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// M24b: the bridge POST parsed the body as JSON without checking Content-Type,
/// so a cross-origin no-cors text/plain form POST (which browsers send without
/// a preflight) could execute exec-sql. The bridge must demand both
/// Content-Type: application/json and the custom header X-Bifrost-Bridge — a
/// combination no cross-origin no-cors request can produce, because the custom
/// header forces a CORS preflight the loopback host never grants.
/// </summary>
public sealed class HttpBridgeCsrfGuardTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        HttpBridgeEndpoint.Map(_app, new ConnectionState());
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Post_TextPlain_IsRejected_UnsupportedMediaType()
    {
        // The no-cors shape: a form post carries text/plain and no custom header.
        var request = new HttpRequestMessage(HttpMethod.Post, "/_bridge/exec-sql")
        {
            Content = new StringContent("{\"sql\":\"select 1\"}", Encoding.UTF8, "text/plain"),
        };
        request.Headers.Add("X-Bifrost-Bridge", "1");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Post_JsonWithoutBridgeHeader_IsRejected_Forbidden()
    {
        // Content-Type alone is not enough: application/json is preflight-safe
        // only with simple headers; the custom header is the preflight-forcing part.
        var request = new HttpRequestMessage(HttpMethod.Post, "/_bridge/exec-sql")
        {
            Content = new StringContent("{\"sql\":\"select 1\"}", Encoding.UTF8, "application/json"),
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
