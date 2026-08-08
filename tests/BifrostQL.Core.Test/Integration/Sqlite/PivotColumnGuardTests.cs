using System.Text.Json;
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
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// COLUMN-guard position for the PIVOT surface.
///
/// <see cref="PivotSecurityFilterTests"/> and <see cref="PivotEndToEndTests"/> are
/// tenant-ROW-only: their fixture is <c>orders(tenant_id, region, status, amount)</c>
/// with no policy and no encryption, so neither can manifest this bug.
///
/// The bug was a guard POSITION mismatch rather than an outright bypass. The pivot
/// attached its referenced columns (row keys + pivotColumn + valueColumn) as
/// <c>ScalarColumns</c>, which <c>QueryTransformerService</c> routes through the READ
/// guard only. Every one of those columns is actually predicate-positioned — the row
/// keys are the GROUP BY, the value column is the aggregate argument, and the pivot
/// column drives a DISTINCT discovery query — so none of them ever reached
/// <see cref="IColumnFilterGuard"/>, which the sibling <c>&lt;table&gt;Aggregate</c>
/// surface does apply to exactly the same positions. An envelope-encrypted column
/// could therefore be pivoted although <c>filter</c>, <c>_order</c> and <c>_agg</c>
/// all reject it, and the discovery query returned the distinct CIPHERTEXT set with
/// a row count per value.
/// </summary>
public sealed class PivotColumnGuardTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_pivot_column_guard_test;Mode=Memory;Cache=Shared";

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;
    private EnvelopeKeyManager _manager = null!;

    private static readonly string[] Rules =
    {
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
                dept TEXT NOT NULL,
                region TEXT NOT NULL,
                salary INTEGER NOT NULL,
                ssn TEXT NULL
            )
            """);

        var root = new byte[FieldCipher.KeySize];
        for (var i = 0; i < root.Length; i++) root[i] = (byte)(i + 31);
        _manager = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), new InMemoryDataEncryptionKeyStore());

        var dek = _manager.GetDataKey("config:pii");
        var aad = CryptoAad.Build("main", "people", "ssn");
        var envelope = FieldCipher.Encrypt(dek, "123-45-6789", aad);

        await using var insert = new SqliteCommand(
            "INSERT INTO people(id, dept, region, salary, ssn) VALUES (1, 'eng', 'east', 250000, @v)", _keepAlive);
        insert.Parameters.AddWithValue("@v", envelope);
        await insert.ExecuteNonQueryAsync();

        var factory = new SqliteDbConnFactory(ConnString);
        _model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<ExecutionResult> PivotAsync(string query, params string[] roles)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);
        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new PolicyFilterTransformer(),
                new EncryptedColumnReadGuard(),
            },
        });

        var services = new ServiceCollection();
        services.AddSingleton(_manager);
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
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
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, transformerService),
            });
        });
    }

    /// <summary>
    /// The policy transformer's generic column-read-denied message.
    /// </summary>
    private const string PolicyReadDeniedMessage =
        "The query references a field that is not permitted by authorization policy.";

    /// <summary>
    /// Asserts the pivot was refused BY THE FILTER GUARD, not by the pivot's own shape
    /// validation. Without this the tests would pass vacuously: repeating a column
    /// across rowKeys/pivotColumn also errors, and an "Errors is not empty" assertion
    /// cannot tell the two apart.
    /// </summary>
    private static void ShouldBeFilterGuardRejection(ExecutionResult result)
    {
        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors![0].InnerException.Should().BeOfType<BifrostExecutionError>()
            .Which.Message.Should().Be("A requested column may not be used in a filter, sort, or aggregate.");
    }

    [Fact]
    public async Task Pivot_OnEncryptedColumn_IsRejected()
    {
        // The distinct-discovery query for this pivot returned the distinct set of
        // stored ENVELOPES plus a row count for each — strictly worse than the
        // plaintext the mask exists to withhold. `filter`/`_order`/`_agg` on the same
        // column are all rejected; the pivot must reject it identically.
        var result = await PivotAsync(
            "{ peoplePivot(rowKeys: [dept], pivotColumn: ssn, valueColumn: id, aggregate: count) }",
            "bifrost-admin", "compliance");

        ShouldBeFilterGuardRejection(result);
        new GraphQLSerializer().Serialize(result).Should().NotContain("123-45");
    }

    [Fact]
    public async Task Pivot_ValueColumnEncrypted_IsRejected()
    {
        var result = await PivotAsync(
            "{ peoplePivot(rowKeys: [dept], pivotColumn: region, valueColumn: ssn, aggregate: count) }",
            "bifrost-admin", "compliance");

        ShouldBeFilterGuardRejection(result);
    }

    [Fact]
    public async Task Pivot_RowKeyEncrypted_IsRejected()
    {
        // Row keys are the GROUP BY: grouping by a ciphertext partitions the result
        // set by the stored envelope and emits it as a row-key value.
        var result = await PivotAsync(
            "{ peoplePivot(rowKeys: [ssn], pivotColumn: region, valueColumn: id, aggregate: count) }",
            "bifrost-admin", "compliance");

        ShouldBeFilterGuardRejection(result);
    }

    [Fact]
    public async Task Pivot_OnPolicyDeniedColumn_IsRejected()
    {
        // The read guard already covered this position, so this is the regression
        // fence for it: routing the columns through AddFiltered must not weaken the
        // READ assertion that was already there.
        var result = await PivotAsync(
            "{ peoplePivot(rowKeys: [dept], pivotColumn: salary, valueColumn: id, aggregate: count) }",
            "bifrost-admin");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors![0].InnerException.Should().BeOfType<BifrostExecutionError>()
            .Which.Message.Should().Be(PolicyReadDeniedMessage);
        new GraphQLSerializer().Serialize(result).Should().NotContain("250000");
    }

    [Fact]
    public async Task Pivot_OnAllowedColumns_StillWorks()
    {
        // Over-rejection fence: an ordinary pivot over unrestricted columns must
        // still produce its cross-tab.
        var result = await PivotAsync(
            "{ peoplePivot(rowKeys: [dept], pivotColumn: region, valueColumn: id, aggregate: count) }",
            "bifrost-admin");

        result.Errors.Should().BeNullOrEmpty();
        var pivot = JsonDocument.Parse(new GraphQLSerializer().Serialize(result))
            .RootElement.GetProperty("data").GetProperty("peoplePivot");
        pivot.GetProperty("columns").EnumerateArray().Select(e => e.GetString()).Should().Equal("east");
    }
}
