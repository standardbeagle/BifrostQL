using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.Execution;
using GraphQL.Types;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Integration.Test;

/// <summary>
/// End-to-end coverage for the server-side authorization policy engine
/// (sub-task 4/4). Proves that a direct GraphQL request — query or mutation —
/// executed through the real <see cref="DocumentExecuter"/> entry point cannot
/// bypass the policy: enforcement lives in the query/mutation pipeline, not in
/// the UI. The four parent scenarios are covered at the outermost layer:
/// table deny, column deny, row-scope deny, and admin allow.
///
/// The query path runs <see cref="PolicyFilterTransformer"/> through a real
/// <see cref="QueryTransformerService"/> (table read-deny and row-scope as an
/// additional filter; column read-deny through the
/// <see cref="IColumnReadGuard"/> seam wired into the transformer service). The
/// mutation path runs <see cref="PolicyMutationTransformer"/> through a real
/// <see cref="MutationTransformersWrap"/>.
///
/// Self-contained: a shared-cache in-memory SQLite database loaded via
/// <see cref="DbModelLoader"/>. It does not depend on the integration suite's
/// environment-gated database infrastructure, so it passes on its own.
/// </summary>
[Collection("PolicyEndToEnd")]
public sealed class PolicyEndToEndTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private IDbModel _model = null!;
    private ISchema _schema = null!;

    // Two policy-governed tables. SQLite tables load under the "main" schema
    // (SqliteSchemaReader), so the metadata selector is "main.<Table>".
    //
    //   Documents — read denied entirely (no policy-actions grant): every read
    //     and write is rejected unless the caller is an admin.
    //   Orders    — read + update permitted; the "secret" column is read-denied
    //     and write-denied; row scope (added below as a raw metadata value)
    //     limits non-admins to their own tenant's rows.
    //
    // The row-scope expression (carrying '{placeholder}' braces) is applied
    // directly to the loaded table's mutable metadata in InitializeAsync, the
    // same seam DbModel.ApplyAdditionalMetadata uses.
    private static readonly string[] PolicyMetadata =
    {
        "main.Documents { policy-read-deny: body }",
        "main.Orders { policy-actions: read,update }",
        "main.Orders { policy-read-deny: secret }",
        "main.Orders { policy-write-deny: secret }",
    };

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_policy_e2e_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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
                );
                CREATE TABLE Documents (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT NOT NULL,
                    body TEXT NOT NULL
                );", conn);
            await ddl.ExecuteNonQueryAsync();

            var seed = new SqliteCommand(
                @"INSERT INTO Orders (tenant_id, total, secret) VALUES (1, 10.0, 'a'), (2, 20.0, 'b');
                  INSERT INTO Documents (title, body) VALUES ('public', 'classified');", conn);
            await seed.ExecuteNonQueryAsync();
        }

        var metadataLoader = new MetadataLoader(PolicyMetadata);
        var loader = new DbModelLoader(_connFactory, metadataLoader);
        _model = await loader.LoadAsync();

        // Row scope: '{ }' is reserved in the rule grammar, so the expression is
        // applied directly to the loaded table's metadata.
        _model.GetTableFromDbName("Orders")
            .Metadata[MetadataKeys.Policy.RowScope] = "tenant_id = {tenant_id}";

        _schema = DbSchema.FromModel(_model);
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    /// <summary>
    /// Executes a GraphQL document through the real <see cref="DocumentExecuter"/>
    /// with both policy transformers wired into the request pipeline — the same
    /// path a direct GraphQL request takes through the server.
    /// </summary>
    private async Task<ExecutionResult> ExecuteAsync(string query, IDictionary<string, object?> userContext)
    {
        var filterTransformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new PolicyFilterTransformer() },
        };

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new PolicyMutationTransformer() },
        });
        services.AddSingleton<IFilterTransformers>(filterTransformers);
        await using var provider = services.BuildServiceProvider();

        var transformerService = new QueryTransformerService(filterTransformers);
        var executor = new SqlExecutionManager(_model, _schema, transformerService);
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

    // ---- Table deny: a read-denied table cannot be queried ----

    [Fact]
    public async Task Query_TableReadDenied_ReturnsGenericError()
    {
        var result = await ExecuteAsync(
            "query { documents { data { id title } } }",
            User("user", tenantId: 1));

        result.Errors.Should().NotBeNullOrEmpty();
        var message = result.Errors!.Single().Message;
        message.Should().Be("Access denied by authorization policy.");
        message.Should().NotContainAny("Documents", "documents", "body", "title");
    }

    // ---- Column deny: a query selecting a read-denied column is rejected ----

    [Fact]
    public async Task Query_SelectsReadDeniedColumn_ReturnsGenericError()
    {
        var result = await ExecuteAsync(
            "query { orders { data { id total secret } } }",
            User("user", tenantId: 1));

        result.Errors.Should().NotBeNullOrEmpty();
        var message = result.Errors!.Single().Message;
        message.Should().Be("The query references a field that is not permitted by authorization policy.");
        message.Should().NotContainAny("secret", "Orders", "orders");
    }

    [Fact]
    public async Task Query_SelectsOnlyAllowedColumns_Succeeds()
    {
        var result = await ExecuteAsync(
            "query { orders(filter: { tenant_id: { _eq: 1 } }) { data { id total } } }",
            User("user", tenantId: 1));

        result.Errors.Should().BeNullOrEmpty();
    }

    // ---- Row-scope deny: a non-admin only sees their own tenant's rows ----

    [Fact]
    public async Task Query_RowScope_RestrictsResultsToCallerTenant()
    {
        var result = await ExecuteAsync(
            "query { orders { data { id total } } }",
            User("user", tenantId: 1));

        result.Errors.Should().BeNullOrEmpty();
        Rows(result, "orders").Should().ContainSingle(
            "row scope limits a non-admin to their own tenant's rows");
    }

    /// <summary>
    /// Extracts the row list from a top-level paged field's <c>data</c> envelope.
    /// <see cref="ExecutionResult.Data"/> is the execution-node tree;
    /// <see cref="ExecutionNode.ToValue"/> materializes it into the plain
    /// dictionary/list structure the GraphQL response is serialized from.
    /// </summary>
    private static IEnumerable<object?> Rows(ExecutionResult result, string field)
    {
        var root = ((ExecutionNode)result.Data!).ToValue() as IDictionary<string, object?>;
        var paged = root![field] as IDictionary<string, object?>;
        return (paged!["data"] as IEnumerable<object?>)!;
    }

    // ---- Mutation path is enforced end-to-end too ----

    [Fact]
    public async Task Mutation_TableActionDenied_ReturnsGenericError()
    {
        var result = await ExecuteAsync(
            "mutation { orders(delete: { id: 1 }) }",
            User("user", tenantId: 1));

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Single().Message.Should().Be("Access denied by authorization policy.");
    }

    [Fact]
    public async Task Mutation_WritesDeniedColumn_ReturnsGenericError()
    {
        var result = await ExecuteAsync(
            "mutation { orders(update: { id: 1, tenant_id: 1, total: 10.0, secret: \"leak\" }) }",
            User("user", tenantId: 1));

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Single().Message.Should()
            .Be("The mutation writes a field that is not permitted by authorization policy.");
    }

    // ---- Admin allow: an admin passes all four scenarios end-to-end ----

    [Fact]
    public async Task Admin_PassesAllFourScenariosEndToEnd()
    {
        var admin = User("admin", tenantId: 1);

        // 1. Table deny — admin reads the read-denied table.
        var tableResult = await ExecuteAsync("query { documents { data { id title } } }", admin);
        tableResult.Errors.Should().BeNullOrEmpty("an admin bypasses table read-deny");

        // 2. Column deny — admin selects the read-denied column.
        var columnResult = await ExecuteAsync("query { orders { data { id total secret } } }", admin);
        columnResult.Errors.Should().BeNullOrEmpty("an admin bypasses column read-deny");

        // 3. Row-scope deny — admin sees rows across every tenant.
        var rowScopeResult = await ExecuteAsync("query { orders { data { id total } } }", admin);
        rowScopeResult.Errors.Should().BeNullOrEmpty();
        Rows(rowScopeResult, "orders").Should().HaveCount(2,
            "an admin is not narrowed by the row-scope filter");

        // 4. Action deny — admin performs the denied delete.
        var mutationResult = await ExecuteAsync("mutation { orders(delete: { id: 1 }) }", admin);
        mutationResult.Errors.Should().BeNullOrEmpty("an admin bypasses mutation action-deny");
    }
}
