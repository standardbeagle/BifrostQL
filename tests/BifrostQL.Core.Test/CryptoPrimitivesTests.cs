using System.Security.Cryptography;
using System.Text;
using BifrostQL.Core.Crypto;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Unit coverage for the Crypto slice-1 primitives: key-ref parsing, AES-256-GCM
/// field encryption with cell-binding AAD, the blind-index HMAC, and the envelope
/// key manager (root-key wrapping of DEKs). These are the foundation the encrypt-on
/// -write transformer (slice 2) builds on, so their security properties are pinned
/// here: tamper/AAD/wrong-key must fail, and equal plaintexts must not produce equal
/// ciphertext.
/// </summary>
public class CryptoPrimitivesTests
{
    private static byte[] Key(byte seed)
    {
        var k = new byte[FieldCipher.KeySize];
        Array.Fill(k, seed);
        return k;
    }

    private static byte[] Aad(string s) => Encoding.UTF8.GetBytes(s);

    [Theory]
    [InlineData("kms:pii", "kms", "pii")]
    [InlineData("config:customer-data", "config", "customer-data")]
    [InlineData("  kms : pii ", "kms", "pii")] // trimmed
    public void KeyRef_TryParse_Valid(string raw, string provider, string id)
    {
        KeyRef.TryParse(raw, out var kr).Should().BeTrue();
        kr.Provider.Should().Be(provider);
        kr.Id.Should().Be(id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pii")]              // no provider
    [InlineData("vault:pii")]        // unknown provider
    [InlineData("kms:")]             // empty id
    [InlineData(":pii")]             // empty provider
    public void KeyRef_TryParse_Invalid(string? raw)
        => KeyRef.TryParse(raw, out _).Should().BeFalse();

    [Fact]
    public void FieldCipher_RoundTrips_WithMatchingKeyAndAad()
    {
        var dek = Key(1);
        var aad = CryptoAad.Build("dbo", "customers", "ssn", "42");

        var envelope = FieldCipher.Encrypt(dek, "123-45-6789", aad);
        FieldCipher.Decrypt(dek, envelope, aad).Should().Be("123-45-6789");
    }

    [Fact]
    public void FieldCipher_EqualPlaintexts_ProduceDifferentCiphertext()
    {
        // Random nonce per call ⇒ non-deterministic ciphertext (no equality oracle).
        var dek = Key(1);
        var aad = Aad("a");
        FieldCipher.Encrypt(dek, "same", aad).Should().NotBe(FieldCipher.Encrypt(dek, "same", aad));
    }

    [Fact]
    public void FieldCipher_WrongKey_FailsAuthentication()
    {
        var aad = Aad("a");
        var envelope = FieldCipher.Encrypt(Key(1), "secret", aad);
        var act = () => FieldCipher.Decrypt(Key(2), envelope, aad);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void FieldCipher_WrongAad_FailsAuthentication()
    {
        // A ciphertext copy-pasted onto another cell (different AAD) will not decrypt.
        var dek = Key(1);
        var envelope = FieldCipher.Encrypt(dek, "secret", CryptoAad.Build("dbo", "customers", "ssn", "42"));
        var act = () => FieldCipher.Decrypt(dek, envelope, CryptoAad.Build("dbo", "customers", "ssn", "43"));
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void FieldCipher_TamperedCiphertext_FailsAuthentication()
    {
        var dek = Key(1);
        var aad = Aad("a");
        var envelope = FieldCipher.Encrypt(dek, "secret", aad);
        var bytes = Convert.FromBase64String(envelope);
        bytes[^1] ^= 0xFF; // flip a ciphertext byte
        var act = () => FieldCipher.Decrypt(dek, Convert.ToBase64String(bytes), aad);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void FieldCipher_RejectsWrongSizedKey()
    {
        var act = () => FieldCipher.Encrypt(new byte[16], "x", Aad("a"));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BlindIndex_IsDeterministic_AndKeySensitive()
    {
        var k1 = Key(1);
        var k2 = Key(2);
        var a = BlindIndexComputer.Compute(k1, "alice@example.com");
        var b = BlindIndexComputer.Compute(k1, "alice@example.com");
        var c = BlindIndexComputer.Compute(k2, "alice@example.com");

        a.Should().Be(b, "the same key + value always yields the same index (so equality search works)");
        a.Should().NotBe(c, "a different index key yields a different hash");
        a.Should().MatchRegex("^[0-9a-f]{64}$", "HMAC-SHA-256 hex");
    }

    [Fact]
    public void EnvelopeKeyManager_GeneratesWrapsAndUnwraps_StableDek()
    {
        var manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(Key(9)), new InMemoryDataEncryptionKeyStore());

        var first = manager.GetDataKey("config:pii");
        var second = manager.GetDataKey("config:pii");

        first.Should().HaveCount(FieldCipher.KeySize);
        second.Should().Equal(first, "the same key-ref resolves to the same DEK across calls");
    }

    [Fact]
    public void EnvelopeKeyManager_WrappedDek_UnwrapsAcrossManagerInstances_SameRoot()
    {
        var store = new InMemoryDataEncryptionKeyStore();
        var root = Key(9);
        var dek1 = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), store).GetDataKey("config:pii");
        // A fresh manager over the same store + root must unwrap the persisted DEK.
        var dek2 = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), store).GetDataKey("config:pii");
        dek2.Should().Equal(dek1);
    }

    [Fact]
    public void EnvelopeKeyManager_WrongRootKey_FailsToUnwrap()
    {
        var store = new InMemoryDataEncryptionKeyStore();
        new EnvelopeKeyManager(new ConfigRootKeyProvider(Key(9)), store).GetDataKey("config:pii");

        var act = () => new EnvelopeKeyManager(new ConfigRootKeyProvider(Key(8)), store).GetDataKey("config:pii");
        act.Should().Throw<CryptographicException>("a different root key cannot unwrap the DEK");
    }

    [Fact]
    public void EnvelopeKeyManager_DistinctKeyRefs_HaveDistinctDeks()
    {
        var manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(Key(9)), new InMemoryDataEncryptionKeyStore());
        manager.GetDataKey("config:pii").Should().NotEqual(manager.GetDataKey("config:hr"));
    }

    [Fact]
    public void EnvelopeKeyManager_BlindIndexKey_IsDerived_DistinctFromDek()
    {
        var manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(Key(9)), new InMemoryDataEncryptionKeyStore());
        var dek = manager.GetDataKey("config:pii");
        var idx = manager.GetBlindIndexKey("config:pii");

        idx.Should().HaveCount(32);
        idx.Should().NotEqual(dek, "the blind-index key is HKDF-derived, not the DEK itself");
        manager.GetBlindIndexKey("config:pii").Should().Equal(idx, "derivation is deterministic");
    }

    [Fact]
    public void ConfigRootKeyProvider_RejectsWrongSizedKey()
    {
        var act = () => new ConfigRootKeyProvider(new byte[16]);
        act.Should().Throw<ArgumentException>();
    }
}
