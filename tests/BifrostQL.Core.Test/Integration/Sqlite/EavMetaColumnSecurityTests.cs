using System.Text.Json;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Modules.Crypto;
using BifrostQL.Core.Modules.Eav;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// COLUMN-level security for the EAV <c>_meta</c> field.
///
/// <see cref="EavMetaProviderSecurityFilterTests"/> covers only the ROW half: it
/// proves the meta table's combined soft-delete/tenant filter is ANDed onto the
/// meta read. That fixture is a plain <c>(meta_key, meta_value, deleted_at)</c>
/// table with no policy and no encryption, so it cannot manifest either column-half
/// bug — the same vacuity the <c>_table</c> row-only fixture had.
///
/// The bugs: <see cref="EavMetaProvider"/> called
/// <c>IFilterTransformers.GetCombinedFilter</c> and then issued
/// <c>SELECT key,value FROM meta WHERE fk=@pk</c> itself, so
/// <see cref="IColumnReadGuard"/> never saw the key/value columns,
/// <see cref="IColumnFilterGuard"/> never saw the FK it filters by, and no
/// <c>CryptoReadProjector</c> ran — an envelope-encrypted meta value was serialized
/// into the client-visible <c>_meta</c> JSON as RAW CIPHERTEXT.
/// </summary>
public sealed class EavMetaColumnSecurityTests : IAsyncLifetime
{
    private const string ConnString =
        "Data Source=bifrost_eav_meta_column_security_test;Mode=Memory;Cache=Shared";

    private SqliteConnection _keepAlive = null!;
    private EnvelopeKeyManager _manager = null!;

    /// <summary>The plaintext behind the seeded encrypted attribute value.</summary>
    private const string SecretValue = "123-45-6789";

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS postmeta");
        await Exec("DROP TABLE IF EXISTS posts");
        await Exec(
            """
            CREATE TABLE posts (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE postmeta (
                id INTEGER PRIMARY KEY,
                post_id INTEGER NOT NULL,
                meta_key TEXT NOT NULL,
                meta_value TEXT
            )
            """);
        await Exec("INSERT INTO posts(id, title) VALUES (1, 'first')");

        var root = new byte[FieldCipher.KeySize];
        for (var i = 0; i < root.Length; i++) root[i] = (byte)(i + 23);
        _manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), new InMemoryDataEncryptionKeyStore());

        // Seed the attribute value as a REAL envelope, produced by the same cipher the
        // write path uses — this is exactly the bytes a naive read hands to the client.
        var dek = _manager.GetDataKey("config:pii");
        var aad = CryptoAad.Build("main", "postmeta", "meta_value");
        Ciphertext = FieldCipher.Encrypt(dek, SecretValue, aad);

        await using var insert = new SqliteCommand(
            "INSERT INTO postmeta(id, post_id, meta_key, meta_value) VALUES (1, 1, 'ssn', @v)", _keepAlive);
        insert.Parameters.AddWithValue("@v", Ciphertext);
        await insert.ExecuteNonQueryAsync();
    }

    private string Ciphertext { get; set; } = "";

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private static readonly string[] EncryptedValueRules =
    {
        "*.postmeta { eav-parent: posts; eav-fk: post_id; eav-key: meta_key; eav-value: meta_value }",
        "main.postmeta.meta_value { encrypt: aes-256-gcm; key-ref: config:pii; mask: last4; unmask-role: compliance }",
    };

    private static readonly string[] DeniedValueRules =
    {
        "*.postmeta { eav-parent: posts; eav-fk: post_id; eav-key: meta_key; eav-value: meta_value; policy-actions: read; policy-read-deny: meta_value }",
    };

    private static readonly string[] DeniedForeignKeyRules =
    {
        "*.postmeta { eav-parent: posts; eav-fk: post_id; eav-key: meta_key; eav-value: meta_value; policy-actions: read; policy-read-deny: post_id }",
    };

    private async Task<IDbModel> LoadModelAsync(params string[] rules)
    {
        var factory = new SqliteDbConnFactory(ConnString);
        return await new DbModelLoader(factory, new MetadataLoader(rules)).LoadAsync();
    }

    [Fact]
    public async Task EavMeta_EncryptedValueColumn_IsMasked_NeverCiphertext()
    {
        var model = await LoadModelAsync(EncryptedValueRules);

        var result = await ExecuteQueryAsync(model, MetaQuery, "bifrost-admin");

        result.Errors.Should().BeNullOrEmpty();
        var value = ExtractMeta(result).GetProperty("ssn").GetString();

        // The whole point: the caller must never receive the stored envelope.
        value.Should().NotBe(Ciphertext);
        value.Should().NotBe(SecretValue);
        value.Should().EndWith("6789").And.NotContain("123-45");
    }

    [Fact]
    public async Task EavMeta_UnmaskRole_ReadsPlaintext()
    {
        // Positive control: the projector genuinely runs and is role-sensitive,
        // rather than blanket-redacting everything it touches.
        var model = await LoadModelAsync(EncryptedValueRules);

        var result = await ExecuteQueryAsync(model, MetaQuery, "bifrost-admin", "compliance");

        result.Errors.Should().BeNullOrEmpty();
        ExtractMeta(result).GetProperty("ssn").GetString().Should().Be(SecretValue);
    }

    [Fact]
    public async Task EavMeta_PolicyDeniedValueColumn_IsRejected()
    {
        // `_meta` is an explicit client selection, so a read-denied column aborts
        // the query — the same reject semantics the ordinary query path uses.
        // Before the fix the denied value came back in the _meta JSON in full.
        var model = await LoadModelAsync(DeniedValueRules);

        var result = await ExecuteQueryAsync(model, MetaQuery, "bifrost-admin");

        result.Errors.Should().NotBeNullOrEmpty();
        var payload = new GraphQLSerializer().Serialize(result);
        payload.Should().NotContain(SecretValue);
        payload.Should().NotContain(Ciphertext);
    }

    [Fact]
    public async Task EavMeta_PolicyDeniedForeignKeyColumn_IsRejected()
    {
        // The FK is this read's WHERE predicate. A predicate on a column the caller
        // may not read is a value oracle whether or not the column is projected, so
        // it must clear the filter guard as well — it previously cleared neither.
        var model = await LoadModelAsync(DeniedForeignKeyRules);

        var result = await ExecuteQueryAsync(model, MetaQuery, "bifrost-admin");

        result.Errors.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task EavMeta_NoColumnRestrictions_StillReturnsAttributes()
    {
        // Guard against over-rejection: an unencrypted, unrestricted meta table must
        // still surface its attributes exactly as before.
        var model = await LoadModelAsync(
            "*.postmeta { eav-parent: posts; eav-fk: post_id; eav-key: meta_key; eav-value: meta_value }");

        var result = await ExecuteQueryAsync(model, MetaQuery, "bifrost-admin");

        result.Errors.Should().BeNullOrEmpty();
        ExtractMeta(result).GetProperty("ssn").GetString().Should().Be(Ciphertext);
    }

    private const string MetaQuery = "{ posts(filter: { id: { _eq: 1 } }) { data { id _meta } } }";

    private static JsonElement ExtractMeta(ExecutionResult result)
    {
        var json = new GraphQLSerializer().Serialize(result);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("data")
            .GetProperty("posts")
            .GetProperty("data")
            .EnumerateArray()
            .First()
            .GetProperty("_meta")
            .Clone();
    }

    private async Task<ExecutionResult> ExecuteQueryAsync(IDbModel model, string query, params string[] roles)
    {
        var schema = DbSchema.FromModel(model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IComputedColumnProvider, EavMetaProvider>();
        services.AddSingleton<IComputedColumnProviders>(sp =>
            new ComputedColumnProviders(sp.GetServices<IComputedColumnProvider>()));
        services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new PolicyFilterTransformer(),
                new EncryptedColumnReadGuard(),
            },
        });
        services.AddSingleton(_manager);
        await using var provider = services.BuildServiceProvider();

        var executor = new DocumentExecuter();
        return await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>
            {
                ["user_id"] = "test-user",
                ["roles"] = roles,
            };
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema, BifrostQL.Core.Modules.NullQueryTransformerService.Instance),
            });
        });
    }
}
