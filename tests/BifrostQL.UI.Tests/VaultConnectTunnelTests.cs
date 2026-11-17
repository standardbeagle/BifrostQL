using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BifrostQL.UI.Vault;
using BifrostQL.UI.Web;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// An <c>ssh -L</c> tunnel started by vault connect used to outlive the connect it was
/// for: a failed WordPress discovery or a failed bind returned with the process still
/// running, and connecting a non-SSH entry afterwards left the old tunnel up until app
/// exit. The tunnel is the process boundary, so it is substituted; the endpoint, vault
/// file and connection state are real.
/// </summary>
public sealed class VaultConnectTunnelTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bifrost-vault-tunnel-{Guid.NewGuid():N}");
    private readonly ConnectionState _state = new();
    private readonly RecordingTunnel _tunnel = new();
    private WebApplication? _app;
    private HttpClient _client = null!;

    private static readonly VaultSshConfig Bastion = new("bastion.invalid", 22, "deploy", null);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _state.VaultPath = Path.Combine(_dir, "vault.enc");
        await VaultStore.Save(new VaultData
        {
            Servers =
            [
                new VaultServer("tunneled", "postgres", "db.internal", 5432, "app", "u", "p", null, Bastion, []),
                new VaultServer("wp-site", "mysql", "127.0.0.1", 3306, null, null, null, null, Bastion, ["wordpress"]),
                new VaultServer("direct", "postgres", "db.local", 5432, "app", "u", "p", null, null, []),
            ],
        }, _state.VaultPath);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapVaultEndpoints(_state, _tunnel);
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
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<HttpStatusCode> Connect(string name)
    {
        var response = await _client.PostAsync("/api/vault/connect",
            new StringContent(JsonSerializer.Serialize(new { name }), Encoding.UTF8, "application/json"));
        return response.StatusCode;
    }

    [Fact]
    public async Task SuccessfulSshConnect_KeepsItsTunnel()
    {
        (await Connect("tunneled")).Should().Be(HttpStatusCode.OK);

        _tunnel.Running.Should().BeTrue("the bound connection goes through it");
    }

    [Fact]
    public async Task FailedWordPressDiscovery_StopsTheTunnel()
    {
        (await Connect("wp-site")).Should().Be(HttpStatusCode.BadRequest);

        _tunnel.Starts.Should().Be(1);
        _tunnel.Running.Should().BeFalse();
    }

    /// <summary>
    /// The catch path: the client gives up while the tunnel is coming up, so the bind
    /// that follows throws OperationCanceledException with the tunnel already running.
    /// </summary>
    [Fact]
    public async Task ConnectAbortedAfterTheTunnelStarted_StopsTheTunnel()
    {
        _tunnel.HoldStartUntilRequestAborted = true;
        using var abort = new CancellationTokenSource();
        var request = _client.PostAsync("/api/vault/connect",
            new StringContent(JsonSerializer.Serialize(new { name = "tunneled" }), Encoding.UTF8, "application/json"),
            abort.Token);
        await _tunnel.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        abort.Cancel();
        await FluentActions.Awaiting(() => request).Should().ThrowAsync<OperationCanceledException>();

        await _tunnel.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _tunnel.Starts.Should().Be(1);
        _tunnel.Running.Should().BeFalse();
        _state.Active.Should().BeNull("the aborted connect bound nothing");
    }

    [Fact]
    public async Task NonSshConnect_StopsTheTunnelLeftByAnEarlierEntry()
    {
        (await Connect("tunneled")).Should().Be(HttpStatusCode.OK);
        _tunnel.Running.Should().BeTrue();

        (await Connect("direct")).Should().Be(HttpStatusCode.OK);

        _tunnel.Running.Should().BeFalse();
        _state.Active!.ConnectionString.Should().Contain("db.local");
    }

    [Fact]
    public async Task FailedNonSshConnect_LeavesTheBoundTunnelAlone()
    {
        (await Connect("tunneled")).Should().Be(HttpStatusCode.OK);

        (await Connect("no-such-entry")).Should().Be(HttpStatusCode.NotFound);

        _tunnel.Running.Should().BeTrue("the still-bound connection uses it");
    }

    private sealed class RecordingTunnel : ISshTunnel
    {
        public int Starts { get; private set; }
        public bool Running { get; private set; }
        public bool HoldStartUntilRequestAborted { get; set; }
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<int> StartAsync(SshTunnelConfig config, CancellationToken ct = default)
        {
            Starts++;
            Running = true;
            StartEntered.TrySetResult();
            if (HoldStartUntilRequestAborted)
            {
                // The tunnel is up; the request is aborted while StartAsync returns.
                var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using (ct.Register(() => aborted.TrySetResult()))
                    await aborted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            return 54321;
        }

        public Task StopAsync()
        {
            Running = false;
            Stopped.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<WpCredentials> DiscoverWordPressAsync(SshTunnelConfig sshConfig, WpDiscoverConfig wpConfig, CancellationToken ct = default) =>
            throw new InvalidOperationException("WP-CLI failed: Error: This does not seem to be a WordPress installation.");
    }
}
