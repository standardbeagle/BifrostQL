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
/// End-to-end proof of online key rotation + the re-encryption sweep (Crypto rotation slice).
/// A value written under an old DEK stays readable after rotation (version-directed decrypt),
/// a half-re-encrypted table serves BOTH old-key and new-key rows without ever returning
/// ciphertext, and the sweep re-encrypts stale rows onto the current version strictly through
/// <see cref="IMutationIntentExecutor"/> (so <see cref="EncryptOnWriteMutationTransformer"/>
/// does the encrypting — no direct SQL, no tenant-scope bypass).
/// </summary>
public sealed class CryptoRotationSweepTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_crypto_rotation_sweep_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private const string KeyRef = "config:pii";
    private const string Plain1 = "111-11-1111";
    private const string Plain2 = "222-22-2222";
    private const string Plain3 = "333-33-3333";

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;
    private EnvelopeKeyManager _manager = null!;

    private static readonly string[] Rules =
    {
        "main.secrets.ssn { encrypt: aes-256-gcm; key-ref: config:pii; mask: last4; unmask-role: compliance; blind-index: ssn_bidx }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS secrets");
        await Exec("CREATE TABLE secrets (id INTEGER PRIMARY KEY, ssn TEXT NULL, ssn_bidx TEXT NULL)");

        var factory = new SqliteDbConnFactory(ConnString);
        _model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();

        var root = new byte[FieldCipher.KeySize];
        for (var i = 0; i < root.Length; i++) root[i] = (byte)(i + 7);
        _manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), new InMemoryDataEncryptionKeyStore());
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private MutationIntentExecutor BuildExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, () => Task.FromResult(new Inputs(new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["connFactory"] = new SqliteDbConnFactory(ConnString),
        })));

        var transformers = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new EncryptOnWriteMutationTransformer() },
        };

        // The pipeline resolves the EnvelopeKeyManager from these services, exactly like the host.
        var services = new ServiceCollection();
        services.AddSingleton(_manager);
        return new MutationIntentExecutor(pathCache, transformers, services.BuildServiceProvider());
    }

    private Task<long> InsertAsync(MutationIntentExecutor exec, string plaintext) =>
        exec.ExecuteAsync(new MutationIntent
        {
            Table = "secrets",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["ssn"] = plaintext },
            Endpoint = EndpointPath,
        }).ContinueWith(t => Convert.ToInt64(t.Result.Value));

    /// <summary>Raw stored rows (bypassing the read projector) so the test can assert the
    /// on-disk envelope's key version and that it is never the plaintext.</summary>
    private async Task<List<(long id, string? ssn)>> ReadRawAsync()
    {
        var rows = new List<(long, string?)>();
        await using var cmd = new SqliteCommand("SELECT id, ssn FROM secrets ORDER BY id", _keepAlive);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return rows;
    }

    private static IReadOnlyDictionary<string, object?> RawRow(long id, string? ssn) =>
        new Dictionary<string, object?> { ["id"] = id, ["ssn"] = ssn };

    [Fact]
    public async Task HalfRotatedTable_ServesBothOldAndNewKeyRows_NeverCiphertext()
    {
        var exec = BuildExecutor();
        await InsertAsync(exec, Plain1);   // written under v1
        _manager.Rotate(KeyRef);           // now v2 is current
        await InsertAsync(exec, Plain2);   // written under v2

        var raw = await ReadRawAsync();
        raw.Should().HaveCount(2);
        FieldCipher.PeekKeyVersion(raw[0].ssn!).Should().Be(1, "the first row is still on the old key");
        FieldCipher.PeekKeyVersion(raw[1].ssn!).Should().Be(2, "the second row is on the new key");

        // An unmask-role caller decrypts BOTH rows across the version boundary.
        var unmask = new CryptoReadProjector(_model, _manager, new[] { "compliance" });
        unmask.Project("secrets", "ssn", raw[0].ssn).Should().Be(Plain1);
        unmask.Project("secrets", "ssn", raw[1].ssn).Should().Be(Plain2);

        // A denied caller gets the mask for BOTH — never the plaintext, never the ciphertext.
        var denied = new CryptoReadProjector(_model, _manager, new[] { "viewer" });
        foreach (var (row, plain) in new[] { (raw[0], Plain1), (raw[1], Plain2) })
        {
            var masked = denied.Project("secrets", "ssn", row.ssn)?.ToString();
            masked.Should().NotBe(plain).And.NotBe(row.ssn, "the raw ciphertext is never returned");
            masked.Should().EndWith(plain[^4..]);
        }
    }

    [Fact]
    public async Task Sweep_ReEncryptsStaleRow_ThroughPipeline_LeavingUnsweptRowReadable()
    {
        var exec = BuildExecutor();
        var id1 = await InsertAsync(exec, Plain1); // v1
        var id2 = await InsertAsync(exec, Plain2); // v1
        _manager.Rotate(KeyRef);                    // -> v2 current
        var id3 = await InsertAsync(exec, Plain3); // v2

        // Sweep ONLY row 1 => half-re-encrypted table.
        var table = _model.GetTableFromDbName("secrets");
        var beforeRaw = await ReadRawAsync();
        var sweep = new CryptoReEncryptionSweep(exec, _manager);
        var result = await sweep.ReEncryptRowsAsync(table, new[] { RawRow(id1, beforeRaw.First(r => r.id == id1).ssn) });

        result.RowsScanned.Should().Be(1);
        result.RowsReEncrypted.Should().Be(1);
        result.RowsAffected.Should().Be(1, "the update went through the mutation pipeline and hit one row");

        var raw = (await ReadRawAsync()).ToDictionary(r => r.id, r => r.ssn);
        FieldCipher.PeekKeyVersion(raw[id1]!).Should().Be(2, "the swept row is now on the current key");
        FieldCipher.PeekKeyVersion(raw[id2]!).Should().Be(1, "the un-swept row is left on the old key (half-swept)");
        FieldCipher.PeekKeyVersion(raw[id3]!).Should().Be(2);

        // Every row — swept, un-swept, natively-new — still decrypts to its original plaintext.
        var unmask = new CryptoReadProjector(_model, _manager, new[] { "compliance" });
        unmask.Project("secrets", "ssn", raw[id1]).Should().Be(Plain1);
        unmask.Project("secrets", "ssn", raw[id2]).Should().Be(Plain2);
        unmask.Project("secrets", "ssn", raw[id3]).Should().Be(Plain3);
    }

    [Fact]
    public async Task Sweep_IsIdempotent_OverAlreadyCurrentRows()
    {
        var exec = BuildExecutor();
        var id1 = await InsertAsync(exec, Plain1);
        _manager.Rotate(KeyRef);
        var table = _model.GetTableFromDbName("secrets");

        // First sweep advances row 1 to the current version.
        var raw1 = await ReadRawAsync();
        (await new CryptoReEncryptionSweep(exec, _manager)
            .ReEncryptRowsAsync(table, new[] { RawRow(id1, raw1[0].ssn) })).RowsAffected.Should().Be(1);

        // Second sweep sees a current row and re-encrypts nothing.
        var raw2 = await ReadRawAsync();
        var second = await new CryptoReEncryptionSweep(exec, _manager)
            .ReEncryptRowsAsync(table, new[] { RawRow(id1, raw2[0].ssn) });
        second.RowsScanned.Should().Be(1);
        second.RowsReEncrypted.Should().Be(0, "a row already on the current version is left untouched");
        second.RowsAffected.Should().Be(0);
    }

    [Fact]
    public async Task Sweep_SkipsNullEncryptedValue()
    {
        var exec = BuildExecutor();
        var id = await InsertAsync(exec, Plain1);
        await Exec($"UPDATE secrets SET ssn = NULL WHERE id = {id}");
        _manager.Rotate(KeyRef);

        var table = _model.GetTableFromDbName("secrets");
        var result = await new CryptoReEncryptionSweep(exec, _manager)
            .ReEncryptRowsAsync(table, new[] { RawRow(id, null) });

        result.RowsReEncrypted.Should().Be(0, "a NULL value has nothing to re-encrypt");
        (await ReadRawAsync())[0].ssn.Should().BeNull();
    }
}
