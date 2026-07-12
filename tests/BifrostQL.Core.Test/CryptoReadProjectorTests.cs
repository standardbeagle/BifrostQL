using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Crypto;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Unit coverage for <see cref="CryptoReadProjector"/> decisions that the SQLite
/// end-to-end read tests do not exercise directly: redact/email masking, the
/// fail-closed redaction when no key manager is available, and passthrough of
/// non-encrypted columns.
/// </summary>
public class CryptoReadProjectorTests
{
    private const string Redacted = "••••••";
    private const string KeyRef = "config:pii";

    private static byte[] RootKey()
    {
        var k = new byte[FieldCipher.KeySize];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 5);
        return k;
    }

    // Builds a model with a single "secrets" table whose "ssn" column carries the
    // supplied crypto metadata, plus a plain "note" column. Returns the model and a
    // manager, and the ciphertext of `plaintext` for ssn (encrypted with that manager).
    private static (IDbModel model, EnvelopeKeyManager manager, string cipher) Build(
        string mask, string? unmaskRole, string plaintext)
    {
        var ssn = new ColumnDto
        {
            TableSchema = "dbo", TableName = "secrets", ColumnName = "ssn", GraphQlName = "ssn",
            NormalizedName = "ssn", DataType = "nvarchar",
            Metadata = new Dictionary<string, object?>
            {
                [MetadataKeys.Crypto.Encrypt] = "aes-256-gcm",
                [MetadataKeys.Crypto.KeyRef] = KeyRef,
                [MetadataKeys.Crypto.Mask] = mask,
            },
        };
        if (unmaskRole != null) ssn.Metadata[MetadataKeys.Crypto.UnmaskRole] = unmaskRole;
        var note = new ColumnDto
        {
            TableSchema = "dbo", TableName = "secrets", ColumnName = "note", GraphQlName = "note",
            NormalizedName = "note", DataType = "nvarchar", Metadata = new Dictionary<string, object?>(),
        };

        var table = Substitute.For<IDbTable>();
        table.TableSchema.Returns("dbo");
        table.DbName.Returns("secrets");
        var lookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase) { ["ssn"] = ssn, ["note"] = note };
        table.ColumnLookup.Returns(lookup);
        table.GraphQlLookup.Returns(lookup);

        var model = Substitute.For<IDbModel>();
        model.GetTableFromDbName("secrets").Returns(table);

        var manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(RootKey()), new InMemoryDataEncryptionKeyStore());
        var cipher = FieldCipher.Encrypt(manager.GetDataKey(KeyRef), plaintext, CryptoAad.Build("dbo", "secrets", "ssn"));
        return (model, manager, cipher);
    }

    [Fact]
    public void UnmaskRole_Decrypts()
    {
        var (model, manager, cipher) = Build(MetadataKeys.Crypto.MaskRedact, "compliance", "secret-value");
        var projector = new CryptoReadProjector(model, manager, new[] { "compliance" });
        projector.Project("secrets", "ssn", cipher).Should().Be("secret-value");
    }

    [Fact]
    public void NoUnmaskRole_RedactMode_ReturnsRedacted_WithoutNeedingManager()
    {
        var (model, _, cipher) = Build(MetadataKeys.Crypto.MaskRedact, "compliance", "secret");
        // No manager at all — redact must still work (it needs no plaintext).
        var projector = new CryptoReadProjector(model, keyManager: null, new[] { "viewer" });
        projector.Project("secrets", "ssn", cipher).Should().Be(Redacted);
    }

    [Fact]
    public void NoUnmaskRole_Last4_ButNoManager_FallsBackToRedacted()
    {
        // last4 needs the plaintext; without a manager it cannot decrypt, so it must
        // redact rather than leak the ciphertext.
        var (model, _, cipher) = Build(MetadataKeys.Crypto.MaskLast4, "compliance", "123456789");
        var projector = new CryptoReadProjector(model, keyManager: null, new[] { "viewer" });
        projector.Project("secrets", "ssn", cipher).Should().Be(Redacted);
    }

    [Fact]
    public void NoUnmaskRole_EmailMask_ShowsDomainOnly()
    {
        var (model, manager, cipher) = Build(MetadataKeys.Crypto.MaskEmail, "compliance", "alice@example.com");
        var projector = new CryptoReadProjector(model, manager, new[] { "viewer" });
        var masked = (string)projector.Project("secrets", "ssn", cipher)!;
        masked.Should().StartWith("a").And.EndWith("@example.com").And.NotContain("lice");
    }

    [Theory]
    [InlineData("1234")]   // exactly 4 — last4 would reveal the whole value
    [InlineData("12")]     // shorter than 4
    [InlineData("9")]      // single char
    public void NoUnmaskRole_Last4_ShortValue_RedactsInsteadOfLeaking(string plaintext)
    {
        // A value of 4 chars or fewer must never be shown in full by last4 masking
        // (e.g. a 4-digit PIN) — it redacts entirely instead.
        var (model, manager, cipher) = Build(MetadataKeys.Crypto.MaskLast4, "compliance", plaintext);
        var projector = new CryptoReadProjector(model, manager, new[] { "viewer" });
        var masked = (string)projector.Project("secrets", "ssn", cipher)!;
        masked.Should().Be(Redacted).And.NotContain(plaintext);
    }

    [Fact]
    public void NoUnmaskRole_Last4_LongValue_RevealsOnlyLastFour()
    {
        var (model, manager, cipher) = Build(MetadataKeys.Crypto.MaskLast4, "compliance", "123456789");
        var projector = new CryptoReadProjector(model, manager, new[] { "viewer" });
        var masked = (string)projector.Project("secrets", "ssn", cipher)!;
        masked.Should().EndWith("6789").And.NotContain("12345");
    }

    [Fact]
    public void NonEncryptedColumn_PassesThrough()
    {
        var (model, manager, _) = Build(MetadataKeys.Crypto.MaskRedact, "compliance", "x");
        var projector = new CryptoReadProjector(model, manager, new[] { "viewer" });
        projector.Project("secrets", "note", "plain text").Should().Be("plain text");
    }

    [Fact]
    public void NullValue_StaysNull()
    {
        var (model, manager, _) = Build(MetadataKeys.Crypto.MaskLast4, "compliance", "x");
        var projector = new CryptoReadProjector(model, manager, new[] { "viewer" });
        projector.Project("secrets", "ssn", null).Should().BeNull();
    }

    [Fact]
    public void TamperedCiphertext_ForUnmaskRole_RedactsInsteadOfLeaking()
    {
        var (model, manager, cipher) = Build(MetadataKeys.Crypto.MaskRedact, "compliance", "secret");
        var bytes = Convert.FromBase64String(cipher);
        bytes[^1] ^= 0xFF;
        var tampered = Convert.ToBase64String(bytes);
        var projector = new CryptoReadProjector(model, manager, new[] { "compliance" });
        projector.Project("secrets", "ssn", tampered).Should().Be(Redacted);
    }
}
