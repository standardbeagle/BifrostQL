using System.Net;
using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.SavedObjects;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The endpoint whose model keys the <c>/_saved-objects</c> owner partition is chosen at mount
/// time, not per request. An unnamed endpoint on a multi-endpoint host, or a name no endpoint is
/// registered under, used to pass the mount (MountAuthRequirement's single-endpoint fallback
/// masked the typo) and then throw on every signed-in request as an unhandled 500. A model that
/// cannot load answers the sanitized 503, never a raw 500.
/// </summary>
public sealed class SavedObjectsEndpointSelectionTests : IAsyncLifetime
{
    private const string UserHeader = "X-Test-User";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"so-endpoint-{Guid.NewGuid():N}.db");
    private readonly string _savedObjectsDir = Path.Combine(Path.GetTempPath(), $"so-endpoint-store-{Guid.NewGuid():N}");
    private IHost? _host;

    public Task InitializeAsync()
    {
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NULL);";
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (Directory.Exists(_savedObjectsDir)) Directory.Delete(_savedObjectsDir, recursive: true);
    }

    private Task<IHost> StartAsync(string? graphQlEndpoint, params (string Path, string ConnectionString)[] endpoints)
        => new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddBifrostSavedObjects(new FileSavedObjectStore(_savedObjectsDir));
                services.AddBifrostEndpoints(o =>
                {
                    foreach (var (path, connectionString) in endpoints)
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = connectionString;
                            e.Provider = "sqlite";
                            e.Path = path;
                            e.DisableAuth = true;
                        });
                });
            });
            web.Configure(app =>
            {
                // Stands in for the host's authentication middleware.
                app.Use((context, next) =>
                {
                    var user = context.Request.Headers[UserHeader].ToString();
                    if (!string.IsNullOrEmpty(user))
                        context.User = new ClaimsPrincipal(new ClaimsIdentity(
                            new[] { new Claim(ClaimTypes.NameIdentifier, user) }, authenticationType: "test"));
                    return next(context);
                });
                app.UseBifrostEndpoints();
                app.UseBifrostSavedObjects(o =>
                {
                    o.RequireAuth = true;
                    o.GraphQlEndpoint = graphQlEndpoint;
                });
            });
        }).StartAsync();

    private async Task<HttpResponseMessage> GetAsSignedInAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_saved-objects");
        request.Headers.Add(UserHeader, "alice");
        return await _host!.GetTestClient().SendAsync(request);
    }

    [Fact]
    public async Task Mount_RefusesToStart_WhenSeveralEndpointsAreRegisteredAndNoneIsNamed()
    {
        var start = () => StartAsync(null, ("/graphql", $"Data Source={_dbPath}"), ("/other", $"Data Source={_dbPath}"));

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*GraphQlEndpoint*");
    }

    [Fact]
    public async Task Mount_RefusesToStart_WhenTheNamedEndpointIsNotRegistered()
    {
        var start = () => StartAsync("/grapql", ("/graphql", $"Data Source={_dbPath}"));

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*'/grapql'*");
    }

    [Fact]
    public async Task Mount_ServesTheNamedEndpoint_OnAMultiEndpointHost()
    {
        // Control for the two refusals: naming a registered endpoint (any case) starts and serves.
        _host = await StartAsync("/GraphQL", ("/graphql", $"Data Source={_dbPath}"), ("/other", $"Data Source={_dbPath}"));

        using var response = await GetAsSignedInAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ModelLoadFailure_AnswersTheSanitized503_NotARaw500()
    {
        // The model cannot load (the database is unreachable). The owner partition needs the model's
        // tenant-context-key, so the request is refused as unavailable, without the driver's text.
        var missing = Path.Combine(Path.GetTempPath(), $"so-missing-{Guid.NewGuid():N}", "absent.db");
        _host = await StartAsync(null, ("/graphql", $"Data Source={missing};Mode=ReadOnly"));

        using var response = await GetAsSignedInAsync();
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, body);
        body.Should().NotContain("absent.db").And.NotContain("SQLite");
    }

    [Fact]
    public async Task ModelLoadFailure_LogsTheFaultAtError_WithoutTheConnectionString()
    {
        // The 503 is a server fault (database down). Logged at Debug it is invisible at the default
        // Information level, so the operator sees only clients failing.
        var missing = Path.Combine(Path.GetTempPath(), $"so-missing-{Guid.NewGuid():N}", "absent.db");
        var logger = new FakeLogger();
        var previous = BifrostErrorSink.Logger;
        BifrostErrorSink.Logger = logger;
        try
        {
            _host = await StartAsync(null, ("/graphql", $"Data Source={missing};Mode=ReadOnly"));

            using var response = await GetAsSignedInAsync();

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            BifrostErrorSink.Logger = previous;
        }

        var fault = logger.Collector.GetSnapshot()
            .Should().ContainSingle(r => r.Message.Contains(nameof(BifrostSavedObjectsMiddleware))).Subject;
        fault.Level.Should().Be(LogLevel.Error);
        fault.Exception.Should().NotBeNull("the driver exception is the operator's diagnosis");
        fault.Message.Should().NotContain("Data Source");
    }
}
