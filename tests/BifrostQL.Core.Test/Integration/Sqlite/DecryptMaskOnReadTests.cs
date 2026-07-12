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
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// End-to-end proof of the decrypt/mask-on-read guard (Crypto slice 3). A caller
/// holding the column's unmask-role (or admin) reads plaintext; everyone else reads
/// the masked value. Using an encrypted column as a filter or sort predicate is
/// rejected so it cannot be used as a plaintext oracle. The encrypt-on-write and
/// read paths share one key manager so ciphertext round-trips.
/// </summary>
public sealed class DecryptMaskOnReadTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_decrypt_mask_read_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;
    private EnvelopeKeyManager _manager = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS secrets");
        await Exec("CREATE TABLE secrets (id INTEGER PRIMARY KEY, ssn TEXT NULL, ssn_bidx TEXT NULL)");

        var factory = new SqliteDbConnFactory(ConnString);
        _model = await new DbModelLoader(factory, new MetadataLoader(new[]
        {
            "main.secrets.ssn { encrypt: aes-256-gcm; key-ref: config:pii; mask: last4; unmask-role: compliance; blind-index: ssn_bidx }",
        })).LoadAsync();

        var root = new byte[FieldCipher.KeySize];
        for (var i = 0; i < root.Length; i++) root[i] = (byte)(i + 3);
        _manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), new InMemoryDataEncryptionKeyStore());

        // Seed one encrypted row through the encrypt-on-write pipeline.
        (await InsertAsync("mutation { secrets(insert: { ssn: \"123-45-6789\" }) }")).Errors.Should().BeNullOrEmpty();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<ExecutionResult> InsertAsync(string mutation)
    {
        var schema = DbSchema.FromModel(_model);
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new EncryptOnWriteMutationTransformer() },
        });
        services.AddSingleton(_manager);
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            });
        });
    }

    private async Task<ExecutionResult> QueryAsync(string query, params string[] roles)
    {
        var schema = DbSchema.FromModel(_model);
        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new EncryptedColumnReadGuard() },
        });
        var services = new ServiceCollection();
        services.AddSingleton(_manager);
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?> { ["roles"] = roles };
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, transformerService),
            });
        });
    }

    private static string? Ssn(ExecutionResult result)
    {
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        return doc.RootElement.GetProperty("data").GetProperty("secrets")
            .GetProperty("data")[0].GetProperty("ssn").GetString();
    }

    [Fact]
    public async Task Read_WithUnmaskRole_ReturnsPlaintext()
    {
        var result = await QueryAsync("{ secrets { data { ssn } } }", "compliance");
        result.Errors.Should().BeNullOrEmpty();
        Ssn(result).Should().Be("123-45-6789");
    }

    [Fact]
    public async Task Read_WithAdminRole_ReturnsPlaintext()
    {
        var result = await QueryAsync("{ secrets { data { ssn } } }", "admin");
        result.Errors.Should().BeNullOrEmpty();
        Ssn(result).Should().Be("123-45-6789");
    }

    [Fact]
    public async Task Read_WithoutUnmaskRole_ReturnsMaskedLast4()
    {
        var result = await QueryAsync("{ secrets { data { ssn } } }", "viewer");
        result.Errors.Should().BeNullOrEmpty();
        var ssn = Ssn(result);
        ssn.Should().NotBe("123-45-6789").And.EndWith("6789").And.NotContain("123-45");
    }

    [Fact]
    public async Task Filter_OnEncryptedColumn_IsRejected()
    {
        var result = await QueryAsync("{ secrets(filter: { ssn: { _eq: \"x\" } }) { data { id } } }", "compliance");
        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Single().Message.Should().Contain("filter, sort, or aggregate");
    }

    [Fact]
    public async Task Sort_OnEncryptedColumn_IsRejected()
    {
        var result = await QueryAsync("{ secrets(sort: [ssn_asc]) { data { id } } }", "compliance");
        result.Errors.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Selecting_EncryptedColumn_IsAllowed_NotRejected()
    {
        // Selecting the column for output is fine (it is masked/decrypted on read);
        // only filter/sort/aggregate positions are rejected.
        var result = await QueryAsync("{ secrets { data { id ssn } } }", "viewer");
        result.Errors.Should().BeNullOrEmpty();
    }
}
