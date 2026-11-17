using System.Net;
using BifrostQL.Tool.Commands;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace BifrostQL.Tool.Test;

/// <summary>
/// `bifrost serve` has no authentication, so a browser page on another origin (or a DNS
/// rebinding name pointed at 127.0.0.1) must not be able to drive it.
/// </summary>
public sealed class ServeCommandExposureTests
{
    private const string SqliteMemory = "Data Source=:memory:";

    private static async Task<WebApplication> StartAsync(string host)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = ServeCommand.BuildApp(builder, host, SqliteMemory);
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task CrossOriginPreflight_IsNotGranted()
    {
        await using var app = await StartAsync("127.0.0.1");
        using var client = app.GetTestClient();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/graphql");
        preflight.Headers.Add("Origin", "https://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        using var response = await client.SendAsync(preflight);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse(
            "a page on another origin must not be allowed to send JSON to an unauthenticated server");
    }

    [Fact]
    public async Task LoopbackBind_RefusesForeignHostHeader()
    {
        await using var app = await StartAsync("127.0.0.1");
        using var client = app.GetTestClient();

        using var rebinding = new HttpRequestMessage(HttpMethod.Get, "/no-such-route");
        rebinding.Headers.Host = "attacker.example:5000";
        using var refused = await client.SendAsync(rebinding);

        using var loopback = new HttpRequestMessage(HttpMethod.Get, "/no-such-route");
        loopback.Headers.Host = "127.0.0.1:5000";
        using var served = await client.SendAsync(loopback);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a DNS-rebinding Host header is refused");
        served.StatusCode.Should().Be(HttpStatusCode.NotFound, "control: a loopback Host header reaches the pipeline");
    }
}
