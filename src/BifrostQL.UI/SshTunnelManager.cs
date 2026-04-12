using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
public sealed class SshTunnelManager : IDisposable
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

            var localPort = FindFreePort();
            var args = BuildSshArgs(config, localPort);

            var psi = new ProcessStartInfo("ssh", args)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            _process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ssh process");

            // Wait for tunnel to become ready by probing the local port
            var ready = await WaitForTunnelAsync(localPort, ct);
            if (!ready)
            {
                var stderr = "";
                if (_process.HasExited)
                {
                    stderr = await _process.StandardError.ReadToEndAsync(ct);
                }
                await StopInternalAsync();
                LastError = string.IsNullOrWhiteSpace(stderr)
                    ? "SSH tunnel failed to start within timeout"
                    : stderr.Trim();
                throw new InvalidOperationException($"SSH tunnel failed: {LastError}");
            }

            LocalPort = localPort;
            SshHost = config.SshHost;
            LastError = null;
            return localPort;
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
        var wpPath = wpConfig.WpPath ?? "wp";
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
        
        // Check if output starts with [ (expected JSON array)
        if (!trimmed.StartsWith("["))
            throw new InvalidOperationException($"WP-CLI returned non-JSON output: {trimmed[..Math.Min(100, trimmed.Length)]}");
        
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

    private static string BuildSshArgs(SshTunnelConfig config, int localPort)
    {
        var sb = new StringBuilder();
        sb.Append("-N ");  // no remote command
        sb.Append("-o BatchMode=yes ");
        sb.Append("-o StrictHostKeyChecking=accept-new ");
        sb.Append("-o ExitOnForwardFailure=yes ");
        sb.Append("-o ServerAliveInterval=30 ");
        sb.Append("-o ServerAliveCountMax=3 ");
        sb.Append($"-p {config.SshPort} ");

        if (!string.IsNullOrWhiteSpace(config.IdentityFile))
            sb.Append($"-i {config.IdentityFile} ");

        sb.Append($"-L 127.0.0.1:{localPort}:{config.RemoteHost}:{config.RemotePort} ");
        sb.Append($"{config.SshUsername}@{config.SshHost}");

        return sb.ToString();
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lock.Wait();
        try
        {
            StopInternalAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}
