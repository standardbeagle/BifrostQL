using System.Data.Common;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.UI.Web;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// M26a: /api/connection/test and /api/databases return raw ex.Message. A driver
/// exception can embed the full connection string — Password= and all — so the
/// error body must be scrubbed like the vault connect endpoint already does.
///
/// No real driver echoes the password on demand, so the fixture registers a
/// sentinel-gated fake MySql factory through the public
/// <see cref="DbConnFactoryResolver.Register"/> seam: it throws with a
/// Password=-carrying message ONLY for the marker connection string, and is
/// inert for anything else. Without the scrub both response bodies contain the
/// password; with it they must not.
/// </summary>
public sealed class ConnectionEndpointsScrubbingTests : IAsyncLifetime
{
    private const string Marker = "unit-test-scrub-marker";
    private const string MarkerConnectionString = "Server=" + Marker + ";Password=hunter2";

    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        DbConnFactoryResolver.Register(BifrostDbProvider.MySql, cs => new MarkerFactory(cs));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapConnectionEndpoints(new ConnectionState());
        await _app.StartAsync();

        var address = _app.Urls.Single();
        _client = new HttpClient { BaseAddress = new Uri(address) };
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
    public async Task ConnectionTest_DriverErrorBody_NeverContainsPassword()
    {
        var body = await Post("/api/connection/test", new { connectionString = MarkerConnectionString, provider = "mysql" });

        Assert.Equal(HttpStatusCode.BadRequest, body.Status);
        Assert.DoesNotContain("hunter2", body.Text);
        Assert.Contains("Password=****", body.Text);
    }

    [Fact]
    public async Task ListDatabases_DriverErrorBody_NeverContainsPassword()
    {
        var body = await Post("/api/databases", new { connectionString = MarkerConnectionString, provider = "mysql" });

        Assert.Equal(HttpStatusCode.BadRequest, body.Status);
        Assert.DoesNotContain("hunter2", body.Text);
        Assert.Contains("Password=****", body.Text);
    }

    private async Task<(HttpStatusCode Status, string Text)> Post(string path, object payload)
    {
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _client.PostAsync(path, content);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Stands in for a DB driver: any operation on the marker connection string
    /// throws with the connection string embedded in the message — the shape the
    /// scrub exists for. Non-marker strings get a plain failure so a concurrent
    /// test accidentally resolving MySql through the registry cannot pass by it.
    /// </summary>
    private sealed class MarkerFactory : IDbConnFactory
    {
        private readonly string _connectionString;

        public MarkerFactory(string connectionString) => _connectionString = connectionString;

        private Exception DriverError()
            => new InvalidOperationException(
                _connectionString.Contains(Marker, StringComparison.Ordinal)
                    ? $"driver exploded while opening '{_connectionString}'"
                    : "marker factory only serves the sentinel connection string");

        public DbConnection GetConnection() => throw DriverError();

        public ISqlDialect Dialect => throw DriverError();
        public ISchemaReader SchemaReader => throw DriverError();
        public ITypeMapper TypeMapper => throw DriverError();

        public Task<string[]> ListDatabasesAsync(CancellationToken cancellationToken = default)
            => throw DriverError();
    }
}
