using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BifrostQL.UI.Vault;

public record VaultData
{
    public int Version { get; init; } = 1;
    public List<VaultServer> Servers { get; init; } = [];
}

public record VaultServer(
    string Name,
    string Provider,
    string Host,
    int Port,
    string? Database,
    string? Username,
    string? Password,
    string? SslMode,
    VaultSshConfig? Ssh,
    List<string> Tags)
{
    // Parameterless for JSON deserialization
    public VaultServer() : this("", "", "", 0, null, null, null, null, null, []) { }
}

public record VaultSshConfig(
    string Host,
    int Port,
    string Username,
    string? IdentityFile,
    VaultWordPressDiscovery? WordPressDiscovery = null)
{
    public VaultSshConfig() : this("", 22, "", null, null) { }
}

/// <summary>
/// Opt-in WordPress credential auto-discovery via WP-CLI over SSH.
/// When set, vault connect runs `wp config get` on the SSH host to extract
/// DB_USER/DB_PASSWORD/DB_NAME instead of using the credentials in the vault
/// entry. Only triggered if the vault entry's Username is empty.
/// </summary>
public record VaultWordPressDiscovery(List<string>? Roots = null)
{
    /// <summary>Default WP installation paths to search when Roots is null.</summary>
    public static readonly IReadOnlyList<string> DefaultRoots =
        ["~/public_html", "/var/www/html", "~/www", "~/htdocs", "."];
}

/// <summary>
/// Encrypted credential vault using AES-256-GCM.
/// File format: [12-byte nonce][ciphertext][16-byte GCM tag]
/// Master key: 32 random bytes stored at ~/.config/bifrost/master.key (chmod 600)
/// </summary>
public static class VaultStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultConfigDir
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config", "bifrost");
        }
    }

    public static string DefaultVaultPath => Path.Combine(DefaultConfigDir, "vault.json.enc");
    public static string DefaultKeyPath => Path.Combine(DefaultConfigDir, "master.key");

    /// <summary>
    /// Load and decrypt the vault. Returns empty vault if file doesn't exist.
    /// </summary>
    public static VaultData Load(string? vaultPath = null)
    {
        var path = vaultPath ?? DefaultVaultPath;
        if (!File.Exists(path))
            return new VaultData();

        var keyPath = KeyPathFor(path);
        var key = File.ReadAllBytes(keyPath);
        if (key.Length != KeySize)
            throw new InvalidOperationException($"Master key at {keyPath} is corrupt (expected {KeySize} bytes, got {key.Length})");

        var data = File.ReadAllBytes(path);
        if (data.Length < NonceSize + TagSize)
            throw new InvalidOperationException($"Vault file at {path} is corrupt (too small)");

        var nonce = data.AsSpan(0, NonceSize);
        var ciphertext = data.AsSpan(NonceSize, data.Length - NonceSize - TagSize);
        var tag = data.AsSpan(data.Length - TagSize, TagSize);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return JsonSerializer.Deserialize<VaultData>(plaintext, JsonOptions)
            ?? new VaultData();
    }

    /// <summary>
    /// Encrypt and save the vault. Atomic write via temp file + rename.
    /// </summary>
    public static void Save(VaultData vault, string? vaultPath = null)
    {
        var path = vaultPath ?? DefaultVaultPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var keyPath = KeyPathFor(path);
        EnsureMasterKey(keyPath);
        var key = File.ReadAllBytes(keyPath);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(vault, JsonOptions);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Write [nonce][ciphertext][tag]
        var output = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(output, 0);
        ciphertext.CopyTo(output, NonceSize);
        tag.CopyTo(output, NonceSize + ciphertext.Length);

        // Atomic write: write to temp, then rename
        var tmpPath = path + ".tmp";
        File.WriteAllBytes(tmpPath, output);
        SetFilePermissions(tmpPath);
        File.Move(tmpPath, path, overwrite: true);
    }

    /// <summary>
    /// Generate master key if it doesn't exist. Sets chmod 600.
    /// </summary>
    public static void EnsureMasterKey(string? keyPath = null)
    {
        var path = keyPath ?? DefaultKeyPath;
        if (File.Exists(path)) return;

        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var key = new byte[KeySize];
        RandomNumberGenerator.Fill(key);

        File.WriteAllBytes(path, key);
        SetFilePermissions(path);
    }

    /// <summary>
    /// Derive key path from vault path (sibling file).
    /// </summary>
    private static string KeyPathFor(string vaultPath)
    {
        var dir = Path.GetDirectoryName(vaultPath)!;
        return Path.Combine(dir, "master.key");
    }

    /// <summary>
    /// Set file to owner-only read/write (chmod 600) on Unix systems.
    /// </summary>
    private static void SetFilePermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
