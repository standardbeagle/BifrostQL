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
/// Resolver-path integration coverage for the wiring of
/// <see cref="MutationTransformResult.AdditionalFilter"/> into the update/delete
/// SQL WHERE clause (sub-task: apply AdditionalFilter to resolver SQL path).
///
/// Before this fix the resolver SQL path consumed the transformer's
/// <see cref="MutationTransformResult.Errors"/> but ignored
/// <see cref="MutationTransformResult.AdditionalFilter"/>, so a row-scope-denied
/// update/delete still affected out-of-scope rows end-to-end — a security gap.
///
/// These tests drive a real GraphQL mutation through <see cref="DbTableMutateResolver"/>
/// against a self-contained shared-cache in-memory SQLite database, with
/// <see cref="PolicyMutationTransformer"/> wired into a real
/// <see cref="MutationTransformersWrap"/>, and assert against the actual row
/// state in the database. They do not depend on the integration suite's
/// environment-gated database infrastructure.
/// </summary>
[Collection("MutationAdditionalFilterIntegration")]
public sealed class MutationAdditionalFilterIntegrationTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private IDbModel _model = null!;
    private ISchema _schema = null!;

    // Policy: Orders permits read + update + delete for everyone. The row-scope
    // expression (which restricts update/delete to the caller's own tenant) is
    // applied directly to the loaded table's metadata in InitializeAsync — the
    // MetadataLoader rule grammar reserves '{ }' as the rule delimiter, so the
    // '{tenant_id}' placeholder cannot be expressed through a rule string. The
    // unit tests (PolicyMutationTransformerTests) set it the same way via the
    // test fixture; here we set it post-load on the real loaded model.
    private static readonly string[] PolicyMetadata =
    {
        "main.Orders { policy-actions: read,update,delete }",
    };

    private const string RowScopeExpression = "tenant_id = {tenant_id}";

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_addfilter_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();

        _connFactory = new SqliteDbConnFactory(_connectionString);

        await using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync();
            var ddl = new SqliteCommand(
                @"CREATE TABLE Orders (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tenant_id INTEGER NOT NULL,
                    total REAL NOT NULL
                )", conn);
            await ddl.ExecuteNonQueryAsync();

            // Id 1 -> tenant 1 (in scope for user-1), Id 2 -> tenant 2 (out of scope).
            var seed = new SqliteCommand(
                "INSERT INTO Orders (tenant_id, total) VALUES (1, 10.0), (2, 20.0)", conn);
            await seed.ExecuteNonQueryAsync();
        }

        var metadataLoader = new MetadataLoader(PolicyMetadata);
        var loader = new DbModelLoader(_connFactory, metadataLoader);
        _model = await loader.LoadAsync();

        // Apply the row-scope expression directly to the loaded table — see the
        // PolicyMetadata comment for why it cannot go through a rule string.
        _model.GetTableFromDbName("Orders").Metadata[MetadataKeys.Policy.RowScope] = RowScopeExpression;

        _schema = DbSchema.FromModel(_model);
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

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

    private long CountOrders()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = new SqliteCommand("SELECT COUNT(*) FROM Orders", conn);
        return (long)cmd.ExecuteScalar()!;
    }

    // ---- Update: out-of-scope row is untouched ----

    [Fact]
    public async Task Update_OutOfScopeRow_DoesNotChangeRow()
    {
        // user-1 is tenant 1; Id 2 belongs to tenant 2 — the row-scope filter
        // must AND tenant_id = 1 into the WHERE clause so this update matches
        // zero rows.
        var result = await ExecuteAsync(
            "mutation { orders(update: { id: 2, tenant_id: 2, total: 999.0 }) }",
            User("user", tenantId: 1));

        result.Errors.Should().BeNullOrEmpty();
        GetOrder(2).total.Should().Be(20.0, "the out-of-scope row must not be modified");
    }

    [Fact]
    public async Task Update_InScopeRow_IsModified()
    {
        // Id 1 belongs to tenant 1 — in scope for user-1, so the update applies.
        var result = await ExecuteAsync(
            "mutation { orders(update: { id: 1, tenant_id: 1, total: 111.0 }) }",
            User("user", tenantId: 1));

        result.Errors.Should().BeNullOrEmpty();
        GetOrder(1).total.Should().Be(111.0, "the in-scope row must be updated normally");
    }

    // ---- Delete: out-of-scope row is untouched ----

    [Fact]
    public async Task Delete_OutOfScopeRow_DoesNotDeleteRow()
    {
        var before = CountOrders();

        var result = await ExecuteAsync(
            "mutation { orders(delete: { id: 2 }) }",
            User("user", tenantId: 1));

        result.Errors.Should().BeNullOrEmpty();
        CountOrders().Should().Be(before, "the out-of-scope row must not be deleted");
    }

    [Fact]
    public async Task Delete_InScopeRow_IsDeleted()
    {
        var before = CountOrders();

        var result = await ExecuteAsync(
            "mutation { orders(delete: { id: 1 }) }",
            User("user", tenantId: 1));

        result.Errors.Should().BeNullOrEmpty();
        CountOrders().Should().Be(before - 1, "the in-scope row must be deleted normally");
    }

    // ---- Admin bypasses row scope: out-of-scope row is reachable ----

    [Fact]
    public async Task Update_AdminRole_BypassesRowScopeAndModifiesOutOfScopeRow()
    {
        var result = await ExecuteAsync(
            "mutation { orders(update: { id: 2, tenant_id: 2, total: 777.0 }) }",
            User("admin", tenantId: 1));

        result.Errors.Should().BeNullOrEmpty();
        GetOrder(2).total.Should().Be(777.0, "an admin is not narrowed by the row-scope filter");
    }
}
