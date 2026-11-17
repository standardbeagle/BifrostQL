using System.Collections.Concurrent;
using System.Text.Json;
using BifrostQL.UI.NativeBridge;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// The bridge used to handle one message at a time in arrival order, with
/// <see cref="CancellationToken.None"/> for every handler: a slow exec-sql or an open
/// credential prompt held every later request until the webview's own timeout, and
/// a timed-out request kept running on the host. These facts drive
/// <see cref="BridgeDispatcher.Post"/>, the entry point NativeBridgeHost hands every
/// webview message to.
/// </summary>
public sealed class BridgeDispatcherConcurrencyTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly ConcurrentQueue<string> _sent = new();
    private readonly SemaphoreSlim _sentSignal = new(0);

    private BridgeDispatcher NewDispatcher(int maxConcurrent = BridgeDispatcher.DefaultMaxConcurrentRequests) =>
        new(m => { _sent.Enqueue(m); _sentSignal.Release(); }, maxConcurrentRequests: maxConcurrent);

    private async Task<JsonElement> NextReply()
    {
        (await _sentSignal.WaitAsync(Wait)).Should().BeTrue("a reply should have been sent");
        _sent.TryDequeue(out var raw).Should().BeTrue();
        return JsonDocument.Parse(raw!).RootElement;
    }

    private static string Request(string id, string kind, string payload = "{}") =>
        $$"""{"id":"{{id}}","kind":"{{kind}}","payload":{{payload}}}""";

    [Fact]
    public async Task SlowHandler_DoesNotHoldALaterRequest()
    {
        var d = NewDispatcher();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        d.Register("slow", async (_, _) => { await release.Task; return "slow"; });
        d.Register("fast", (_, _) => Task.FromResult<object?>("fast"));

        d.Post(Request("s", "slow"));
        d.Post(Request("f", "fast"));

        var first = await NextReply();
        first.GetProperty("id").GetString().Should().Be("f", "the fast request must not queue behind the slow one");
        release.SetResult();
        (await NextReply()).GetProperty("id").GetString().Should().Be("s");
    }

    [Fact]
    public async Task CancelEnvelope_CancelsThatRequestsToken()
    {
        var d = NewDispatcher();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        d.Register("query", async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "finished";
        });

        d.Post(Request("q1", "query"));
        await entered.Task.WaitAsync(Wait);
        d.Post(Request("c1", BridgeDispatcher.CancelKind, """{"id":"q1"}"""));

        var reply = await NextReply();
        reply.GetProperty("id").GetString().Should().Be("q1", "the cancel itself gets no reply");
        reply.GetProperty("kind").GetString().Should().Be("error");
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelForAFinishedRequest_IsIgnored()
    {
        var d = NewDispatcher();
        d.Register("k", (_, _) => Task.FromResult<object?>("x"));

        await d.DispatchAsync(Request("done", "k"));
        await NextReply();
        await d.DispatchAsync(Request("c", BridgeDispatcher.CancelKind, """{"id":"done"}"""));

        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentHandlers_AreCapped()
    {
        var d = NewDispatcher(maxConcurrent: 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var peak = 0;
        var twoRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        d.Register("hold", async (_, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            if (now == 2) twoRunning.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref running);
            return null;
        });

        for (var i = 0; i < 5; i++)
            d.Post(Request($"h{i}", "hold"));
        await twoRunning.Task.WaitAsync(Wait);
        // Give the other three every chance to (wrongly) start.
        await Task.Delay(200);
        Volatile.Read(ref peak).Should().Be(2);

        release.SetResult();
        for (var i = 0; i < 5; i++)
            (await NextReply()).GetProperty("kind").GetString().Should().Be("result");
        peak.Should().Be(2);
    }

    [Fact]
    public async Task CancelReachesARequestWaitingForASlot()
    {
        var d = NewDispatcher(maxConcurrent: 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        d.Register("hold", async (_, _) => { entered.TrySetResult(); await release.Task; return "held"; });
        d.Register("never", (_, _) => throw new InvalidOperationException("must not run"));

        d.Post(Request("h", "hold"));
        await entered.Task.WaitAsync(Wait);
        var queued = d.DispatchAsync(Request("w", "never"));
        await d.DispatchAsync(Request("c", BridgeDispatcher.CancelKind, """{"id":"w"}"""));
        await queued.WaitAsync(Wait);

        var reply = await NextReply();
        reply.GetProperty("id").GetString().Should().Be("w");
        reply.GetProperty("kind").GetString().Should().Be("error");
        reply.GetProperty("payload").GetProperty("message").GetString().Should().NotContain("must not run");
        release.SetResult();
        (await NextReply()).GetProperty("id").GetString().Should().Be("h");
    }

    [Fact]
    public async Task DuplicateInFlightId_IsRefused()
    {
        var d = NewDispatcher();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        d.Register("hold", async (_, _) => { entered.TrySetResult(); await release.Task; return "first"; });

        d.Post(Request("same", "hold"));
        await entered.Task.WaitAsync(Wait);
        await d.DispatchAsync(Request("same", "hold")).WaitAsync(Wait);

        var refused = await NextReply();
        refused.GetProperty("kind").GetString().Should().Be("error");
        release.SetResult();
        (await NextReply()).GetProperty("payload").GetString().Should().Be("first");
    }

    [Fact]
    public async Task Clear_CancelsInFlightRequests()
    {
        var d = NewDispatcher();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        d.Register("query", async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return null; });

        d.Post(Request("q", "query"));
        await entered.Task.WaitAsync(Wait);
        d.Clear();

        (await NextReply()).GetProperty("kind").GetString().Should().Be("error");
    }

    /// <summary>
    /// save-vault-entry is load-modify-save of one file. Run concurrently, two saves
    /// each wrote a vault missing the other's entry.
    /// </summary>
    [Fact]
    public async Task ConcurrentVaultSaves_KeepEveryEntry()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bifrost-vault-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var vaultPath = Path.Combine(dir, "vault.enc");
            var d = NewDispatcher();
            // save-vault-entry never touches the window (only request-credential does).
            new VaultBridgeHandlers(window: null!, logger: null, vaultPath).Register(new DispatcherRegistry(d));

            const int saves = 12;
            await Task.WhenAll(Enumerable.Range(0, saves).Select(i => Task.Run(() => d.DispatchAsync(
                Request($"s{i}", "save-vault-entry", $$"""{"vaultName":"entry-{{i}}","provider":"postgres","host":"h{{i}}"}""")))))
                .WaitAsync(Wait);

            var names = (await BifrostQL.UI.Vault.VaultStore.Load(vaultPath)).Servers.Select(s => s.Name);
            names.Should().BeEquivalentTo(Enumerable.Range(0, saves).Select(i => $"entry-{i}"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class DispatcherRegistry(BridgeDispatcher d) : IBridgeRegistry
    {
        public void Register(string kind, Func<JsonElement, CancellationToken, Task<object?>> handler) => d.Register(kind, handler);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value
               && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
