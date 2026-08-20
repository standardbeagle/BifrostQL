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
        var aad = CryptoAad.Build("dbo", "customers", "ssn");

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
        // A ciphertext copy-pasted into another column (different AAD) will not decrypt.
        var dek = Key(1);
        var envelope = FieldCipher.Encrypt(dek, "secret", CryptoAad.Build("dbo", "customers", "ssn"));
        var act = () => FieldCipher.Decrypt(dek, envelope, CryptoAad.Build("dbo", "customers", "dob"));
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
        var binding = CryptoAad.Build("main", "people", "ssn");
        var dek = manager.GetDataKey("config:pii");
        var idx = manager.GetBlindIndexKey("config:pii", binding);

        idx.Should().HaveCount(32);
        idx.Should().NotEqual(dek, "the blind-index key is HKDF-derived, not the DEK itself");
        manager.GetBlindIndexKey("config:pii", binding).Should().Equal(idx, "derivation is deterministic");
    }

    [Fact]
    public void EnvelopeKeyManager_BlindIndexKey_DiffersPerColumn_UnderTheSameKeyRef()
    {
        // Two columns SHARING a key-ref must derive DISTINCT blind-index keys, or equal plaintext
        // in different columns hashes identically — a cross-column equality oracle (correlate, or
        // test a hidden column against a known value, from the visible index alone). The column
        // binding (schema, table, column) is folded into the derivation to break the collision.
        var manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(Key(9)), new InMemoryDataEncryptionKeyStore());

        var ssn = manager.GetBlindIndexKey("config:pii", CryptoAad.Build("main", "people", "ssn"));
        var dob = manager.GetBlindIndexKey("config:pii", CryptoAad.Build("main", "people", "dob"));
        var ssnOtherTable = manager.GetBlindIndexKey("config:pii", CryptoAad.Build("main", "staff", "ssn"));

        ssn.Should().NotEqual(dob, "a different column under the same key-ref gets a different index key");
        ssn.Should().NotEqual(ssnOtherTable, "the same column name in another table is a distinct binding");
    }

    [Fact]
    public void ConfigRootKeyProvider_RejectsWrongSizedKey()
    {
        var act = () => new ConfigRootKeyProvider(new byte[16]);
        act.Should().Throw<ArgumentException>();
    }

    // ---- Key rotation: version-in-ciphertext + version-directed decrypt ----

    [Fact]
    public void FieldCipher_Versioned_CarriesKeyVersion_AndRoundTrips()
    {
        var dek = Key(1);
        var aad = CryptoAad.Build("dbo", "customers", "ssn");

        var envelope = FieldCipher.Encrypt(dek, "123-45-6789", aad, keyVersion: 7);

        FieldCipher.PeekKeyVersion(envelope).Should().Be(7, "the key version travels with the ciphertext");
        FieldCipher.Decrypt(dek, envelope, aad).Should().Be("123-45-6789");
    }

    [Fact]
    public void FieldCipher_LegacyEnvelope_HasNoKeyVersion_AndStillDecrypts()
    {
        // A value written by the pre-rotation (legacy-format) code path must still decrypt.
        var dek = Key(1);
        var aad = CryptoAad.Build("dbo", "customers", "ssn");

        var legacy = FieldCipher.Encrypt(dek, "legacy-secret", aad); // 3-arg = legacy format

        FieldCipher.PeekKeyVersion(legacy).Should().BeNull("legacy envelopes carry no embedded key version");
        FieldCipher.Decrypt(dek, legacy, aad).Should().Be("legacy-secret");
    }

    [Fact]
    public void FieldCipher_Versioned_RejectsZeroKeyVersion()
    {
        var act = () => FieldCipher.Encrypt(Key(1), "x", Aad("a"), keyVersion: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EnvelopeKeyManager_FreshKeyRef_IsVersionOne()
    {
        var manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(Key(9)), new InMemoryDataEncryptionKeyStore());
        manager.GetCurrentVersion("config:pii").Should().Be(1, "a never-rotated key-ref is version 1");
    }

    [Fact]
    public void EnvelopeKeyManager_Rotate_MintsHigherVersion_KeepingOldResolvable()
    {
        var manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(Key(9)), new InMemoryDataEncryptionKeyStore());
        var v1Dek = manager.GetDataKey("config:pii", 1);

        var newVersion = manager.Rotate("config:pii");

        newVersion.Should().Be(2);
        manager.GetCurrentVersion("config:pii").Should().Be(2, "rotation makes the new version current for writes");
        manager.GetDataKey("config:pii", 1).Should().Equal(v1Dek, "the old DEK stays durably resolvable for reads");
        manager.GetDataKey("config:pii", 2).Should().NotEqual(v1Dek, "the new version is a fresh, distinct DEK");
    }

    [Fact]
    public void EnvelopeKeyManager_Version1_EqualsUnversionedSlot_ForBackwardCompat()
    {
        // Legacy stored ciphertext resolves via the unversioned slot; version 1 must be that
        // same DEK so pre-rotation data still decrypts.
        var manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(Key(9)), new InMemoryDataEncryptionKeyStore());
        manager.GetDataKey("config:pii", 1).Should().Equal(manager.GetDataKey("config:pii"));
    }

    [Fact]
    public void EnvelopeKeyManager_ValueWrittenUnderOldVersion_DecryptsAfterRotation()
    {
        var manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(Key(9)), new InMemoryDataEncryptionKeyStore());
        var aad = CryptoAad.Build("dbo", "customers", "ssn");

        // Write under the current (v1) DEK, stamping v1 into the envelope.
        var v1 = manager.GetCurrentVersion("config:pii");
        var envelope = FieldCipher.Encrypt(manager.GetDataKey("config:pii", v1), "old-value", aad, v1);

        // Rotate: current becomes v2, but the envelope still names v1.
        manager.Rotate("config:pii").Should().Be(2);

        // Version-directed read: resolve the version named in the envelope, not the current one.
        var version = FieldCipher.PeekKeyVersion(envelope)!.Value;
        FieldCipher.Decrypt(manager.GetDataKey("config:pii", version), envelope, aad)
            .Should().Be("old-value", "an old-DEK value stays decryptable after rotation");
    }

    [Fact]
    public void EnvelopeKeyManager_Rotation_PersistsAcrossManagerInstances_SameStore()
    {
        var store = new InMemoryDataEncryptionKeyStore();
        var root = Key(9);
        var m1 = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), store);
        m1.Rotate("config:pii"); // -> v2

        // A fresh manager over the same durable store must see the rotated version and resolve
        // the same v2 DEK — versions are durably resolvable across the sweep / restarts.
        var m2 = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), store);
        m2.GetCurrentVersion("config:pii").Should().Be(2);
        m2.GetDataKey("config:pii", 2).Should().Equal(m1.GetDataKey("config:pii", 2));
        m2.GetDataKey("config:pii", 1).Should().Equal(m1.GetDataKey("config:pii", 1));
    }

    [Fact]
    public void EnvelopeKeyManager_BlindIndexKey_IsStableAcrossRotation()
    {
        // Blind-index hashes must keep matching across rotation, so the index key must NOT
        // change when the DEK rotates (it is bound to version 1).
        var manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(Key(9)), new InMemoryDataEncryptionKeyStore());
        var binding = CryptoAad.Build("main", "people", "ssn");
        var before = manager.GetBlindIndexKey("config:pii", binding);
        manager.Rotate("config:pii");
        manager.GetBlindIndexKey("config:pii", binding).Should().Equal(before, "the blind-index key survives rotation");
    }

}
