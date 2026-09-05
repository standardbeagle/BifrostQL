using System.Net;
using System.Net.Http;
using System.Text.Json;
using BifrostQL.UI.Web;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// M26b follow-up: the peer-auth connection form must never offer (or submit)
/// an OS user the <see cref="PsqlDatabaseLister"/> gate would refuse. The host
/// exposes the permitted set — the current user plus the
/// BIFROST_UI_PSQL_PEER_USERS allow-list — as a small read so the form can
/// default to the current user instead of hardcoding 'postgres'.
/// </summary>
public sealed class PeerUsersEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapConnectionEndpoints(new ConnectionState());
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
    public async Task PeerUsers_ReturnsCurrentUserAsPermitted()
    {
        var response = await _client.GetAsync("/api/databases/peer-users");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the form needs this read to default the OS user to one the gate permits");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("current").GetString().Should().Be(Environment.UserName);
        var permitted = doc.RootElement.GetProperty("permitted").EnumerateArray()
            .Select(e => e.GetString()).ToArray();
        permitted.Should().Contain(Environment.UserName,
            "the gate always permits the user the host runs as, so the read must offer it");
    }

    [Fact]
    public async Task RefusalMessage_NamesThePermittedAccounts_NotTheRequestedOne()
    {
        // The form renders the refusal text verbatim as a validation message, so
        // it must name the permitted accounts (host-derived) and must NOT echo
        // the caller-supplied OS user.
        const string requested = "definitely-not-a-real-user-xyz";

        var act = async () => await PsqlDatabaseLister.ListDatabasesAsync(
            "Host=/tmp;Username=peer", psqlUser: requested, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(Environment.UserName,
                "the refusal must name the permitted accounts so the user can pick one")
            .And.NotContain(requested,
                "caller-supplied identifiers never cross into error text");
    }
}
