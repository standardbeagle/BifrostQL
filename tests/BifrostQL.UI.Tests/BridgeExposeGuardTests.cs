using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// M24a: the HTTP bridge runs SQL with no authentication of its own — its only
/// safeguard is loopback binding. --expose binds 0.0.0.0, so the two flags
/// together publish an unauthenticated SQL console to the LAN. Startup must
/// refuse the combination (non-zero exit, message naming both flags) rather
/// than start a host whose bridge is reachable off-box.
/// </summary>
public sealed class BridgeExposeGuardTests
{
    [Fact]
    public async Task ExposePlusHttpBridge_ExitsNonZero_WithClearMessage()
    {
        var port = GetFreePort();
        var uiDll = ResolveUiAssembly();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{uiDll}\" --headless --expose --enable-http-bridge --port {port}",
            WorkingDirectory = Path.GetDirectoryName(uiDll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Pre-fix the host starts and runs forever; the timeout kill is the RED path.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var exited = true;
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            exited = false;
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }

        string captured;
        lock (output) captured = output.ToString();

        exited.Should().BeTrue(
            "the combination must be refused at startup, not start a LAN-exposed bridge. Output: {0}", captured);
        process.ExitCode.Should().NotBe(0, "refusal must exit non-zero. Output: {0}", captured);
        captured.Should().Contain("--expose").And.Contain("--enable-http-bridge",
            "the operator must be told which flag combination is forbidden. Output: {0}", captured);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ResolveUiAssembly([CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "Release";
        var dll = Path.Combine(repoRoot, "src", "BifrostQL.UI", "bin", config, "net10.0", "bifrostui.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException($"BifrostQL.UI assembly not found at {dll}. Build the UI project first.");
        return dll;
    }
}
