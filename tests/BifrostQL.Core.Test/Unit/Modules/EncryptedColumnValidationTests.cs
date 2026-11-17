using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Crypto;
using BifrostQL.Core.Modules.Validation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Modules;

/// <summary>
/// Declared validation rules describe the plaintext. Encryption (priority 40) runs before
/// server validation (199), so the rules of an encrypted column must run on the value the
/// caller sent, not on the ciphertext envelope the chain hands validation.
/// </summary>
public sealed class EncryptedColumnValidationTests
{
    [Fact]
    public async Task Pattern_OnEncryptedColumn_AcceptsMatchingPlaintext()
    {
        var result = await RunChainAsync(new() { ["ssn"] = "123-45-6789" });

        result.Errors.Should().BeEmpty("the plaintext matches the pattern; only the envelope does not");
        ((string)result.Data["ssn"]!).Should().NotBe("123-45-6789", "the stored value is still encrypted");
    }

    [Fact]
    public async Task Pattern_OnEncryptedColumn_RefusesMalformedPlaintext()
    {
        var result = await RunChainAsync(new() { ["ssn"] = "12-345-6789" });

        result.Errors.Should().ContainSingle().Which.Should().Be("ssn is invalid.");
    }

    [Fact]
    public async Task Range_OnEncryptedColumn_RefusesOutOfRangePlaintext()
    {
        var result = await RunChainAsync(new() { ["ssn"] = "123-45-6789", ["limit"] = 500m });

        result.Errors.Should().ContainSingle().Which.Should().Be("limit must be at most 100.");
    }

    [Fact]
    public async Task ServerValidationOff_OnTheEncryptedColumn_SkipsItsPlaintextRules()
    {
        var result = await RunChainAsync(new() { ["ssn"] = "123-45-6789", ["limit"] = 500m },
            limitValidation: "off");

        result.Errors.Should().BeEmpty();
    }

    private static async Task<MutationTransformResult> RunChainAsync(
        Dictionary<string, object?> data, string? limitValidation = null)
    {
        var table = BuildTable(limitValidation);
        var manager = new EnvelopeKeyManager(
            new ConfigRootKeyProvider(Enumerable.Range(1, FieldCipher.KeySize).Select(i => (byte)i).ToArray()),
            new InMemoryDataEncryptionKeyStore());
        var services = new ServiceCollection().AddSingleton(manager).BuildServiceProvider();
        var chain = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new ExtendedServerValidationTransformer(), new EncryptOnWriteMutationTransformer(),
            },
        };
        return await chain.TransformAsync(table, MutationType.Insert, data, new MutationTransformContext
        {
            Model = Substitute.For<IDbModel>(),
            UserContext = new Dictionary<string, object?>(),
            Services = services,
        });
    }

    private static IDbTable BuildTable(string? limitValidation)
    {
        var ssn = Column("ssn", "nvarchar", new()
        {
            [MetadataKeys.Crypto.Encrypt] = "aes-256-gcm",
            [MetadataKeys.Crypto.KeyRef] = "config:pii",
            [MetadataKeys.Validation.Pattern] = @"\d{3}-\d{2}-\d{4}",
        });
        var limitMetadata = new Dictionary<string, object?>
        {
            [MetadataKeys.Crypto.Encrypt] = "aes-256-gcm",
            [MetadataKeys.Crypto.KeyRef] = "config:pii",
            [MetadataKeys.Validation.Max] = "100",
        };
        if (limitValidation is not null)
            limitMetadata[MetadataKeys.Validation.Server] = limitValidation;
        var limit = Column("limit", "nvarchar", limitMetadata);

        var table = Substitute.For<IDbTable>();
        table.TableSchema.Returns("dbo");
        table.DbName.Returns("people");
        var lookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["ssn"] = ssn,
            ["limit"] = limit,
        };
        table.Columns.Returns(new[] { ssn, limit });
        table.ColumnLookup.Returns(lookup);
        table.GraphQlLookup.Returns(lookup);
        table.Metadata.Returns(new Dictionary<string, object?>());
        table.GetMetadataValue(Arg.Any<string>()).Returns((string?)null);
        return table;
    }

    private static ColumnDto Column(string name, string type, Dictionary<string, object?> metadata) => new()
    {
        TableSchema = "dbo",
        TableName = "people",
        ColumnName = name,
        GraphQlName = name,
        NormalizedName = name,
        DataType = type,
        IsNullable = true,
        Metadata = metadata,
    };
}
