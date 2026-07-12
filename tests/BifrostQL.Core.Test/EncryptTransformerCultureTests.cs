using System.Globalization;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Crypto;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Regression: the encrypt-on-write transformer must serialize values with the
/// invariant culture, so a decimal column encrypts to the same plaintext (and the
/// same deterministic blind-index hash) regardless of the host's culture. A
/// culture-dependent ToString would make equality search fail across hosts.
/// </summary>
public class EncryptTransformerCultureTests
{
    private static (IDbTable table, EnvelopeKeyManager manager) BuildTable()
    {
        var balance = new ColumnDto
        {
            TableSchema = "dbo",
            TableName = "accounts",
            ColumnName = "balance",
            GraphQlName = "balance",
            NormalizedName = "balance",
            DataType = "decimal",
            Metadata = new Dictionary<string, object?>
            {
                [MetadataKeys.Crypto.Encrypt] = "aes-256-gcm",
                [MetadataKeys.Crypto.KeyRef] = "config:pii",
            },
        };
        var bidx = new ColumnDto
        {
            TableSchema = "dbo", TableName = "accounts", ColumnName = "balance_bidx",
            GraphQlName = "balance_bidx", NormalizedName = "balance_bidx", DataType = "nvarchar",
            Metadata = new Dictionary<string, object?>(),
        };

        var table = Substitute.For<IDbTable>();
        table.TableSchema.Returns("dbo");
        table.DbName.Returns("accounts");
        var lookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["balance"] = balance,
            ["balance_bidx"] = bidx,
        };
        table.Columns.Returns(new[] { balance, bidx });
        table.ColumnLookup.Returns(lookup);
        table.GraphQlLookup.Returns(lookup);

        var manager = new EnvelopeKeyManager(
            new ConfigRootKeyProvider(RootKey()), new InMemoryDataEncryptionKeyStore());
        return (table, manager);
    }

    private static byte[] RootKey()
    {
        var k = new byte[FieldCipher.KeySize];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 7);
        return k;
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")] // comma decimal separator — would corrupt a culture-sensitive ToString
    public async Task Encrypt_DecimalValue_IsCultureInvariant(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            var (table, manager) = BuildTable();
            var services = new ServiceCollection().AddSingleton(manager).BuildServiceProvider();
            var transformer = new EncryptOnWriteMutationTransformer();
            var ctx = new MutationTransformContext
            {
                Model = Substitute.For<IDbModel>(),
                UserContext = new Dictionary<string, object?>(),
                Services = services,
            };

            var data = new Dictionary<string, object?> { ["balance"] = 1234.5m };
            var result = await transformer.TransformAsync(table, MutationType.Insert, data, ctx);

            result.Errors.Should().BeEmpty();
            var dek = manager.GetDataKey("config:pii");
            var aad = CryptoAad.Build("dbo", "accounts", "balance");
            // Decrypts to the invariant-formatted decimal regardless of the host culture.
            FieldCipher.Decrypt(dek, (string)result.Data["balance"]!, aad).Should().Be("1234.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
