using System.Text.Json;
using BifrostQL.UI.Vault;
using Microsoft.Extensions.Logging;
using Photino.NET;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// Vault credential bridge handlers — Photino-only, in-process.
    ///
    /// <list type="bullet">
    ///   <item><description><c>save-vault-entry</c> — writes a passwordless vault
    ///     entry (metadata only) from the renderer payload.</description></item>
    ///   <item><description><c>request-credential</c> — opens an isolated Photino child
    ///     window (separate WebView2, own <see cref="NativeBridgeHost"/>, CSP-locked HTML)
    ///     to collect the password, writes the complete vault entry server-side, and
    ///     returns only {saved, name} — never the password/username — to the renderer.</description></item>
    /// </list>
    ///
    /// SECURITY: the password is ONLY collected by the child window and is never
    /// returned to the renderer. This handler writes the encrypted vault entry inside
    /// the process, then drops the credential references so the plaintext becomes
    /// unreachable.
    /// </summary>
    public sealed class VaultBridgeHandlers
    {
        private readonly PhotinoWindow _window;
        private readonly ILogger? _logger;
        private readonly string? _vaultPath;

        public VaultBridgeHandlers(PhotinoWindow window, ILogger? logger, string? vaultPath)
        {
            _window = window;
            _logger = logger;
            _vaultPath = vaultPath;
        }

        public void Register(NativeBridgeHost bridge)
        {
            bridge.Register("save-vault-entry", SaveVaultEntryAsync);
            bridge.Register("request-credential", RequestCredentialAsync);
        }

        private async Task<object?> SaveVaultEntryAsync(JsonElement payload, CancellationToken _)
        {
            var server = BuildVaultServerFromPayload(payload, null, null);
            await UpsertVaultServer(server);
            return new { saved = true, name = server.Name };
        }

        private async Task<object?> RequestCredentialAsync(JsonElement payload, CancellationToken innerCt)
        {
            var metadata = BuildVaultServerFromPayload(payload, null, null);

            // Collect the password via the isolated child window. This call blocks
            // until the user clicks Save, Cancel, or closes the window. PromptAsync
            // throws OperationCanceledException on innerCt cancellation, which
            // NativeBridgeHost catches and scrubs into a BridgeError containing
            // "cancel" — the TS wrapper turns that into CredentialCancelledError.
            var result = await CredentialPromptWindow
                .PromptAsync(_window, metadata.Name, _logger, innerCt)
                .ConfigureAwait(false);

            if (!result.IsSaved)
            {
                // User cancelled — surface as a bridge error so the wrapper can map it
                // to CredentialCancelledError. A message containing "cancel" is the
                // contract the wrapper matches.
                throw new OperationCanceledException("Credential prompt cancelled by user");
            }

            // Construct the vault entry. The username from the child window takes
            // precedence over the payload username — the child window is authoritative
            // on what the user typed. If it somehow returned an empty username, fall
            // back to the payload-supplied one so we still have a value.
            var server = BuildVaultServerFromPayload(payload, result.Username, result.Password);
            var savedName = server.Name;

            // Load + upsert + save, same as the CLI `vault add` path.
            await UpsertVaultServer(server);

            // Drop all references to the password ASAP. .NET can't guarantee the heap
            // string is collected immediately, but this severs the only paths from
            // which any subsequent code could reach the plaintext.
            server = null!;
            result = null!;

            return new { saved = true, name = savedName };
        }

        private async Task UpsertVaultServer(VaultServer server)
        {
            var vault = await VaultStore.Load(_vaultPath);
            var servers = vault.Servers
                .Where(s => !s.Name.Equals(server.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            servers.Add(server);
            vault = vault with { Servers = servers };
            await VaultStore.Save(vault, _vaultPath);
        }

        private static VaultServer BuildVaultServerFromPayload(
            JsonElement payload,
            string? usernameOverride,
            string? password)
        {
            if (payload.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("ConnectionInfo payload required");

            string? ReadString(string key) =>
                payload.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString()
                    : null;
            int? ReadInt(string key) =>
                payload.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)
                    ? i
                    : null;
            bool? ReadBool(string key)
            {
                if (!payload.TryGetProperty(key, out var p) || p.ValueKind == JsonValueKind.Null)
                    return null;
                // A present-but-non-boolean value (e.g. the string "true") must fail,
                // not be treated as absent: for `ssl`, silently reading it as null
                // downgrades the persisted SSL mode to an opportunistic/plaintext-
                // fallback setting the user did not intend.
                return p.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new ArgumentException($"'{key}' must be a boolean, got {p.ValueKind}."),
                };
            }
            List<string> ReadStringArray(string key)
            {
                if (!payload.TryGetProperty(key, out var p) || p.ValueKind != JsonValueKind.Array)
                    return [];

                var values = new List<string>();
                foreach (var item in p.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var value = item.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            values.Add(value);
                    }
                }
                return values;
            }

            var vaultName = ReadString("vaultName");
            if (string.IsNullOrWhiteSpace(vaultName))
                throw new ArgumentException("vaultName required");

            var provider = ReadString("provider")?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(provider))
                throw new ArgumentException("provider required");

            var host = ReadString("host") ?? "";
            var port = ReadInt("port") ?? provider switch
            {
                "postgres" => 5432,
                "mysql" => 3306,
                "sqlserver" => 1433,
                _ => 0,
            };
            var database = ReadString("database");
            var username = string.IsNullOrEmpty(usernameOverride)
                ? ReadString("username")
                : usernameOverride;
            var ssl = ReadBool("ssl");
            var sslMode = ssl == true ? "Require" : null;
            var tags = ReadStringArray("tags");

            VaultSshConfig? ssh = null;
            if (payload.TryGetProperty("ssh", out var sshPayload) && sshPayload.ValueKind == JsonValueKind.Object)
            {
                string? ReadSshString(string key) =>
                    sshPayload.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
                        ? p.GetString()
                        : null;
                int? ReadSshInt(string key) =>
                    sshPayload.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)
                        ? i
                        : null;

                var sshHost = ReadSshString("host");
                var sshUsername = ReadSshString("username");
                var hasHost = !string.IsNullOrWhiteSpace(sshHost);
                var hasUsername = !string.IsNullOrWhiteSpace(sshUsername);
                // An SSH object was supplied — a partial one (host without username or
                // vice versa) must fail rather than being silently discarded. Dropping
                // it would save the server with no tunnel and later connect directly to
                // the database host, defeating the tunnel the user configured.
                if (hasHost != hasUsername)
                    throw new ArgumentException("SSH config requires both 'host' and 'username'.");
                if (hasHost && hasUsername)
                {
                    ssh = new VaultSshConfig(
                        sshHost!,
                        ReadSshInt("port") ?? 22,
                        sshUsername!,
                        ReadSshString("identityFile"));
                }
            }

            return new VaultServer(
                Name: vaultName!,
                Provider: provider!,
                Host: host,
                Port: port,
                Database: database,
                Username: username,
                Password: password,
                SslMode: sslMode,
                Ssh: ssh,
                Tags: tags);
        }
    }
}
