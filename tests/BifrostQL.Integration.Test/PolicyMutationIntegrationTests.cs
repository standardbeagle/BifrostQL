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
/// Resolver-path integration coverage for <see cref="PolicyMutationTransformer"/>
/// (sub-task 3/4). Verifies the transformer is actually invoked by the mutation
/// resolvers (<see cref="DbTableMutateResolver"/>) — not just unit-testable in
/// isolation — so an unauthorized create/update/delete or a write-denied column
/// surfaces as a GraphQL error and the database is left untouched.
///
/// Self-contained: a shared-cache in-memory SQLite database, schema loaded via
/// <see cref="DbModelLoader"/> with policy metadata, and the transformer wired
/// into a real <see cref="MutationTransformersWrap"/> in the request service
/// provider. It does not depend on the integration suite's environment-gated
/// database infrastructure.
/// </summary>
[Collection("PolicyMutationIntegration")]
public sealed class PolicyMutationIntegrationTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private IDbModel _model = null!;
    private ISchema _schema = null!;

    // Policy: Orders permits read + update only (no create, no delete);
    // the "secret" column is write-denied. Row-scope compilation is exercised
    // exhaustively by the unit tests, and
    // the resolver SQL path does not yet consume MutationTransformResult
    // .AdditionalFilter (a pre-existing gap shared with soft-delete), so the
    // resolver-path coverage here is the action-deny and column-write-deny
    // enforcement that does reach the database.
    // SQLite tables load under the "main" schema (SqliteSchemaReader), so the
    // metadata selector is "main.Orders".
    private static readonly string[] PolicyMetadata =
    {
        "main.Orders { policy-actions: read,update }",
        "main.Orders { policy-write-deny: secret }",
    };

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_policy_mut_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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
                    total REAL NOT NULL,
                    secret TEXT NULL
                )", conn);
            await ddl.ExecuteNonQueryAsync();

            var seed = new SqliteCommand(
                "INSERT INTO Orders (tenant_id, total, secret) VALUES (1, 10.0, 'a'), (2, 20.0, 'b')", conn);
            await seed.ExecuteNonQueryAsync();
        }

        var metadataLoader = new MetadataLoader(PolicyMetadata);
        var loader = new DbModelLoader(_connFactory, metadataLoader);
        _model = await loader.LoadAsync();
        _schema = DbSchema.FromModel(_model);
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    /// <summary>
    /// Executes a GraphQL document against the loaded schema with the policy
    /// mutation transformer registered, under the supplied user context.
    /// </summary>
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

    private long CountOrders()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = new SqliteCommand("SELECT COUNT(*) FROM Orders", conn);
        return (long)cmd.ExecuteScalar()!;
    }

    // ---- Table action deny: create is not permitted ----

    [Fact]
    public async Task Create_NotPermitted_ReturnsErrorAndDoesNotInsert()
    {
        var before = CountOrders();

        var result = await ExecuteAsync(
            "mutation { orders(insert: { tenant_id: 1, total: 99.0 }) }",
            User("user", tenantId: 1));

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Single().Message.Should().Be("Access denied by authorization policy.");
        CountOrders().Should().Be(before, "the rejected insert must not reach the database");
    }

    // ---- Table action deny: delete is not permitted ----

    [Fact]
    public async Task Delete_NotPermitted_ReturnsErrorAndDoesNotDelete()
    {
        var before = CountOrders();

        var result = await ExecuteAsync(
            "mutation { orders(delete: { id: 1 }) }",
            User("user", tenantId: 1));

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Single().Message.Should().Be("Access denied by authorization policy.");
        CountOrders().Should().Be(before, "the rejected delete must not reach the database");
    }

    // ---- Column write-deny on a permitted action ----
    // The generated update input type makes every NOT NULL column required, so
    // tenant_id and total are always supplied; the denied column under test is
    // the nullable "secret".

    [Fact]
    public async Task Update_WritesDeniedColumn_ReturnsError()
    {
        var result = await ExecuteAsync(
            "mutation { orders(update: { id: 1, tenant_id: 1, total: 10.0, secret: \"leak\" }) }",
            User("user", tenantId: 1));

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Single().Message.Should()
            .Be("The mutation writes a field that is not permitted by authorization policy.");
    }

    // ---- Permitted action with allowed columns succeeds ----

    [Fact]
    public async Task Update_PermittedActionAndAllowedColumns_Succeeds()
    {
        var result = await ExecuteAsync(
            "mutation { orders(update: { id: 1, tenant_id: 1, total: 55.5 }) }",
            User("user", tenantId: 1));

        result.Errors.Should().BeNullOrEmpty();
    }

    // ---- Admin bypasses the mutation-path policy ----

    [Fact]
    public async Task Create_AdminRole_BypassesPolicyAndInserts()
    {
        var before = CountOrders();

        var result = await ExecuteAsync(
            "mutation { orders(insert: { tenant_id: 1, total: 99.0 }) }",
            User("admin", tenantId: 1));

        result.Errors.Should().BeNullOrEmpty();
        CountOrders().Should().Be(before + 1, "an admin create bypasses the policy");
    }
}
