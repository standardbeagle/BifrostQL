using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Fail-fast validation coverage for the field-encryption metadata contract
/// (Crypto slice 1). These are security keys — a typo'd algorithm or an
/// unparseable key-ref must be rejected at model load, never silently leave a
/// column unencrypted or throw on the first write.
/// </summary>
public class CryptoMetadataValidationTests
{
    private static DbModelTestFixture EncryptedColumn(string key, string value, params (string k, string v)[] extra)
    {
        return DbModelTestFixture.Create()
            .WithTable("customers", t =>
            {
                t.WithSchema("dbo").WithPrimaryKey("Id").WithColumn("ssn", "nvarchar").WithColumn("ssn_bidx", "nvarchar");
                t.WithColumnMetadata("ssn", key, value);
                foreach (var (k, v) in extra)
                    t.WithColumnMetadata("ssn", k, v);
            });
    }

    [Fact]
    public void Validate_WellFormedEncryptConfig_DoesNotThrow()
    {
        var model = EncryptedColumn(MetadataKeys.Crypto.Encrypt, "aes-256-gcm",
            (MetadataKeys.Crypto.KeyRef, "kms:pii"),
            (MetadataKeys.Crypto.Mask, "last4"),
            (MetadataKeys.Crypto.UnmaskRole, "compliance"),
            (MetadataKeys.Crypto.BlindIndex, "ssn_bidx")).Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_UnsupportedAlgorithm_Throws()
    {
        var model = EncryptedColumn(MetadataKeys.Crypto.Encrypt, "des",
            (MetadataKeys.Crypto.KeyRef, "kms:pii")).Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("des").And.Contain(MetadataKeys.Crypto.Encrypt);
    }

    [Fact]
    public void Validate_MissingKeyRef_Throws()
    {
        var model = EncryptedColumn(MetadataKeys.Crypto.Encrypt, "aes-256-gcm").Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Crypto.KeyRef);
    }

    [Fact]
    public void Validate_MalformedKeyRef_Throws()
    {
        var model = EncryptedColumn(MetadataKeys.Crypto.Encrypt, "aes-256-gcm",
            (MetadataKeys.Crypto.KeyRef, "vault:pii")).Build(); // unknown provider

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("vault:pii").And.Contain(MetadataKeys.Crypto.KeyRef);
    }

    [Fact]
    public void Validate_UnknownMaskMode_Throws()
    {
        var model = EncryptedColumn(MetadataKeys.Crypto.Encrypt, "aes-256-gcm",
            (MetadataKeys.Crypto.KeyRef, "kms:pii"),
            (MetadataKeys.Crypto.Mask, "stars")).Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("stars").And.Contain(MetadataKeys.Crypto.Mask);
    }

    [Fact]
    public void Validate_BlindIndexNamesMissingColumn_Throws()
    {
        var model = EncryptedColumn(MetadataKeys.Crypto.Encrypt, "aes-256-gcm",
            (MetadataKeys.Crypto.KeyRef, "kms:pii"),
            (MetadataKeys.Crypto.BlindIndex, "nonexistent")).Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("nonexistent").And.Contain(MetadataKeys.Crypto.BlindIndex);
    }

    [Fact]
    public void Validate_CryptoMetadataWithoutEncrypt_Throws()
    {
        // key-ref/mask/etc without encrypt is a misconfiguration that does nothing.
        var model = EncryptedColumn(MetadataKeys.Crypto.KeyRef, "kms:pii").Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Crypto.Encrypt);
    }
}
