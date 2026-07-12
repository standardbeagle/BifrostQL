using System.Text;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Crypto;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// End-to-end proof of the encrypt-on-write mutation transformer (Crypto slice 2).
/// An encrypted column's plaintext never reaches the database: the column stores an
/// AES-256-GCM envelope and the blind-index sibling stores a deterministic keyed
/// hash. When no key manager is configured, a write to an encrypted column fails
/// closed rather than persisting plaintext.
/// </summary>
public sealed class EncryptOnWriteTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_encrypt_on_write_test;Mode=Memory;Cache=Shared";
    private const string KeyRef = "config:pii";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS secrets");
        await Exec("CREATE TABLE secrets (id INTEGER PRIMARY KEY, ssn TEXT NULL, ssn_bidx TEXT NULL)");

        var factory = new SqliteDbConnFactory(ConnString);
        _model = await new DbModelLoader(factory, new MetadataLoader(new[]
        {
            "main.secrets.ssn { encrypt: aes-256-gcm; key-ref: config:pii; blind-index: ssn_bidx }",
        })).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(string? ssn, string? bidx)> ReadRawAsync(long id)
    {
        await using var cmd = new SqliteCommand("SELECT ssn, ssn_bidx FROM secrets WHERE id = $id", _keepAlive);
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (null, null);
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static EnvelopeKeyManager NewManager()
        => new(new ConfigRootKeyProvider(FixedRootKey()), new InMemoryDataEncryptionKeyStore());

    private static byte[] FixedRootKey()
    {
        var k = new byte[FieldCipher.KeySize];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 1);
        return k;
    }

    [Fact]
    public async Task Insert_StoresCiphertextAndBlindIndex_NotPlaintext()
    {
        var manager = NewManager();
        const string plaintext = "123-45-6789";

        var result = await ExecuteMutationAsync("mutation { secrets(insert: { ssn: \"123-45-6789\" }) }", manager);
        result.Errors.Should().BeNullOrEmpty();

        var (ssn, bidx) = await ReadRawAsync(1);
        ssn.Should().NotBeNull().And.NotBe(plaintext, "the column stores ciphertext, never the plaintext");

        // The stored value is a decryptable AES-256-GCM envelope bound to this cell.
        var dek = manager.GetDataKey(KeyRef);
        var aad = CryptoAad.Build("main", "secrets", "ssn");
        FieldCipher.Decrypt(dek, ssn!, aad).Should().Be(plaintext);

        // The blind index is the deterministic keyed hash of the plaintext.
        var expectedBidx = BlindIndexComputer.Compute(manager.GetBlindIndexKey(KeyRef), plaintext);
        bidx.Should().Be(expectedBidx);
    }

    [Fact]
    public async Task Insert_NullEncryptedValue_StaysNull()
    {
        var result = await ExecuteMutationAsync("mutation { secrets(insert: { ssn: null }) }", NewManager());
        result.Errors.Should().BeNullOrEmpty();
        (await ReadRawAsync(1)).ssn.Should().BeNull("a null is stored as NULL, not encrypted");
    }

    [Fact]
    public async Task Insert_WithoutKeyManager_FailsClosed_NoPlaintextWritten()
    {
        // No EnvelopeKeyManager registered: the write must be refused, not fall back
        // to storing plaintext.
        var result = await ExecuteMutationAsync("mutation { secrets(insert: { ssn: \"123-45-6789\" }) }", manager: null);

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Single().Message.Should().Contain("encryption key manager");

        await using var cmd = new SqliteCommand("SELECT COUNT(*) FROM secrets", _keepAlive);
        Convert.ToInt64(await cmd.ExecuteScalarAsync()).Should().Be(0, "the write was refused, so no row exists");
    }

    private async Task<ExecutionResult> ExecuteMutationAsync(string mutation, EnvelopeKeyManager? manager)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new EncryptOnWriteMutationTransformer() },
        });
        if (manager != null)
            services.AddSingleton(manager);
        await using var provider = services.BuildServiceProvider();

        var executor = new DocumentExecuter();
        return await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            });
        });
    }
}
