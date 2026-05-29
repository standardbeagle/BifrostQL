using System.Security.Cryptography;
using BifrostQL.UI.Vault;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Unit tests for VaultStore — the AES-256-GCM encrypted credential store.
/// Covers round-trip fidelity, the GCM authentication/tamper guarantee, the
/// fresh-nonce-per-save security property (GCM nonce reuse is catastrophic),
/// corruption handling, and Unix permission hardening. Each test runs in an
/// isolated temp directory so the real ~/.config/bifrost vault is never touched.
/// </summary>
public sealed class VaultStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bifrost-vault-test-" + Guid.NewGuid().ToString("N"));

    private string VaultPath => Path.Combine(_dir, "vault.json.enc");
    private string KeyPath => Path.Combine(_dir, "master.key");

    public VaultStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static VaultData SampleVault() => new()
    {
        Servers =
        {
            new VaultServer("prod-pg", "postgres", "db.example.com", 5432,
                "appdb", "svc", "s3cr3t!;weird=chars", "Require",
                new VaultSshConfig("bastion", 22, "deploy", "~/.ssh/id_ed25519"),
                ["production", "wordpress"]),
            new VaultServer("local-sqlite", "sqlite", "/tmp/app.db", 0,
                null, null, null, null, null, []),
        },
    };

    [Fact]
    public void Save_ThenLoad_RoundTripsAllFields()
    {
        var original = SampleVault();

        VaultStore.Save(original, VaultPath);
        var loaded = VaultStore.Load(VaultPath);

        loaded.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyVault()
    {
        var loaded = VaultStore.Load(VaultPath);

        loaded.Servers.Should().BeEmpty();
        loaded.Version.Should().Be(1);
    }

    [Fact]
    public void EncryptedFile_DoesNotContainPlaintextSecret()
    {
        VaultStore.Save(SampleVault(), VaultPath);

        var bytes = File.ReadAllBytes(VaultPath);
        var asText = System.Text.Encoding.UTF8.GetString(bytes);
        asText.Should().NotContain("s3cr3t");
        asText.Should().NotContain("svc");
    }

    [Fact]
    public void Save_GeneratesFreshNoncePerSave()
    {
        // GCM security collapses if a (key, nonce) pair is ever reused. Two saves
        // of identical plaintext under the same key must produce different nonces
        // (first 12 bytes) and therefore different ciphertext.
        VaultStore.Save(SampleVault(), VaultPath);
        var first = File.ReadAllBytes(VaultPath);

        VaultStore.Save(SampleVault(), VaultPath);
        var second = File.ReadAllBytes(VaultPath);

        first.AsSpan(0, 12).ToArray().Should().NotEqual(second.AsSpan(0, 12).ToArray());
        first.Should().NotEqual(second);
    }

    [Fact]
    public void Save_ReusesExistingMasterKey()
    {
        VaultStore.Save(SampleVault(), VaultPath);
        var key1 = File.ReadAllBytes(KeyPath);

        VaultStore.Save(SampleVault(), VaultPath);
        var key2 = File.ReadAllBytes(KeyPath);

        key2.Should().Equal(key1);
    }

    [Fact]
    public void Load_TamperedCiphertext_ThrowsCryptographic()
    {
        VaultStore.Save(SampleVault(), VaultPath);
        var bytes = File.ReadAllBytes(VaultPath);
        // Flip a bit inside the ciphertext region (past the 12-byte nonce).
        bytes[20] ^= 0xFF;
        File.WriteAllBytes(VaultPath, bytes);

        var act = () => VaultStore.Load(VaultPath);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Load_TamperedTag_ThrowsCryptographic()
    {
        VaultStore.Save(SampleVault(), VaultPath);
        var bytes = File.ReadAllBytes(VaultPath);
        // Flip a bit in the trailing 16-byte GCM tag.
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(VaultPath, bytes);

        var act = () => VaultStore.Load(VaultPath);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Load_CorruptKeySize_Throws()
    {
        VaultStore.Save(SampleVault(), VaultPath);
        File.WriteAllBytes(KeyPath, new byte[10]); // not 32 bytes

        var act = () => VaultStore.Load(VaultPath);

        act.Should().Throw<InvalidOperationException>().WithMessage("*corrupt*");
    }

    [Fact]
    public void Load_TruncatedVaultFile_Throws()
    {
        VaultStore.EnsureMasterKey(KeyPath);
        File.WriteAllBytes(VaultPath, new byte[10]); // smaller than nonce+tag (28)

        var act = () => VaultStore.Load(VaultPath);

        act.Should().Throw<InvalidOperationException>().WithMessage("*too small*");
    }

    [Fact]
    public void Save_SetsOwnerOnlyPermissions_OnUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return; // chmod is a no-op on Windows

        VaultStore.Save(SampleVault(), VaultPath);

        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.GetUnixFileMode(VaultPath).Should().Be(expected, "vault file must be chmod 600");
        File.GetUnixFileMode(KeyPath).Should().Be(expected, "master key must be chmod 600");
    }

    [Fact]
    public void EnsureMasterKey_IsIdempotent()
    {
        VaultStore.EnsureMasterKey(KeyPath);
        var key1 = File.ReadAllBytes(KeyPath);

        VaultStore.EnsureMasterKey(KeyPath);
        var key2 = File.ReadAllBytes(KeyPath);

        key1.Should().HaveCount(32);
        key2.Should().Equal(key1);
    }
}
