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
using System.Security.Claims;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// COLUMN-level security for the <c>_table</c> generic-query escape hatch.
///
/// <see cref="GenericTableQuerySecurityFilterTests"/> covers only ROW security
/// (tenant scope) — it proves <c>_table</c> ANDs the transformers' combined
/// filter onto the WHERE. That leaves the other half of the transformer chain
/// unexercised: <c>_table</c> called <c>GetCombinedFilter</c> directly instead of
/// going through the column read guards, and then issued <c>SELECT *</c> with no
/// crypto read projection. So a caller holding only the generic-table role read
/// policy-denied columns in full, and read envelope-encrypted columns as RAW
/// CIPHERTEXT — while the very same caller's ordinary table query got both
/// protections. A row-only fixture cannot manifest either bug.
/// </summary>
public sealed class GenericTableColumnSecurityTests : IAsyncLifetime
{
    private const string ConnString =
        "Data Source=bifrost_generic_table_column_security_test;Mode=Memory;Cache=Shared";

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;
    private EnvelopeKeyManager _manager = null!;

    private static readonly string[] Rules =
    {
        ":root { generic-table: enabled }",
        "main.people { policy-actions: read; policy-read-deny: salary }",
        "main.people.ssn { encrypt: aes-256-gcm; key-ref: config:pii; mask: last4; unmask-role: compliance }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS people");
        await Exec(
            """
            CREATE TABLE people (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                salary INTEGER NOT NULL,
                ssn TEXT NULL
            )
            """);

        var factory = new SqliteDbConnFactory(ConnString);
        _model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();

        var root = new byte[FieldCipher.KeySize];
        for (var i = 0; i < root.Length; i++) root[i] = (byte)(i + 11);
        _manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), new InMemoryDataEncryptionKeyStore());

        // Seed through the encrypt-on-write pipeline so `ssn` is stored as a real
        // envelope — the value a naive `SELECT *` would hand straight to the caller.
        var insert = await InsertAsync(
            "mutation { people(insert: { name: \"ada\", salary: 250000, ssn: \"123-45-6789\" }) }");
        insert.Errors.Should().BeNullOrEmpty();
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

    /// <summary>The stored envelope for the seeded row, read straight from SQLite.</summary>
    private async Task<string> StoredSsnCiphertextAsync()
    {
        await using var cmd = new SqliteCommand("SELECT ssn FROM people WHERE id = 1", _keepAlive);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static ClaimsPrincipal PrincipalWith(params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "test-user") };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private async Task<GenericTableResult> QueryAsync(
        Dictionary<string, object?>? filter, params string[] roles)
    {
        var schema = DbSchema.FromModel(_model);
        var services = new ServiceCollection();
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

        var arguments = new Dictionary<string, object?> { ["name"] = "people" };
        if (filter != null)
            arguments["filter"] = filter;

        var context = new FakeFieldContext
        {
            Arguments = arguments,
            UserContext = new Dictionary<string, object?>
            {
                ["user"] = PrincipalWith(roles),
                ["user_id"] = "test-user",
                ["roles"] = roles,
            },
            RequestServices = provider,
            InputExtensions = new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            },
        };

        var resolver = new GenericTableQueryResolver(_model, GenericTableConfig.FromModel(_model));
        return (GenericTableResult)(await resolver.ResolveAsync(context))!;
    }

    [Fact]
    public async Task GenericTable_PolicyDeniedColumn_IsNotReturned()
    {
        // `salary` is policy-read-denied for this caller. `_table` never ran the
        // column read guards, so `SELECT *` handed the value over in full.
        var result = await QueryAsync(filter: null, "bifrost-admin");

        result.Rows.Should().ContainSingle();
        result.Rows[0].Should().NotContainKey("salary");
        result.Rows[0].Values.Should().NotContain(v => Equals(v, 250000L));
        result.Columns.Should().NotContain(c => c.Name.Equals("salary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GenericTable_EncryptedColumn_IsMasked_NeverCiphertext()
    {
        // `_table` built no CryptoReadProjector, so the raw envelope went to the
        // caller — worse than the plaintext leak it is supposed to prevent, since
        // the ciphertext is also an offline attack surface.
        var ciphertext = await StoredSsnCiphertextAsync();
        ciphertext.Should().NotBeNullOrWhiteSpace();

        var result = await QueryAsync(filter: null, "bifrost-admin");

        var ssn = result.Rows[0]["ssn"]?.ToString();
        ssn.Should().NotBe(ciphertext);
        ssn.Should().NotBe("123-45-6789");
        ssn.Should().EndWith("6789").And.NotContain("123-45");
    }

    [Fact]
    public async Task GenericTable_UnmaskRole_ReadsPlaintext_NotCiphertext()
    {
        // Positive control: the projector is genuinely running, not blanket-redacting.
        var result = await QueryAsync(filter: null, "bifrost-admin", "compliance");

        result.Rows[0]["ssn"]?.ToString().Should().Be("123-45-6789");
    }

    [Fact]
    public async Task GenericTable_FilterOnPolicyDeniedColumn_IsRejected()
    {
        // Filtering on a column the caller may not read is the same binary oracle
        // the ordinary query path rejects; `_table` must reject it identically.
        var act = () => QueryAsync(
            new Dictionary<string, object?>
            {
                ["salary"] = new Dictionary<string, object?> { ["_gt"] = 200000 },
            },
            "bifrost-admin");

        await act.Should().ThrowAsync<BifrostExecutionError>();
    }

    [Fact]
    public async Task GenericTable_FilterOnEncryptedColumn_IsRejected()
    {
        var act = () => QueryAsync(
            new Dictionary<string, object?>
            {
                ["ssn"] = new Dictionary<string, object?> { ["_eq"] = "123-45-6789" },
            },
            "bifrost-admin", "compliance");

        await act.Should().ThrowAsync<BifrostExecutionError>();
    }

    [Fact]
    public async Task GenericTable_AllowedColumns_StillReturned()
    {
        // The projection must narrow to the denied column only — everything else
        // the caller was already entitled to must still come back.
        var result = await QueryAsync(filter: null, "bifrost-admin");

        result.TotalCount.Should().Be(1);
        result.Rows[0].Should().ContainKey("id");
        result.Rows[0]["name"].Should().Be("ada");
    }

    [Fact]
    public async Task GenericTable_FilterOnAllowedColumn_StillWorks()
    {
        var result = await QueryAsync(
            new Dictionary<string, object?>
            {
                ["name"] = new Dictionary<string, object?> { ["_eq"] = "ada" },
            },
            "bifrost-admin");

        result.TotalCount.Should().Be(1);
    }

    private sealed class FakeFieldContext : IBifrostFieldContext
    {
        public string FieldName => "_table";
        public string? FieldAlias => null;
        public object? Source => null;
        public IReadOnlyList<object> Path => Array.Empty<object>();
        public IDictionary<string, object?> UserContext { get; init; } = new Dictionary<string, object?>();
        public IServiceProvider? RequestServices { get; init; }
        public bool HasSubFields => false;
        public object Document => null!;
        public object Variables => null!;
        public IDictionary<string, object?> InputExtensions { get; init; } = new Dictionary<string, object?>();
        public CancellationToken CancellationToken => CancellationToken.None;
        public IDictionary<string, object?> Arguments { get; init; } = new Dictionary<string, object?>();

        public bool HasArgument(string name) => Arguments.ContainsKey(name);
        public T? GetArgument<T>(string name) => Arguments.TryGetValue(name, out var v) ? (T?)v : default;
    }
}
