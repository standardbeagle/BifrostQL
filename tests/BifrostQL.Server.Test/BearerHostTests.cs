using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// A bearer-token host with Bifrost auth enabled: the documented registration order works,
/// and the login gate's reach over the host's own routes is the host's explicit choice.
/// </summary>
public sealed class BearerHostTests : IAsyncLifetime
{
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("bearer-host-tests-signing-key-0123456789abcdef"));
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bearer-host-{Guid.NewGuid():N}.db");
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL); INSERT INTO notes(body) VALUES ('n1');";
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BearerRegisteredBeforeOrAfterAddBifrostQL_AuthenticatesTheGraphQlEndpoint(bool bearerFirst)
    {
        // The authentication guide registers AddJwtBearer BEFORE AddBifrostQL. Before the fix
        // that order answered a valid token with 401: Bifrost pinned the authenticate scheme to
        // its cookie, overriding the host's bearer default.
        using var host = await StartAsync(bearerFirst, BifrostAuthGateScope.AllRoutes);
        var response = await host.GetTestClient().SendAsync(GraphQl(Token()));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("n1");
    }

    [Fact]
    public async Task DefaultGateScope_GatesTheHostsOwnRoutes()
    {
        using var host = await StartAsync(bearerFirst: true, BifrostAuthGateScope.AllRoutes);
        var response = await host.GetTestClient().SendAsync(Health());
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the default keeps every route behind the login gate; narrowing it is the host's decision");
    }

    [Fact]
    public async Task BifrostRoutesGateScope_LeavesHostRoutesToTheHost_AndStillGatesGraphQl()
    {
        using var host = await StartAsync(bearerFirst: true, BifrostAuthGateScope.BifrostRoutes);
        var client = host.GetTestClient();

        (await client.SendAsync(Health())).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(GraphQl(token: null))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(GraphQl(Token()))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<IHost> StartAsync(bool bearerFirst, BifrostAuthGateScope gateScope)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "false",
            ["Bifrost:Path"] = "/graphql",
            ["Bifrost:Playground"] = "/graphiql",
            // OIDC settings Bifrost registers its interactive login with; a bearer-only host
            // never uses them.
            ["JwtSettings:Authority"] = "https://idp.example.invalid",
            ["JwtSettings:ClientId"] = "bearer-host-tests",
        }).Build();

        return await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                if (bearerFirst)
                    AddBearer(services);
                services.AddBifrostQL(o => o
                    .BindConfiguration(config.GetSection("Bifrost"))
                    .BindJwtSettings(config.GetSection("JwtSettings"))
                    .BindConnectionString($"Data Source={_dbPath}")
                    .BindProvider("sqlite"));
                if (!bearerFirst)
                    AddBearer(services);
            });
            web.Configure(app =>
            {
                app.UseBifrostQL(gateScope);
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapGet("/health", () => "ok"));
            });
        }).StartAsync();
    }

    private static void AddBearer(IServiceCollection services)
        => services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o => o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                IssuerSigningKey = SigningKey,
            });

    private static string Token() => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity(new[] { new Claim("sub", "dana") }),
        Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
    });

    private static HttpRequestMessage GraphQl(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(new { query = "{ notes { data { body } } }" }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage Health()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}
