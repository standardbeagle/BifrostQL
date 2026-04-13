using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using BifrostQL.Core.Model;

namespace BifrostQL.UI.Vault;

/// <summary>
/// CLI subcommands for managing the encrypted credential vault.
/// Usage: bifrostui vault add|list|remove|export
/// </summary>
public static class VaultCommands
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Command CreateVaultCommand(Option<string?> vaultPathOption)
    {
        var vaultCommand = new Command("vault", "Manage saved database server credentials");

        vaultCommand.Add(CreateAddCommand(vaultPathOption));
        vaultCommand.Add(CreateListCommand(vaultPathOption));
        vaultCommand.Add(CreateRemoveCommand(vaultPathOption));
        vaultCommand.Add(CreateExportCommand(vaultPathOption));

        return vaultCommand;
    }

    private static Command CreateAddCommand(Option<string?> vaultPathOption)
    {
        var nameArg = new Argument<string>("name") { Description = "Server name (used to identify this connection)" };
        var providerOpt = new Option<string>("--provider") { Description = "Database provider: postgres, mysql, sqlserver, sqlite", Required = true };
        var hostOpt = new Option<string>("--host") { Description = "Database host", Required = true };
        var portOpt = new Option<int?>("--port") { Description = "Database port (default: provider-specific)" };
        var databaseOpt = new Option<string?>("--database") { Description = "Database name" };
        var usernameOpt = new Option<string?>("--username") { Description = "Database username" };
        var passwordOpt = new Option<string?>("--password") { Description = "Database password (omit to prompt interactively)" };
        var sslModeOpt = new Option<string?>("--ssl-mode") { Description = "SSL mode" };
        var sshHostOpt = new Option<string?>("--ssh-host") { Description = "SSH tunnel host" };
        var sshPortOpt = new Option<int>("--ssh-port") { Description = "SSH tunnel port", DefaultValueFactory = _ => 22 };
        var sshUserOpt = new Option<string?>("--ssh-user") { Description = "SSH tunnel username" };
        var sshIdentityOpt = new Option<string?>("--ssh-identity") { Description = "SSH identity file path" };
        var tagOpt = new Option<string[]>("--tag") { Description = "Tags for this server (can be repeated; the 'wordpress' tag enables WP-CLI credential auto-discovery for SSH-tunneled entries with no username)", AllowMultipleArgumentsPerToken = true };

        var cmd = new Command("add", "Add a server to the vault")
        {
            nameArg, providerOpt, hostOpt, portOpt, databaseOpt, usernameOpt, passwordOpt,
            sslModeOpt, sshHostOpt, sshPortOpt, sshUserOpt, sshIdentityOpt, tagOpt
        };

        cmd.SetAction((parseResult, _) =>
        {
            var vaultPath = parseResult.GetValue(vaultPathOption);
            var name = parseResult.GetValue(nameArg)!;
            var provider = parseResult.GetValue(providerOpt)!.ToLowerInvariant();
            var host = parseResult.GetValue(hostOpt)!;
            var port = parseResult.GetValue(portOpt) ?? GetDefaultPort(provider);
            var database = parseResult.GetValue(databaseOpt);
            var username = parseResult.GetValue(usernameOpt);
            var password = parseResult.GetValue(passwordOpt);
            var sslMode = parseResult.GetValue(sslModeOpt);
            var sshHost = parseResult.GetValue(sshHostOpt);
            var sshPort = parseResult.GetValue(sshPortOpt);
            var sshUser = parseResult.GetValue(sshUserOpt);
            var sshIdentity = parseResult.GetValue(sshIdentityOpt);
            var tags = parseResult.GetValue(tagOpt) ?? [];

            // Interactive password prompt if not provided
            if (password is null && username is not null)
            {
                password = ReadPassword("Password: ");
            }

            VaultSshConfig? ssh = null;
            if (!string.IsNullOrWhiteSpace(sshHost))
            {
                ssh = new VaultSshConfig(sshHost, sshPort, sshUser ?? Environment.UserName, sshIdentity);
            }

            var server = new VaultServer(name, provider, host, port, database, username, password, sslMode, ssh, [.. tags]);

            var vault = VaultStore.Load(vaultPath);
            // Replace existing server with same name
            var servers = vault.Servers.Where(s => !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            servers.Add(server);
            vault = vault with { Servers = servers };
            VaultStore.Save(vault, vaultPath);

            Console.WriteLine($"Added '{name}' ({provider} @ {host}:{port})");
            return Task.CompletedTask;
        });

        return cmd;
    }

    private static Command CreateListCommand(Option<string?> vaultPathOption)
    {
        var tagOpt = new Option<string?>("--tag") { Description = "Filter by tag" };
        var jsonOpt = new Option<bool>("--json") { Description = "Output as JSON" };

        var cmd = new Command("list", "List saved servers") { tagOpt, jsonOpt };

        cmd.SetAction((parseResult, _) =>
        {
            var vaultPath = parseResult.GetValue(vaultPathOption);
            var tagFilter = parseResult.GetValue(tagOpt);
            var asJson = parseResult.GetValue(jsonOpt);

            var servers = VaultServerProvider.LoadServers(vaultPath);

            if (tagFilter is not null)
                servers = servers.Where(s => s.Server.Tags.Contains(tagFilter, StringComparer.OrdinalIgnoreCase)).ToList();

            if (asJson)
            {
                var jsonServers = servers.Select(s => s.Server).ToList();
                Console.WriteLine(JsonSerializer.Serialize(jsonServers, PrettyJson));
                return Task.CompletedTask;
            }

            if (servers.Count == 0)
            {
                Console.WriteLine("No saved servers.");
                return Task.CompletedTask;
            }

            // Table output
            var nameWidth = Math.Max(4, servers.Max(s => s.Server.Name.Length));
            var provWidth = Math.Max(8, servers.Max(s => s.Server.Provider.Length));
            var addrWidth = Math.Max(7, servers.Max(s => FormatAddress(s.Server).Length));

            Console.WriteLine($"{"Name".PadRight(nameWidth)}  {"Provider".PadRight(provWidth)}  {"Address".PadRight(addrWidth)}  {"Source",-6}  Tags");
            Console.WriteLine(new string('-', nameWidth + provWidth + addrWidth + 20));

            foreach (var (server, source) in servers)
            {
                var addr = FormatAddress(server);
                var tags = server.Tags.Count > 0 ? string.Join(", ", server.Tags) : "";
                Console.WriteLine($"{server.Name.PadRight(nameWidth)}  {server.Provider.PadRight(provWidth)}  {addr.PadRight(addrWidth)}  {source,-6}  {tags}");
            }

            return Task.CompletedTask;
        });

        return cmd;
    }

    private static Command CreateRemoveCommand(Option<string?> vaultPathOption)
    {
        var nameArg = new Argument<string>("name") { Description = "Server name to remove" };
        var cmd = new Command("remove", "Remove a server from the vault") { nameArg };

        cmd.SetAction((parseResult, _) =>
        {
            var vaultPath = parseResult.GetValue(vaultPathOption);
            var name = parseResult.GetValue(nameArg)!;

            var vault = VaultStore.Load(vaultPath);
            var before = vault.Servers.Count;
            var servers = vault.Servers.Where(s => !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();

            if (servers.Count == before)
            {
                Console.Error.WriteLine($"Server '{name}' not found in vault.");
                return Task.CompletedTask;
            }

            vault = vault with { Servers = servers };
            VaultStore.Save(vault, vaultPath);
            Console.WriteLine($"Removed '{name}'.");
            return Task.CompletedTask;
        });

        return cmd;
    }

    private static Command CreateExportCommand(Option<string?> vaultPathOption)
    {
        var nameArg = new Argument<string?>("name") { Description = "Export specific server (default: all)", Arity = ArgumentArity.ZeroOrOne };
        var formatOpt = new Option<string>("--format") { Description = "Output format: json or env", DefaultValueFactory = _ => "json" };

        var cmd = new Command("export", "Export servers for deployment") { nameArg, formatOpt };

        cmd.SetAction((parseResult, _) =>
        {
            var vaultPath = parseResult.GetValue(vaultPathOption);
            var name = parseResult.GetValue(nameArg);
            var format = parseResult.GetValue(formatOpt)!.ToLowerInvariant();

            var allServers = VaultServerProvider.LoadServers(vaultPath);
            var servers = name is not null
                ? allServers.Where(s => s.Server.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList()
                : allServers;

            if (servers.Count == 0)
            {
                Console.Error.WriteLine(name is not null ? $"Server '{name}' not found." : "No servers to export.");
                return Task.CompletedTask;
            }

            if (format == "env")
            {
                foreach (var (server, _) in servers)
                {
                    var envName = server.Name.ToUpperInvariant().Replace('-', '_').Replace(' ', '_');
                    var connStr = VaultServerProvider.BuildConnectionString(server);
                    Console.WriteLine($"export BIFROST_SERVER_{envName}='{connStr}'");
                }
            }
            else
            {
                var jsonServers = servers.Select(s => s.Server).ToList();
                var json = JsonSerializer.Serialize(jsonServers, PrettyJson);
                Console.WriteLine($"export BIFROST_SERVERS='{json}'");
            }

            return Task.CompletedTask;
        });

        return cmd;
    }

    private static string FormatAddress(VaultServer server)
    {
        var addr = $"{server.Host}:{server.Port}";
        if (!string.IsNullOrWhiteSpace(server.Database))
            addr += $"/{server.Database}";
        return addr;
    }

    private static int GetDefaultPort(string provider) => provider switch
    {
        "postgres" => 5432,
        "mysql" => 3306,
        "sqlserver" => 1433,
        _ => 0,
    };

    /// <summary>
    /// Read password from console with masked input.
    /// Falls back to ReadLine when stdin is redirected (piped).
    /// </summary>
    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);

        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";

        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return password.ToString();
            }
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password.Length--;
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write('*');
            }
        }
    }
}
