using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Boots the real BifrostQL UI host in <c>--headless</c> mode (server only, no
/// Photino window) on a free loopback port, so the HTTP API + static-file
/// endpoints can be exercised in-process instead of against a manually started
/// localhost:5000. Shared across the API test class via a collection fixture so
/// the host starts once.
/// </summary>
public sealed class HeadlessUiServer : IAsyncLifetime
{
    private Process? _process;
    private readonly StringBuilder _log = new();
    private readonly object _logLock = new();

    public HttpClient Client { get; private set; } = null!;
    public int Port { get; private set; }

    /// <summary>The wwwroot the host serves: the UI project's bin copy of the built SPA.</summary>
    public string WebRoot { get; private set; } = null!;

    // GetFreePort releases the port before the host binds it, so another process (a parallel
    // test assembly) can take it in between. The host then exits with this message, and only
    // that exit is retried on a fresh port; any other early exit fails the run.
    private const int MaxStartAttempts = 5;

    public async Task InitializeAsync()
    {
        var uiDll = ResolveUiAssembly();
        WebRoot = Path.Combine(Path.GetDirectoryName(uiDll)!, "wwwroot");

        for (var attempt = 1; ; attempt++)
        {
            Port = GetFreePort();
            Start(uiDll);
            try
            {
                await WaitForHealthAsync();
                return;
            }
            catch (InvalidOperationException) when (attempt < MaxStartAttempts && LostThePortRace())
            {
                StopProcess();
            }
        }
    }

    private void Start(string uiDll)
    {
        lock (_logLock) _log.Clear();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            // ContentRootPath is pinned to the assembly directory by the host
            // itself, so wwwroot resolves regardless of the working directory.
            Arguments = $"\"{uiDll}\" --headless --port {Port}",
            WorkingDirectory = Path.GetDirectoryName(uiDll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        _process = new Process { StartInfo = psi };
        _process.OutputDataReceived += (_, e) => Append(e.Data);
        _process.ErrorDataReceived += (_, e) => Append(e.Data);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        Client?.Dispose();
        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}") };
    }

    private bool LostThePortRace()
        => _process is { HasExited: true }
           && Snapshot().Contains($"Port {Port} is already in use", StringComparison.Ordinal);

    public Task DisposeAsync()
    {
        Client?.Dispose();
        StopProcess();
        return Task.CompletedTask;
    }

    private void StopProcess()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
            _process?.WaitForExit(5000);
        }
        catch
        {
            // Best effort — the test run is tearing down regardless.
        }
        _process?.Dispose();
        _process = null;
    }

    /// <summary>Polls /api/health until the host answers 200 or the timeout elapses.</summary>
    private async Task WaitForHealthAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                // HasExited turns true before the async readers deliver the last lines;
                // the parameterless WaitForExit waits for both streams to reach EOF.
                _process.WaitForExit();
                throw new InvalidOperationException(
                    $"Headless UI host exited early (code {_process.ExitCode}).\n{Snapshot()}");
            }
            try
            {
                // A short probe: whatever holds a contested port may accept and never answer,
                // and an unbounded wait would hide the host's exit until the deadline.
                using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var resp = await Client.GetAsync("/api/health", probe.Token);
                if (resp.IsSuccessStatusCode)
                    return;
            }
            catch (Exception ex)
            {
                last = ex; // connection refused until Kestrel is listening
            }
            await Task.Delay(250);
        }
        throw new TimeoutException(
            $"Headless UI host did not become healthy on port {Port} within 60s. " +
            $"Last error: {last?.Message}\n{Snapshot()}");
    }

    private void Append(string? line)
    {
        if (line is null) return;
        lock (_logLock) _log.AppendLine(line);
    }

    private string Snapshot()
    {
        lock (_logLock) return _log.ToString();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Locates the built <c>bifrostui.dll</c> in the UI project's own output
    /// directory (which carries wwwroot), matching the configuration the tests
    /// were built in. A ProjectReference copies the assembly into the test
    /// output but not its static web assets, so the host is launched from its
    /// own bin directory instead.
    /// </summary>
    private static string ResolveUiAssembly([CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "Release";
        var dll = Path.Combine(repoRoot, "src", "BifrostQL.UI", "bin", config, "net10.0", "bifrostui.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException(
                $"Built UI host not found at {dll}. Build BifrostQL.UI in {config} before running the API tests.");
        return dll;
    }
}

/// <summary>Collection that shares a single headless UI host across the API tests.</summary>
[CollectionDefinition(Name)]
public sealed class HeadlessUiServerCollection : ICollectionFixture<HeadlessUiServer>
{
    public const string Name = "headless-ui-server";
}
