using System.Text.Json;
using System.Text.Json.Serialization;
using BifrostQL.Core.Model;

namespace BifrostQL.UI.Vault;

/// <summary>
/// Merges server sources according to priority chain:
/// 1. Environment variables (highest) — BIFROST_SERVERS, BIFROST_SERVER_<NAME>
/// 2. Vault file (default or --vault override)
/// </summary>
public static class VaultServerProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Load all servers from vault + environment variables, with env vars taking priority.
    /// Returns tuples of (server, source) where source is "vault" or "env".
    /// </summary>
    public static List<(VaultServer Server, string Source)> LoadServers(string? vaultPathOverride = null)
    {
        var servers = new List<(VaultServer Server, string Source)>();

        // Load from vault file (lowest priority)
        var vaultPath = vaultPathOverride ?? VaultStore.DefaultVaultPath;
        try
        {
            var vault = VaultStore.Load(vaultPath);
            servers.AddRange(vault.Servers.Select(s => (s, "vault")));
        }
        catch
        {
            // Vault doesn't exist or can't be read — continue with env vars only
        }

        // Load from environment variables (highest priority, override by name)
        var envServers = LoadFromEnvironment();
        foreach (var envServer in envServers)
        {
            servers.RemoveAll(s => s.Server.Name.Equals(envServer.Name, StringComparison.OrdinalIgnoreCase));
            servers.Add((envServer, "env"));
        }

        return servers;
    }

    /// <summary>
    /// Parse servers from environment variables.
    /// BIFROST_SERVERS: JSON array of VaultServer objects
    /// BIFROST_SERVER_<NAME>: raw connection string (provider auto-detected)
    /// </summary>
    private static List<VaultServer> LoadFromEnvironment()
    {
        var servers = new List<VaultServer>();

        // BIFROST_SERVERS — JSON array
        var jsonEnv = Environment.GetEnvironmentVariable("BIFROST_SERVERS");
        if (!string.IsNullOrWhiteSpace(jsonEnv))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<VaultServer>>(jsonEnv, JsonOptions);
                if (parsed is not null)
                    servers.AddRange(parsed);
            }
            catch
            {
                // Malformed JSON — skip silently
            }
        }

        // BIFROST_SERVER_<NAME> — individual connection strings
        foreach (var entry in Environment.GetEnvironmentVariables())
        {
            if (entry is not System.Collections.DictionaryEntry de) continue;
            var key = de.Key?.ToString();
            var value = de.Value?.ToString();
            if (key is null || value is null) continue;
            if (!key.StartsWith("BIFROST_SERVER_", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("BIFROST_SERVERS", StringComparison.OrdinalIgnoreCase)) continue;

            var suffix = key["BIFROST_SERVER_".Length..];
            if (string.IsNullOrWhiteSpace(suffix)) continue;

            var name = suffix.ToLowerInvariant().Replace('_', '-');

            try
            {
                var provider = DbConnFactoryResolver.DetectProvider(value);
                var server = ParseConnectionString(name, provider, value);

                // Override any existing server with same name from BIFROST_SERVERS
                servers.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                servers.Add(server);
            }
            catch
            {
                // Can't detect provider — skip
            }
        }

        return servers;
    }

    /// <summary>
    /// Parse a connection string into a VaultServer.
    /// </summary>
    private static VaultServer ParseConnectionString(string name, BifrostDbProvider provider, string connectionString)
    {
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq <= 0) continue;
            parts[segment[..eq].Trim()] = segment[(eq + 1)..].Trim();
        }

        return provider switch
        {
            BifrostDbProvider.PostgreSql => new VaultServer(
                name, "postgres",
                parts.GetValueOrDefault("Host", "localhost"),
                int.TryParse(parts.GetValueOrDefault("Port"), out var pgPort) ? pgPort : 5432,
                parts.GetValueOrDefault("Database"),
                parts.GetValueOrDefault("Username"),
                parts.GetValueOrDefault("Password"),
                parts.GetValueOrDefault("SSL Mode"),
                null, []),

            BifrostDbProvider.MySql => new VaultServer(
                name, "mysql",
                parts.GetValueOrDefault("Server", "localhost"),
                int.TryParse(parts.GetValueOrDefault("Port"), out var myPort) ? myPort : 3306,
                parts.GetValueOrDefault("Database"),
                parts.GetValueOrDefault("Uid") ?? parts.GetValueOrDefault("User Id"),
                parts.GetValueOrDefault("Pwd") ?? parts.GetValueOrDefault("Password"),
                parts.GetValueOrDefault("SslMode"),
                null, []),

            BifrostDbProvider.SqlServer => new VaultServer(
                name, "sqlserver",
                ParseSqlServerHost(parts.GetValueOrDefault("Server", "localhost"), out var sqlPort),
                sqlPort,
                parts.GetValueOrDefault("Database") ?? parts.GetValueOrDefault("Initial Catalog"),
                parts.GetValueOrDefault("User Id"),
                parts.GetValueOrDefault("Password"),
                null, null, []),

            BifrostDbProvider.Sqlite => new VaultServer(
                name, "sqlite",
                parts.GetValueOrDefault("Data Source", ""),
                0, null, null, null, null, null, []),

            _ => throw new ArgumentException($"Unknown provider: {provider}")
        };
    }

    /// <summary>
    /// SQL Server uses comma-separated port: "host,port"
    /// </summary>
    private static string ParseSqlServerHost(string server, out int port)
    {
        var comma = server.IndexOf(',');
        if (comma > 0 && int.TryParse(server[(comma + 1)..], out port))
            return server[..comma];
        port = 1433;
        return server;
    }

    /// <summary>
    /// Build a provider-specific connection string from a VaultServer.
    /// </summary>
    public static string BuildConnectionString(VaultServer server)
    {
        return server.Provider.ToLowerInvariant() switch
        {
            "sqlserver" => BuildSqlServerConnectionString(server),
            "postgres" => BuildPostgresConnectionString(server),
            "mysql" => BuildMySqlConnectionString(server),
            "sqlite" => $"Data Source={server.Host}",
            _ => throw new ArgumentException($"Unknown provider: {server.Provider}")
        };
    }

    private static string BuildSqlServerConnectionString(VaultServer s)
    {
        var parts = new List<string>();
        var host = s.Port != 1433 ? $"{s.Host},{s.Port}" : s.Host;
        parts.Add($"Server={host}");
        if (!string.IsNullOrWhiteSpace(s.Database)) parts.Add($"Database={s.Database}");
        if (!string.IsNullOrWhiteSpace(s.Username))
        {
            parts.Add($"User Id={s.Username}");
            parts.Add($"Password={s.Password ?? ""}");
        }
        else
        {
            parts.Add("Integrated Security=True");
        }
        parts.Add($"Encrypt={MapSqlServerEncrypt(s.SslMode)}");
        parts.Add("TrustServerCertificate=True");
        return string.Join(';', parts);
    }

    /// <summary>
    /// Map the vault entry's SslMode to a Microsoft.Data.SqlClient `Encrypt` value.
    /// SqlClient 6.x defaults to `Mandatory`, which causes connect failures from
    /// OpenSSL-3 hosts (e.g. WSL2) to older Windows SQL Servers because OpenSSL 3
    /// rejects the SQL Server's default TLS cipher suite. Setting `--ssl-mode false`
    /// (or `disable`/`optional`/`off`) on the vault entry produces `Encrypt=False`
    /// to bypass the TLS handshake.
    /// </summary>
    private static string MapSqlServerEncrypt(string? sslMode)
    {
        if (string.IsNullOrWhiteSpace(sslMode)) return "Mandatory";
        return sslMode.Trim().ToLowerInvariant() switch
        {
            "false" or "disable" or "disabled" or "off" or "none" or "optional" => "False",
            "true" or "require" or "required" or "mandatory" or "on" => "Mandatory",
            "strict" => "Strict",
            _ => sslMode, // pass through unknown values verbatim
        };
    }

    private static string BuildPostgresConnectionString(VaultServer s)
    {
        var parts = new List<string>
        {
            $"Host={s.Host}",
            $"Port={s.Port}",
        };
        if (!string.IsNullOrWhiteSpace(s.Database)) parts.Add($"Database={s.Database}");
        if (!string.IsNullOrWhiteSpace(s.Username)) parts.Add($"Username={s.Username}");
        if (!string.IsNullOrWhiteSpace(s.Password)) parts.Add($"Password={s.Password}");
        parts.Add($"SSL Mode={s.SslMode ?? "Prefer"}");
        return string.Join(';', parts);
    }

    private static string BuildMySqlConnectionString(VaultServer s)
    {
        var parts = new List<string>
        {
            $"Server={s.Host}",
            $"Port={s.Port}",
        };
        if (!string.IsNullOrWhiteSpace(s.Database)) parts.Add($"Database={s.Database}");
        if (!string.IsNullOrWhiteSpace(s.Username)) parts.Add($"Uid={s.Username}");
        if (!string.IsNullOrWhiteSpace(s.Password)) parts.Add($"Pwd={s.Password}");
        parts.Add($"SslMode={s.SslMode ?? "Preferred"}");
        return string.Join(';', parts);
    }
}
