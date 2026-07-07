using System.Net;
using System.Net.Http.Headers;
using BifrostQL.Core.Model;
using BifrostQL.Server;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Finding 6: authentication enforcement is endpoint-scoped. An endpoint that requires
    /// auth (DisableAuth=false) must challenge/deny regardless of a sibling endpoint that
    /// disables auth, and regardless of pipeline order — not merely gated by a single
    /// aggregate toggle.
    /// </summary>
    public sealed class EndpointScopedAuthTests
    {
        private static async Task<IHost> BuildHostAsync(string dbPath)
        {
            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE IF NOT EXISTS widgets (id INTEGER PRIMARY KEY, name TEXT)";
                cmd.ExecuteNonQuery();
            }

            var jwt = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JwtSettings:Authority"] = "https://login.example.test",
                    ["JwtSettings:ClientId"] = "test-client",
                })
                .Build();

            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddBifrostEndpoints(o =>
                    {
                        o.BindJwtSettings(jwt.GetSection("JwtSettings"));
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = $"Data Source={dbPath}";
                            e.Provider = "sqlite";
                            e.Path = "/graphql/secured";
                            e.PlaygroundPath = "/graphiql/secured";
                            e.DisableAuth = false; // requires auth
                        });
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = $"Data Source={dbPath}";
                            e.Provider = "sqlite";
                            e.Path = "/graphql/open";
                            e.PlaygroundPath = "/graphiql/open";
                            e.DisableAuth = true; // anonymous allowed
                        });
                    });
                });
                web.Configure(app => app.UseBifrostEndpoints());
            });

            return await builder.StartAsync();
        }

        private static HttpRequestMessage GraphQlPost(string path)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(
                    "{\"query\":\"{ __typename }\"}", System.Text.Encoding.UTF8, "application/json"),
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return req;
        }

        [Fact]
        public async Task SecuredEndpoint_Unauthenticated_IsDenied()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"endpoint-auth-{Guid.NewGuid():N}.db");
            using var host = await BuildHostAsync(dbPath);
            try
            {
                var client = host.GetTestClient();

                using var response = await client.SendAsync(GraphQlPost("/graphql/secured"));

                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                    "an auth-required endpoint must deny an unauthenticated API request");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) File.Delete(dbPath);
            }
        }

        [Fact]
        public async Task OpenEndpoint_Unauthenticated_IsNotGated()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"endpoint-auth-{Guid.NewGuid():N}.db");
            using var host = await BuildHostAsync(dbPath);
            try
            {
                var client = host.GetTestClient();

                using var response = await client.SendAsync(GraphQlPost("/graphql/open"));

                // The DisableAuth endpoint passes the auth gate: a GraphQL response (HTTP 200),
                // never a 401/302, even though a sibling endpoint requires auth.
                response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
                response.StatusCode.Should().NotBe(HttpStatusCode.Found);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) File.Delete(dbPath);
            }
        }
    }
}
