using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Verifies the protocol-adapter mutation-intent entry point
/// (<see cref="IMutationIntentExecutor"/>): a programmatic insert/update/delete —
/// no GraphQL text — must pass through the same mutation transformer chain as a
/// GraphQL mutation. Tenant isolation pins inserts and scopes updates/deletes
/// (cross-tenant writes are no-ops, matching the GraphQL path), audit columns are
/// stamped server-side, and the optimistic-concurrency token bumps on success and
/// CONFLICTs when stale.
/// </summary>
public sealed class MutationIntentExecutorTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_mutation_intent_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;

    private static readonly string[] Rules =
    {
        "*.orders { tenant-filter: tenant_id; concurrency-token: row_version }",
        "*.orders.created_at { populate: created-on }",
        "*.orders.updated_at { populate: updated-on }",
        "*.notes { tenant-filter: tenant_id }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS orders");
        await Exec("DROP TABLE IF EXISTS notes");
        await Exec(
            """
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                row_version INTEGER NOT NULL,
                created_at TEXT NULL,
                updated_at TEXT NULL
            )
            """);
        await Exec(
            """
            INSERT INTO orders(id, tenant_id, name, row_version, created_at, updated_at) VALUES
                (10, 1, 'tenant-one-order', 5, NULL, NULL),
                (20, 2, 'tenant-two-order', 3, NULL, NULL)
            """);
        await Exec(
            """
            CREATE TABLE notes (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                body TEXT NOT NULL
            )
            """);
        await Exec(
            """
            INSERT INTO notes(id, tenant_id, body) VALUES
                (1, 1, 'tenant-one-note'),
                (2, 2, 'tenant-two-note')
            """);
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    /// <summary>
    /// Builds the executor with the same built-in security/audit/concurrency
    /// transformers the server auto-prepends (see WithBuiltInMutationTransformers),
    /// wired to the endpoint's cached model exactly like the hosted registration.
    /// </summary>
    private static MutationIntentExecutor BuildExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["connFactory"] = factory,
            });
        });

        var transformers = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new PolicyMutationTransformer(),
                new StateMachineMutationTransformer(),
                new EnumValueMutationTransformer(),
                new SoftDeleteMutationTransformer(),
                new TenantMutationTransformer(),
                new AuditMutationTransformer(),
                new ConcurrencyMutationTransformer(),
            },
        };

        return new MutationIntentExecutor(pathCache, transformers);
    }

    private static IDictionary<string, object?> TenantContext(int tenantId) =>
        new Dictionary<string, object?> { ["tenant_id"] = tenantId };

    // ---- insert ---------------------------------------------------------

    [Fact]
    public async Task Insert_PinsTenantFromContext_OverridingClientValue_AndStampsAudit()
    {
        var executor = BuildExecutor();

        var result = await executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Insert,
            // The caller tries to plant the row in tenant 2; the tenant
            // transformer must pin it to the caller's tenant 1.
            Data = new Dictionary<string, object?>
            {
                ["name"] = "intent-insert",
                ["tenant_id"] = 2,
                ["row_version"] = 1,
            },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        result.Value.Should().NotBeNull("the insert returns the generated identity");
        var id = Convert.ToInt64(result.Value);

        (await ScalarAsync($"SELECT tenant_id FROM orders WHERE id = {id}")).Should().Be("1");
        (await ScalarAsync($"SELECT name FROM orders WHERE id = {id}")).Should().Be("intent-insert");
        // Audit transformer proof: server-side stamps, never client-supplied.
        (await ScalarAsync($"SELECT created_at FROM orders WHERE id = {id}")).Should().NotBeNullOrEmpty();
        (await ScalarAsync($"SELECT updated_at FROM orders WHERE id = {id}")).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Insert_WithoutTenantContext_FailsClosed()
    {
        var executor = BuildExecutor();

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["name"] = "x", ["row_version"] = 1 },
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>()
            .WithMessage("*Tenant context required*");
        (await ScalarAsync("SELECT COUNT(*) FROM orders")).Should().Be("2", "nothing was written");
    }

    [Fact]
    public async Task Insert_WithPositionalPrimaryKey_FailsFast()
    {
        var executor = BuildExecutor();

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "notes",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["body"] = "x" },
            PrimaryKey = new object?[] { 99 },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>()
            .WithMessage("*PrimaryKey is not valid for an insert*");
    }

    // ---- update: concurrency + audit ------------------------------------

    [Fact]
    public async Task Update_WithCurrentToken_Writes_BumpsVersion_AndStampsUpdatedAt()
    {
        var executor = BuildExecutor();

        var result = await executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["name"] = "updated-via-intent", ["row_version"] = 5 },
            PrimaryKey = new object?[] { 10 },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        // Single-key table: the GraphQL update field returns the key value.
        Convert.ToInt64(result.Value).Should().Be(10L);
        (await ScalarAsync("SELECT name FROM orders WHERE id = 10")).Should().Be("updated-via-intent");
        (await ScalarAsync("SELECT row_version FROM orders WHERE id = 10")).Should().Be("6", "the concurrency token bumps on every write");
        (await ScalarAsync("SELECT updated_at FROM orders WHERE id = 10")).Should().NotBeNullOrEmpty();
        (await ScalarAsync("SELECT created_at FROM orders WHERE id = 10")).Should().BeNullOrEmpty("created-on stamps on INSERT only");
    }

    [Fact]
    public async Task Update_WithStaleToken_ConflictsAndLeavesRowUntouched()
    {
        var executor = BuildExecutor();

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["name"] = "stomp", ["row_version"] = 4 },
            PrimaryKey = new object?[] { 10 },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        (await act.Should().ThrowAsync<BifrostExecutionError>()
            .WithMessage("*concurrency token no longer matches*"))
            .Which.ErrorCode.Should().Be("CONFLICT");
        (await ScalarAsync("SELECT name FROM orders WHERE id = 10")).Should().Be("tenant-one-order");
        (await ScalarAsync("SELECT row_version FROM orders WHERE id = 10")).Should().Be("5");
    }

    [Fact]
    public async Task Update_WithoutToken_IsRejected()
    {
        var executor = BuildExecutor();

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["name"] = "no-token" },
            PrimaryKey = new object?[] { 10 },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>()
            .WithMessage("*must include the concurrency token*");
    }

    // ---- update/delete: tenant isolation ---------------------------------

    [Fact]
    public async Task Update_CrossTenant_IsNoOp_RowUntouched()
    {
        var executor = BuildExecutor();

        // Tenant 2 addresses tenant 1's note. The tenant transformer ANDs
        // tenant_id = 2 onto the WHERE, so no row matches — a silent no-op,
        // exactly like the GraphQL path (no token on notes, so no CONFLICT).
        await executor.ExecuteAsync(new MutationIntent
        {
            Table = "notes",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["body"] = "hijacked" },
            PrimaryKey = new object?[] { 1 },
            UserContext = TenantContext(2),
            Endpoint = EndpointPath,
        });

        (await ScalarAsync("SELECT body FROM notes WHERE id = 1")).Should().Be("tenant-one-note");
    }

    [Fact]
    public async Task Delete_CrossTenant_IsNoOp_RowRemains()
    {
        var executor = BuildExecutor();

        var result = await executor.ExecuteAsync(new MutationIntent
        {
            Table = "notes",
            Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?>(),
            PrimaryKey = new object?[] { 1 },
            UserContext = TenantContext(2),
            Endpoint = EndpointPath,
        });

        result.Value.Should().Be(0, "the tenant scope matched no rows");
        (await ScalarAsync("SELECT COUNT(*) FROM notes WHERE id = 1")).Should().Be("1");
    }

    [Fact]
    public async Task Delete_OwnTenantRow_Deletes()
    {
        var executor = BuildExecutor();

        var result = await executor.ExecuteAsync(new MutationIntent
        {
            Table = "notes",
            Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?>(),
            PrimaryKey = new object?[] { 1 },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        result.Value.Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM notes WHERE id = 1")).Should().Be("0");
    }

    // ---- fail fast --------------------------------------------------------

    [Fact]
    public async Task UnknownTable_FailsFast()
    {
        var executor = BuildExecutor();

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "no_such_table",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["name"] = "x" },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task UnknownEndpoint_FailsFast()
    {
        var executor = BuildExecutor();

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "notes",
            Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?>(),
            PrimaryKey = new object?[] { 1 },
            UserContext = TenantContext(1),
            Endpoint = "/nope",
        });

        await act.Should().ThrowAsync<BifrostExecutionError>()
            .WithMessage("*Unknown BifrostQL endpoint*");
    }
}
