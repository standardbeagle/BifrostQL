using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace BifrostQL.Host.Test;

/// <summary>
/// H9: the host's `/mcp` front door under the SHIPPED (auth ON) posture.
///
/// <para>The host used to ship <c>BifrostQL:DisableAuth = true</c> in its base
/// appsettings.json and to call <c>AddBifrostMcpHttp()</c> with no auth options and
/// <c>MapBifrostMcp</c> with no authorization, so with <c>Mcp:Http:Enabled = true</c>
/// any caller that could reach the port read every table with no identity at all.
/// These tests pin the two halves of the fix: an unauthenticated request is rejected
/// by the endpoint (401, never a JSON-RPC session), and a valid bearer's identity is
/// what carries a tool call through the pipeline.</para>
/// </summary>
public sealed class McpHttpAuthTests : IClassFixture<McpHttpAuthTests.AuthenticatedHostFactory>
{
    private readonly AuthenticatedHostFactory _factory;

    public McpHttpAuthTests(AuthenticatedHostFactory factory) => _factory = factory;

    [Fact]
    public async Task Mcp_AuthEnabled_NoToken_Returns401()
    {
        using var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"probe","version":"1.0"}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an anonymous caller must be refused by the endpoint, not handed an MCP session");
    }

    [Fact]
    public async Task Mcp_AuthEnabled_ValidToken_IdentityReachesToolPipeline()
    {
        var httpClient = _factory.CreateClient();
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {AuthenticatedHostFactory.CreateToken()}",
                },
            },
            httpClient));

        var result = await mcp.CallToolAsync("bifrost_query",
            new Dictionary<string, object?> { ["table"] = "orders", ["detail"] = "full" });

        result.IsError.Should().NotBeTrue(
            result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);

        // `orders` is tenant-filtered, so this read can only succeed — and can only return
        // tenant-a's rows — if the token's principal was projected into the user context the
        // intent executor ran with. A caller with no identity is refused by the pipeline, so
        // the assertion cannot be satisfied by an anonymous session.
        var payload = result.StructuredContent!.Value;
        payload.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("name").GetString())
            .Should().BeEquivalentTo("a-first", "a-second");
    }

    public sealed class AuthenticatedHostFactory : WebApplicationFactory<Program>
    {
        private const string Issuer = "https://bifrost-tests.local/";
        private const string Audience = "bifrost-mcp-tests";
        private const string SigningKey = "bifrost-host-test-signing-key-0123456789";
        private const string TenantClaim = "bifrost:tenant";

        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"mcp-auth-{Guid.NewGuid():N}.db");

        internal static string CreateToken()
        {
            var handler = new JsonWebTokenHandler();
            return handler.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = Audience,
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim(JwtRegisteredClaimNames.Sub, "mcp-caller"),
                    new Claim(TenantClaim, "tenant-a"),
                }),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                    SecurityAlgorithms.HmacSha256),
            });
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            EnsureSqliteSeed(_dbPath);

            // Auth is deliberately NOT disabled here: this fixture runs the shipped posture.
            // JwtSettings must exist or BindStandardConfig refuses to start (the fail-closed
            // startup guard); the signing material itself is supplied below, because
            // JwtBearerOptions' TokenValidationParameters are not configuration-bindable.
            // UseSetting (not ConfigureAppConfiguration): Program.cs reads
            // builder.Configuration BEFORE the host is built, so a source added at build
            // time would be invisible to the auth/MCP switches under test.
            // The shipped posture, not the developer one: appsettings.Development.json is
            // where DisableAuth now lives, so a Development host would serve /mcp anonymously
            // by design and this fixture would prove nothing.
            builder.UseEnvironment("Production");
            builder.UseSetting("BifrostQL:Provider", "sqlite");
            builder.UseSetting("BifrostQL:Path", "/graphql");
            builder.UseSetting("BifrostQL:Mcp:Http:Enabled", "true");
            builder.UseSetting("ConnectionStrings:bifrost", $"Data Source={_dbPath}");
            builder.UseSetting("JwtSettings:Audience", Audience);
            // The interactive OIDC handler registered alongside the JWT one needs an authority
            // and a client id to construct; it is never challenged here (MCP callers hit the
            // bearer scheme), so the values only have to exist.
            builder.UseSetting("JwtSettings:Authority", Issuer);
            builder.UseSetting("JwtSettings:ClientId", "bifrost-host-tests");
            // Replaces the first shipped metadata rule: the fixture needs a tenant-filtered
            // table so a read can only succeed under an identity that carries a tenant.
            builder.UseSetting("BifrostQL:Metadata:0", "*.orders { tenant-filter: tenant_id }");

            builder.ConfigureTestServices(services =>
                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Authority = null;
                    options.RequireHttpsMetadata = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = Issuer,
                        ValidAudience = Audience,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                        ValidateIssuerSigningKey = true,
                    };
                }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private static void EnsureSqliteSeed(string dbPath)
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS orders (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tenant_id TEXT NOT NULL,
                    name TEXT NOT NULL);
                INSERT INTO orders(tenant_id, name)
                VALUES ('tenant-a','a-first'),('tenant-a','a-second'),('tenant-b','b-only');
                """;
            cmd.ExecuteNonQuery();
        }
    }
}
