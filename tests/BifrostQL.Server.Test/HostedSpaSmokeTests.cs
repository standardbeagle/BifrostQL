using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// WebApplicationFactory smoke test for the hosted SPA/API mode: the HostedSpa sample
    /// serves a single-page app and a BifrostQL GraphQL endpoint from one process. These
    /// tests verify the SPA fallback and the GraphQL endpoint coexist on the same host.
    /// </summary>
    public class HostedSpaSmokeTests : IClassFixture<HostedSpaSmokeTests.HostedSpaFactory>
    {
        private readonly HostedSpaFactory _factory;

        public HostedSpaSmokeTests(HostedSpaFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task UnknownRoute_FallsBackToIndexHtml()
        {
            // Arrange
            var client = _factory.CreateClient();

            // Act: a route the SPA owns (not GraphQL, playground, api, or health).
            var response = await client.GetAsync("/dashboard/orders/42");

            // Assert: the SPA index.html is served so client-side routing can take over.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("BifrostQL HostedSpa Sample");
        }

        [Fact]
        public async Task PostGraphql_ReturnsGraphqlResponse_OnSameHost()
        {
            // Arrange
            var client = _factory.CreateClient();

            // Act: query the seeded widgets table through the GraphQL endpoint.
            var response = await client.PostAsJsonAsync(
                "/graphql",
                new { query = "{ widgets { data { id name color } } }" });

            // Assert: a well-formed GraphQL response served from the same host as the SPA.
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            doc.RootElement.TryGetProperty("errors", out _)
                .Should().BeFalse("the widgets query should resolve without GraphQL errors");

            var widgets = doc.RootElement
                .GetProperty("data")
                .GetProperty("widgets")
                .GetProperty("data");
            widgets.GetArrayLength().Should().BeGreaterThan(0);
        }

        [Theory]
        [InlineData("/graphql")]
        [InlineData("/playground")]
        public async Task ExcludedPrefixes_AreNotStolenBySpaFallback(string path)
        {
            // Arrange
            var client = _factory.CreateClient();

            // Act
            var response = await client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();

            // Assert: the excluded path is handled by its own endpoint, not the SPA
            // index.html fallback, so it never returns the SPA shell markup.
            body.Should().NotContain(
                "BifrostQL HostedSpa Sample",
                $"'{path}' is an excluded prefix and must not fall back to the SPA index.html");
        }

        /// <summary>
        /// Hosts the HostedSpa sample for the smoke test. Each test run points the sample
        /// at a fresh, uniquely named SQLite database so the seed always runs and runs do
        /// not collide with a stale file left in the test output directory.
        /// </summary>
        public sealed class HostedSpaFactory : WebApplicationFactory<Program>
        {
            private readonly string _dbPath =
                Path.Combine(Path.GetTempPath(), $"hostedspa-smoke-{Guid.NewGuid():N}.db");

            protected override IHost CreateHost(IHostBuilder builder)
            {
                builder.ConfigureHostConfiguration(config =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:bifrost"] = $"Data Source={_dbPath}",
                    }));

                return base.CreateHost(builder);
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing && File.Exists(_dbPath))
                    File.Delete(_dbPath);
            }
        }
    }
}
