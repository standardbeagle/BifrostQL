using System.Net;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Host.Test;

/// <summary>
/// Boots the BifrostQL.Host web entry point against an in-memory Sqlite
/// database and exercises the `/graphql` endpoint. Verifies the wiring
/// in Program.cs — JWT-optional, BifrostQL pipeline, CORS — survives a
/// real HTTP round-trip.
/// </summary>
public sealed class HostSmokeTests : IClassFixture<HostSmokeTests.HostFactory>
{
    private readonly HostFactory _factory;

    public HostSmokeTests(HostFactory factory) => _factory = factory;

    [Fact]
    public async Task GraphQL_DbSchemaIntrospection_ReturnsTableList()
    {
        using var client = _factory.CreateClient();

        var payload = JsonSerializer.Serialize(new
        {
            query = "query { _dbSchema { dbName graphQlName } }"
        });

        using var response = await client.PostAsync(
            "/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        // The seed table is `widgets`; the schema endpoint must surface it.
        body.Should().Contain("widgets");
        body.Should().NotContain("\"errors\"");
    }

    public sealed class HostFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"host-smoke-{Guid.NewGuid():N}.db");

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // BifrostQL.Host's appsettings.json hardcodes a SQL Server
            // connection string and `Provider: sqlserver`. Override both so
            // the smoke test runs without a database server, against a fresh
            // Sqlite file seeded by the schema loader.
            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));

            EnsureSqliteSeed(_dbPath);

            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BifrostQL:Provider"] = "sqlite",
                    ["BifrostQL:DisableAuth"] = "true",
                    ["BifrostQL:Path"] = "/graphql",
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

        private static void EnsureSqliteSeed(string dbPath)
        {
            // Trivial seed — one table with a primary key — so the schema
            // loader has something to introspect and the GraphQL pipeline
            // emits a non-empty schema.
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS widgets (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL)";
            cmd.ExecuteNonQuery();
        }
    }
}
