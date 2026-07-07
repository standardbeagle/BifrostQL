using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Integration.Test;

/// <summary>
/// Finding #1 coverage: a single-statement upsert must not bypass a transformer's
/// row-scope <see cref="MutationTransformResult.AdditionalFilter"/>. Routing the
/// upsert through the real Insert-or-Update decision means an upsert of an existing
/// out-of-scope row is enforced by the same row-scope WHERE guard as a plain update,
/// so it cannot take over a row in another tenant.
/// </summary>
[Collection("MutationUpsertPolicySecurity")]
public sealed class MutationUpsertPolicySecurityTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private IDbModel _model = null!;
    private ISchema _schema = null!;

    private static readonly string[] PolicyMetadata =
    {
        "main.Orders { policy-actions: read,update,delete }",
    };

    private const string RowScopeExpression = "tenant_id = {tenant_id}";

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_upsertpolicy_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();

        _connFactory = new SqliteDbConnFactory(_connectionString);

        await using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync();
            await new SqliteCommand(
                @"CREATE TABLE Orders (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tenant_id INTEGER NOT NULL,
                    total REAL NOT NULL
                );", conn).ExecuteNonQueryAsync();
            await new SqliteCommand(
                "INSERT INTO Orders (tenant_id, total) VALUES (1, 10.0), (2, 20.0);", conn).ExecuteNonQueryAsync();
        }

        var metadataLoader = new MetadataLoader(PolicyMetadata);
        var loader = new DbModelLoader(_connFactory, metadataLoader);
        _model = await loader.LoadAsync();
        _model.GetTableFromDbName("Orders").Metadata[MetadataKeys.Policy.RowScope] = RowScopeExpression;
        _schema = DbSchema.FromModel(_model);
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<ExecutionResult> ExecuteAsync(string query, IDictionary<string, object?> userContext)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new PolicyMutationTransformer() },
        });
        services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        });
        await using var provider = services.BuildServiceProvider();

        var executor = new SqlExecutionManager(_model, _schema);
        var extensions = new Dictionary<string, object?>
        {
            { "connFactory", _connFactory },
            { "model", _model },
            { "tableReaderFactory", executor },
        };

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = _schema;
            options.Query = query;
            options.Extensions = new Inputs(extensions);
            options.RequestServices = provider;
            options.UserContext = userContext;
        });
    }

    private static IDictionary<string, object?> User(string role, int tenantId) =>
        new Dictionary<string, object?>
        {
            ["user_id"] = "user-1",
            ["roles"] = new[] { role },
            ["tenant_id"] = tenantId,
        };

    private (long tenantId, double total) GetOrder(long id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = new SqliteCommand("SELECT tenant_id, total FROM Orders WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetDouble(1));
    }

    [Fact]
    public async Task Upsert_OutOfScopeExistingRow_IsNotTakenOver()
    {
        // Id 2 belongs to tenant 2 and already exists. user-1 is tenant 1, so the
        // upsert resolves to the UPDATE branch and the row-scope filter (tenant_id=1)
        // must AND into the WHERE — matching zero rows and leaving the row intact.
        var result = await ExecuteAsync(
            "mutation { orders(upsert: { id: 2, tenant_id: 2, total: 999.0 }) }",
            User("user", tenantId: 1));

        result.Errors.Should().BeNullOrEmpty();
        GetOrder(2).total.Should().Be(20.0, "an upsert must not take over an out-of-scope existing row");
    }

    [Fact]
    public async Task Upsert_InScopeExistingRow_IsUpdated()
    {
        var result = await ExecuteAsync(
            "mutation { orders(upsert: { id: 1, tenant_id: 1, total: 111.0 }) }",
            User("user", tenantId: 1));

        result.Errors.Should().BeNullOrEmpty();
        GetOrder(1).total.Should().Be(111.0, "an in-scope existing row upserts normally");
    }
}
