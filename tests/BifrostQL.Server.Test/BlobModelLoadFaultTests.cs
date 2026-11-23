using System.Net;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The blob mount answers 503 when its endpoint's model cannot load (database down). That is a
/// server fault, so it goes through <see cref="BifrostErrorSink.ServerFault"/>, the one
/// log-the-detail seam, as the saved-objects 503 does
/// (<see cref="SavedObjectsEndpointSelectionTests"/>): at Error, with the driver exception
/// attached and a constant detail that cannot echo the connection string.
/// </summary>
[Collection(ErrorSinkLoggerCollection.Name)]
public sealed class BlobModelLoadFaultTests : IAsyncLifetime
{
    private IHost? _host;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task ModelLoadFailure_LogsTheFaultAtErrorThroughTheSink_WithoutTheConnectionString()
    {
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        var missing = Path.Combine(Path.GetTempPath(), $"blob-missing-{Guid.NewGuid():N}", "absent.db");
        var logger = new FakeLogger();
        var previous = BifrostErrorSink.Logger;
        BifrostErrorSink.Logger = logger;
        try
        {
            _host = await new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddBifrostEndpoints(o => o.AddEndpoint(e =>
                    {
                        e.ConnectionString = $"Data Source={missing};Mode=ReadOnly";
                        e.Provider = "sqlite";
                        e.Path = "/graphql";
                        e.DisableAuth = true;
                    }));
                    services.AddBifrostEngine();
                });
                web.Configure(app =>
                {
                    app.UseBifrostEndpoints();
                    app.UseBifrostBlobs();
                });
            }).StartAsync();

            using var response = await _host.GetTestClient().GetAsync("/_blob/files/data?k.id=1");

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await response.Content.ReadAsStringAsync()).Should().Be("The blob endpoint is temporarily unavailable.");
        }
        finally
        {
            BifrostErrorSink.Logger = previous;
        }

        var fault = logger.Collector.GetSnapshot()
            .Should().ContainSingle(r => r.Message.Contains(nameof(BifrostBlobMiddleware))).Subject;
        fault.Level.Should().Be(LogLevel.Error);
        fault.Exception.Should().NotBeNull("the driver exception is the operator's diagnosis");
        fault.Message.Should().NotContain("Data Source");
    }
}
