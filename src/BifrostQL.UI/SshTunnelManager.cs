using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace BifrostQL.UI;

public record SshTunnelConfig(
    string SshHost,
    int SshPort,
    string SshUsername,
    string? IdentityFile,
    string RemoteHost,
    int RemotePort);

public record WpDiscoverConfig(string? WpPath, string? WpRoot);

public record WpCredentials(string DbName, string DbUser, string DbPassword, string DbHost);

/// <summary>
/// Manages an SSH tunnel by spawning the system <c>ssh</c> binary with <c>-L</c> local port forwarding.
/// Uses the system SSH agent, config, and known_hosts automatically.
/// </summary>
public sealed class SshTunnelManager : IAsyncDisposable
{
    private Process? _process;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsActive => _process is { HasExited: false };
    public int? LocalPort { get; private set; }
    public string? SshHost { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// Starts an SSH tunnel. Finds a free local port, spawns ssh -L, and waits
    /// for the tunnel to become ready by probing the local port.
    /// </summary>
    public async Task<int> StartAsync(SshTunnelConfig config, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // Tear down any existing tunnel
            await StopInternalAsync();

            // FindFreePort picks a port by binding 0, then releases it before ssh -L
            // claims it — a TOCTOU window in which another process can grab the port.
            // ssh (ExitOnForwardFailure=yes) then exits with a bind/"Address already in
            // use" error. Since a fresh FindFreePort call almost always hands back a
            // different port, retry once on a detected port conflict before giving up.
            const int maxAttempts = 2;
            for (var attempt = 1; ; attempt++)
            {
                var localPort = FindFreePort();
                var psi = new ProcessStartInfo("ssh")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                AddSshTunnelArguments(psi.ArgumentList, config, localPort);

                _process = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to start ssh process");

                // Wait for tunnel to become ready by probing the local port
                var ready = await WaitForTunnelAsync(localPort, ct);
                if (ready)
                {
                    LocalPort = localPort;
                    SshHost = config.SshHost;
                    LastError = null;
                    return localPort;
                }

                var stderr = "";
                if (_process.HasExited)
                {
                    stderr = await _process.StandardError.ReadToEndAsync(ct);
                }
                await StopInternalAsync();

                if (IsLocalPortConflict(stderr) && attempt < maxAttempts)
                {
                    // Local port was snatched between FindFreePort and ssh -L; try a
                    // fresh port. Only local-forward bind failures are retried — a
                    // genuine auth/host failure would just fail again more slowly.
                    continue;
                }

                LastError = IsLocalPortConflict(stderr)
                    ? $"Local port {localPort} was taken before the SSH tunnel could bind it " +
                      $"(retried {attempt} time(s)). Retry the connection. Detail: {stderr.Trim()}"
                    : string.IsNullOrWhiteSpace(stderr)
                        ? "SSH tunnel failed to start within timeout"
                        : stderr.Trim();
                throw new InvalidOperationException($"SSH tunnel failed: {LastError}");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public object GetStatus() => new
    {
        isActive = IsActive,
        localPort = LocalPort,
        sshHost = SshHost,
        error = LastError,
    };

    /// <summary>
    /// Discovers WordPress database credentials by running wp-cli over SSH.
    /// Does not require an active tunnel — opens its own SSH connection.
    /// </summary>
    public async Task<WpCredentials> DiscoverWordPressAsync(
        SshTunnelConfig sshConfig, WpDiscoverConfig wpConfig, CancellationToken ct = default)
    {
        var wpPath = ShellEscape(wpConfig.WpPath ?? "wp");
        var wpCmd = $"{wpPath} config list --fields=name,value --format=json";
        if (!string.IsNullOrWhiteSpace(wpConfig.WpRoot))
            wpCmd = $"{wpPath} --path={ShellEscape(wpConfig.WpRoot)} config list --fields=name,value --format=json";

        var (exitCode, stdout, stderr) = await RunSshCommandAsync(sshConfig, wpCmd, ct);

        if (exitCode != 0)
        {
            var msg = string.IsNullOrWhiteSpace(stderr) ? $"wp-cli exited with code {exitCode}" : stderr.Trim();
            throw new InvalidOperationException($"WP-CLI failed: {msg}");
        }

        return ParseWpConfigOutput(stdout);
    }

    private async Task<(int exitCode, string stdout, string stderr)> RunSshCommandAsync(
        SshTunnelConfig config, string command, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", "ConnectTimeout=10",
            "-p", config.SshPort.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(config.IdentityFile))
        {
            args.AddRange(new[] { "-i", config.IdentityFile });
        }

        args.Add($"{config.SshUsername}@{config.SshHost}");
        args.Add(command);

        var psi = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ssh process");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);

        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    private static WpCredentials ParseWpConfigOutput(string json)
    {
        // wp config list --format=json returns:
        // [{"name":"DB_NAME","value":"wp_db"},{"name":"DB_USER","value":"root"}, ...]
        
        // Trim whitespace and check for empty output
        var trimmed = json?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new InvalidOperationException("WP-CLI returned empty output");
        
        // Check if output starts with [ (expected JSON array). Anything else
        // is the wrong format even if it parses as JSON (e.g. a bare object).
        if (!trimmed.StartsWith("["))
            throw new InvalidOperationException($"WP-CLI returned wrong format (expected JSON array): {trimmed[..Math.Min(100, trimmed.Length)]}");
        
        JsonElement entries;
        try
        {
            entries = JsonSerializer.Deserialize<JsonElement>(trimmed);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse WP-CLI JSON output: {ex.Message}. Output: {trimmed[..Math.Min(200, trimmed.Length)]}");
        }
        
        if (entries.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Unexpected wp-cli output format: expected JSON array");

        string? dbName = null, dbUser = null, dbPassword = null, dbHost = null;

        foreach (var entry in entries.EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString();
            var value = entry.GetProperty("value").GetString() ?? "";

            switch (name)
            {
                case "DB_NAME": dbName = value; break;
                case "DB_USER": dbUser = value; break;
                case "DB_PASSWORD": dbPassword = value; break;
                case "DB_HOST": dbHost = value; break;
            }
        }

        return new WpCredentials(
            DbName: dbName ?? throw new InvalidOperationException("DB_NAME not found in wp-cli output"),
            DbUser: dbUser ?? throw new InvalidOperationException("DB_USER not found in wp-cli output"),
            DbPassword: dbPassword ?? "",
            DbHost: dbHost ?? "localhost");
    }

    private static void AddSshTunnelArguments(Collection<string> args, SshTunnelConfig config, int localPort)
    {
        args.Add("-N");
        args.Add("-o");
        args.Add("BatchMode=yes");
        args.Add("-o");
        args.Add("StrictHostKeyChecking=accept-new");
        args.Add("-o");
        args.Add("ExitOnForwardFailure=yes");
        args.Add("-o");
        args.Add("ServerAliveInterval=30");
        args.Add("-o");
        args.Add("ServerAliveCountMax=3");
        args.Add("-p");
        args.Add(config.SshPort.ToString());

        if (!string.IsNullOrWhiteSpace(config.IdentityFile))
        {
            args.Add("-i");
            args.Add(config.IdentityFile);
        }

        args.Add("-L");
        args.Add($"127.0.0.1:{localPort}:{config.RemoteHost}:{config.RemotePort}");
        args.Add($"{config.SshUsername}@{config.SshHost}");
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Detects an ssh local-forward bind failure (the chosen local port was taken
    /// between <see cref="FindFreePort"/> and <c>ssh -L</c>). With
    /// ExitOnForwardFailure=yes, ssh emits messages like
    /// "bind [127.0.0.1]:PORT: Address already in use" / "cannot listen to port".
    /// </summary>
    private static bool IsLocalPortConflict(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        return stderr.Contains("Address already in use", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("cannot listen to port", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("bind: ", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> WaitForTunnelAsync(int localPort, CancellationToken ct)
    {
        // Probe the local port up to 30 times with 200ms delay (6 seconds total)
        for (var i = 0; i < 30; i++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, localPort, ct);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(200, ct);
            }
        }
        return false;
    }

    private async Task StopInternalAsync()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch { /* best effort */ }
        finally
        {
            _process.Dispose();
            _process = null;
            LocalPort = null;
            SshHost = null;
        }
    }

    private static string ShellEscape(string value)
    {
        // Single-quote escaping for shell arguments
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lock.WaitAsync();
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}
