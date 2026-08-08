using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Crypto;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// COLUMN half of the tree-sync state load.
///
/// <see cref="TreeSyncStateLoaderSecurityFilterTests"/> is tenant-ROW-only: its
/// fixture carries a <c>tenant_id</c> and nothing else, so it cannot manifest either
/// column-half problem. The loader ran <c>GetCombinedFilter</c> and then SELECTed
/// EVERY column with a raw reader loop.
///
/// Reachability: these rows are diff-internal. <c>DbTableMutateResolver.SyncObject</c>
/// hands the loaded subtree to <see cref="TreeSyncEngine.ComputeOperations"/> and
/// returns only the root key, and the engine writes only values taken from the
/// SUBMITTED tree — so no loaded value is ever echoed to the caller. The crypto side
/// still matters, and in the opposite direction from a client surface: the diff
/// compares a submitted PLAINTEXT against a stored CIPHERTEXT, so an unchanged
/// encrypted field always compared unequal and was re-encrypted and rewritten on
/// EVERY sync. Masking would not fix that (a mask compares unequal too); the loaded
/// value must be DECRYPTED for the comparison, which is what
/// <see cref="ReadProjection.InternalDiff"/> means.
/// </summary>
public sealed class TreeSyncStateLoaderColumnSecurityTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _factory;
    private readonly EnvelopeKeyManager _manager;

    private const string Secret = "123-45-6789";

    public TreeSyncStateLoaderColumnSecurityTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-treesync-columns-{Guid.NewGuid():N}.db");
        _factory = new SqliteDbConnFactory($"Data Source={_dbPath}");

        var root = new byte[FieldCipher.KeySize];
        for (var i = 0; i < root.Length; i++) root[i] = (byte)(i + 41);
        _manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), new InMemoryDataEncryptionKeyStore());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    /// <summary>
    /// people(person_id, name, salary, ssn): <c>salary</c> is policy-read-denied and
    /// <c>ssn</c> is envelope-encrypted — neither shape exists in the row-only fixture.
    /// </summary>
    private static IDbModel BuildModel() =>
        DbModelTestFixture.Create()
            .WithTable("people", t => t
                .WithPrimaryKey("person_id")
                .WithColumn("name", "nvarchar")
                .WithColumn("salary", "int")
                .WithColumn("ssn", "nvarchar")
                .WithMetadata(MetadataKeys.Policy.Actions, "read")
                .WithMetadata(MetadataKeys.Policy.ReadDeny, "salary")
                .WithColumnMetadata("ssn", MetadataKeys.Crypto.Encrypt, "aes-256-gcm")
                .WithColumnMetadata("ssn", MetadataKeys.Crypto.KeyRef, "config:pii")
                .WithColumnMetadata("ssn", MetadataKeys.Crypto.Mask, MetadataKeys.Crypto.MaskLast4))
            .Build();

    private async Task SeedAsync(IDbTable table)
    {
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE people (person_id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, salary INTEGER, ssn TEXT)",
            null, 30, 1000);

        var dek = _manager.GetDataKey("config:pii");
        var aad = CryptoAad.Build(table.TableSchema, table.DbName, "ssn");
        var envelope = FieldCipher.Encrypt(dek, Secret, aad);

        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO people (person_id, name, salary, ssn) VALUES (1,'ada',250000,@ssn)",
            new Dictionary<string, object?> { ["@ssn"] = envelope }, 30, 1000);
    }

    private IServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new PolicyFilterTransformer() },
        });
        services.AddSingleton(_manager);
        return services.BuildServiceProvider();
    }

    private async Task<Dictionary<string, object?>?> LoadAsync(params string[] roles)
    {
        var model = BuildModel();
        var people = model.GetTableFromDbName("people");
        await SeedAsync(people);

        var loader = new TreeSyncStateLoader(
            _factory.Dialect,
            model,
            new Dictionary<string, object?> { ["user_id"] = "test-user", ["roles"] = roles },
            Services());

        var tree = new Dictionary<string, object?> { ["person_id"] = 1L, ["name"] = "ada" };
        return await loader.LoadAsync(people, tree, _factory);
    }

    [Fact]
    public async Task LoadAsync_EncryptedColumn_IsDecryptedForDiff_NotLeftAsCiphertext()
    {
        var existing = await LoadAsync("bifrost-admin");

        existing.Should().NotBeNull();
        // Decrypted, so a resubmitted identical value diffs as UNCHANGED. Left as the
        // envelope (the old behavior) it never equals the submitted plaintext.
        existing!["ssn"].Should().Be(Secret);
    }

    [Fact]
    public async Task ComputeOperations_UnchangedEncryptedValue_ProducesNoUpdate()
    {
        // The behavioural consequence, stated at the level the user sees it: syncing a
        // tree whose encrypted field is unchanged must not rewrite the row. Against a
        // ciphertext the comparison could never be equal, so every sync rewrote it.
        var model = BuildModel();
        var people = model.GetTableFromDbName("people");
        await SeedAsync(people);

        var loader = new TreeSyncStateLoader(
            _factory.Dialect,
            model,
            new Dictionary<string, object?> { ["user_id"] = "u", ["roles"] = new[] { "bifrost-admin" } },
            Services());

        var tree = new Dictionary<string, object?>
        {
            ["person_id"] = 1L,
            ["name"] = "ada",
            ["ssn"] = Secret,
        };
        var existing = await loader.LoadAsync(people, tree, _factory);
        var ops = new TreeSyncEngine(model).ComputeOperations(people, tree, existing);

        ops.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_PolicyDeniedColumn_IsNotProjected()
    {
        // The loader SELECTed every column irrespective of the column read guard.
        var existing = await LoadAsync("bifrost-admin");

        existing.Should().NotBeNull();
        existing!.Should().NotContainKey("salary");
        existing.Values.Should().NotContain(v => Equals(v, 250000L));
    }

    [Fact]
    public async Task LoadAsync_AllowedColumns_StillLoaded()
    {
        // Over-narrowing fence: the diff still needs the key and the ordinary columns.
        var existing = await LoadAsync("bifrost-admin");

        existing.Should().NotBeNull();
        existing!["person_id"].Should().Be(1L);
        existing["name"].Should().Be("ada");
    }
}
