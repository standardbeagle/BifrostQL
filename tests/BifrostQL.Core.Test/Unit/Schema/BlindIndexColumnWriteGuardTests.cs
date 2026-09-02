using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Approval;
using BifrostQL.Core.Modules.Crypto;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// Blind-index columns are derived server-side from their encrypted source
/// (EncryptOnWriteMutationTransformer computes the search token); a
/// client-supplied value would desync the token from the ciphertext or plant a
/// forged one. Three layers hold: mutation input types omit the column, the
/// transformer rejects direct writes as a fail-closed backstop for programmatic
/// callers, and model validation refuses a NOT NULL blind-index column whose
/// encrypted source is nullable (the insert could never satisfy it).
/// </summary>
public class BlindIndexColumnWriteGuardTests
{
    private static IDbModel SecretsModel() => DbModelTestFixture.Create()
        .WithTable("secrets", t => t
            .WithPrimaryKey("id")
            .WithColumn("ssn", "nvarchar", isNullable: false)
            .WithColumn("ssn_bidx", "nvarchar", isNullable: true)
            .WithColumn("note")
            .WithColumnMetadata("ssn", MetadataKeys.Crypto.Encrypt, "aes-256-gcm")
            .WithColumnMetadata("ssn", MetadataKeys.Crypto.KeyRef, "config:pii")
            .WithColumnMetadata("ssn", MetadataKeys.Crypto.BlindIndex, "ssn_bidx"))
        .Build();

    [Theory]
    [InlineData(MutateActions.Insert)]
    [InlineData(MutateActions.Update)]
    public void MutationInput_OmitsBlindIndexColumn(MutateActions action)
    {
        var table = SecretsModel().Tables.Single(t => t.GraphQlName == "secrets");

        var sdl = new TableSchemaGenerator(table)
            .GetMutationParameterType(action, IdentityType.Required);

        sdl.Should().NotContain("ssn_bidx",
            "the blind-index column is server-derived and must not be client-writable");
        sdl.Should().Contain("ssn", "the encrypted source column itself stays writable");
    }

    [Fact]
    public void SetInput_OmitsBlindIndexColumn()
    {
        var table = SecretsModel().Tables.Single(t => t.GraphQlName == "secrets");

        var sdl = new TableSchemaGenerator(table).GetSetParameterType();

        sdl.Should().NotContain("ssn_bidx");
    }

    private static (EncryptOnWriteMutationTransformer transformer, MutationTransformContext ctx)
        BuildTransformer(Dictionary<string, object?>? userContext = null)
    {
        var rootKey = new byte[FieldCipher.KeySize];
        for (var i = 0; i < rootKey.Length; i++) rootKey[i] = (byte)(i + 7);
        var manager = new EnvelopeKeyManager(
            new ConfigRootKeyProvider(rootKey), new InMemoryDataEncryptionKeyStore());
        var services = new ServiceCollection().AddSingleton(manager).BuildServiceProvider();
        var ctx = new MutationTransformContext
        {
            Model = Substitute.For<IDbModel>(),
            UserContext = userContext ?? new Dictionary<string, object?>(),
            Services = services,
        };
        return (new EncryptOnWriteMutationTransformer(), ctx);
    }

    [Fact]
    public async Task Transformer_RejectsDirectBlindIndexWrite()
    {
        var table = SecretsModel().Tables.Single(t => t.GraphQlName == "secrets");
        var (transformer, ctx) = BuildTransformer();

        var data = new Dictionary<string, object?>
        {
            ["ssn"] = "123-45-6789",
            ["ssn_bidx"] = "forged-token",
        };
        var result = await transformer.TransformAsync(table, MutationType.Insert, data, ctx);

        result.Errors.Should().ContainSingle(e => e.Contains("ssn_bidx") && e.Contains("blind-index"));
    }

    [Fact]
    public async Task Transformer_RejectsBlindIndexWriteWithoutSourceColumn()
    {
        // The dangerous variant: no encrypted value in the write, so the token is
        // never recomputed — the client value would land raw and desync the index.
        var table = SecretsModel().Tables.Single(t => t.GraphQlName == "secrets");
        var (transformer, ctx) = BuildTransformer();

        var data = new Dictionary<string, object?> { ["ssn_bidx"] = "forged-token" };
        var result = await transformer.TransformAsync(table, MutationType.Update, data, ctx);

        result.Errors.Should().ContainSingle(e => e.Contains("ssn_bidx"));
    }

    [Fact]
    public async Task Transformer_AllowsBlindIndexColumnInApprovedReplay()
    {
        // An approved replay re-applies the post-transformer payload verbatim —
        // ciphertext plus the ORIGINAL token — so the guard must not veto it.
        var table = SecretsModel().Tables.Single(t => t.GraphQlName == "secrets");
        var userContext = new Dictionary<string, object?>();
        ApprovalInterceptMutationHook.MarkApprovedReplay(userContext, 1, "approver");
        var (transformer, ctx) = BuildTransformer(userContext);

        var data = new Dictionary<string, object?>
        {
            ["ssn"] = "enc:v1:...",
            ["ssn_bidx"] = "original-token",
        };
        var result = await transformer.TransformAsync(table, MutationType.Insert, data, ctx);

        result.Errors.Should().BeEmpty();
        result.Data["ssn_bidx"].Should().Be("original-token");
    }

    [Fact]
    public void Validation_RejectsNotNullBlindIndexWithNullableSource()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("secrets", t => t
                .WithPrimaryKey("id")
                .WithColumn("ssn", "nvarchar", isNullable: true)
                .WithColumn("ssn_bidx", "nvarchar", isNullable: false)
                .WithColumnMetadata("ssn", MetadataKeys.Crypto.Encrypt, "aes-256-gcm")
                .WithColumnMetadata("ssn", MetadataKeys.Crypto.KeyRef, "config:pii")
                .WithColumnMetadata("ssn", MetadataKeys.Crypto.BlindIndex, "ssn_bidx"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<Exception>()
            .WithMessage("*blind-index column 'ssn_bidx' is NOT NULL*");
    }

    [Fact]
    public void Validation_AcceptsNullableBlindIndexColumn()
    {
        var act = () => ModelConfigValidator.Validate(SecretsModel());

        act.Should().NotThrow();
    }
}
