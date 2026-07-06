using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using GraphQL.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// End-to-end proof that the server-side authorization policy engine cannot be
/// bypassed by a direct GraphQL request (policy engine sub-task 4/4). The system
/// under test is the real <see cref="BifrostHttpMiddleware"/> — the outermost
/// HTTP/GraphQL entry point — driven by a raw POST body and an authenticated
/// <see cref="ClaimsPrincipal"/>, so enforcement is proven to be server-side
/// rather than UI-only.
///
/// Each request runs through the production pipeline: the middleware reads the
/// GraphQL body, builds the user context from the principal's claims, resolves
/// the cached schema, and executes through <see cref="BifrostDocumentExecutor"/>
/// with <see cref="PolicyFilterTransformer"/> and
/// <see cref="PolicyMutationTransformer"/> wired into the request services. The
/// four parent scenarios are covered — table deny, column deny, row-scope deny,
/// and admin allow — for both a query and a mutation.
///
/// Self-contained: a shared-cache in-memory SQLite database loaded via
/// <see cref="DbModelLoader"/>; it has no dependency on external infrastructure.
/// </summary>
[Collection("PolicyBypassPrevention")]
public sealed class PolicyBypassPreventionTests : IAsyncLifetime
{
    private const string GraphQlPath = "/graphql";

    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private IDbModel _model = null!;
    private ISchema _schema = null!;
    private ProfileModelCache _profileCache = null!;
    private BifrostProfileRegistry _profileRegistry = null!;

    // The "policy" profile activates the built-in policy transformers. Under the
    // per-profile model the empty default profile runs raw (no opt-in modules), so
    // these end-to-end policy proofs select a profile that lists the policy module.
    private const string ProfileName = "policy";

    // Documents is read-denied entirely; Orders permits read + update, denies
    // the "secret" column for read and write, and scopes non-admin rows by
    // tenant. The row-scope expression (carrying '{placeholder}' braces) is
    // applied directly to the loaded table's metadata to keep setup explicit.
    private static readonly string[] PolicyMetadata =
    {
        "main.Documents { policy-read-deny: body }",
        "main.Orders { policy-actions: read,update }",
        "main.Orders { policy-read-deny: secret }",
        "main.Orders { policy-write-deny: secret }",
    };

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_policy_bypass_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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

        _profileRegistry = new BifrostProfileRegistry();
        _profileRegistry.Add(new BifrostProfile { Name = ProfileName, Modules = new[] { "policy" } });

        // The middleware now resolves the model+schema per profile from a shared DB
        // read. Build that cache once, then apply the row-scope expression directly
        // to the cached profile model ('{ }' is reserved in the rule grammar).
        var read = await loader.ReadAsync();
        _profileCache = new ProfileModelCache(loader, read, PolicyMetadata, null, _profileRegistry);
        (_model, _schema) = _profileCache.GetFor(ProfileName);
        _model.GetTableFromDbName("Orders")
            .Metadata[MetadataKeys.Policy.RowScope] = "tenant_id = {tenant_id}";
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    /// <summary>
    /// Builds a request service provider with the policy transformers wired in,
    /// exactly as the production host configures them, plus the schema cache the
    /// middleware reads.
    /// </summary>
    private ServiceProvider BuildRequestServices()
    {
        var filterTransformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new PolicyFilterTransformer() },
        };

        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(GraphQlPath, () => Task.FromResult(new Inputs(new Dictionary<string, object?>
        {
            { "connFactory", _connFactory },
            { "model", _model },
            { "dbSchema", _schema },
            { "profileModelCache", _profileCache },
        })));

        var services = new ServiceCollection();
        services.AddSingleton<IFilterTransformers>(filterTransformers);
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new PolicyMutationTransformer() },
        });
        services.AddSingleton<IQueryTransformerService>(new QueryTransformerService(filterTransformers));
        services.AddSingleton(pathCache);
        services.AddSingleton(_profileRegistry);
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Drives <see cref="BifrostHttpMiddleware"/> with a raw GraphQL POST body
    /// under an authenticated principal, and returns the deserialized response.
    /// </summary>
    private async Task<GraphQlResponse> ExecuteAsync(string query, string role, int tenantId)
    {
        var serializer = new GraphQLSerializer();
        var middleware = new BifrostHttpMiddleware(
            next: _ => Task.CompletedTask,
            serializer: serializer,
            documentExecutor: new DocumentExecuter(),
            logger: NullLogger<BifrostHttpMiddleware>.Instance);

        await using var provider = BuildRequestServices();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.RequestServices.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

        context.Request.Method = HttpMethods.Post;
        context.Request.Path = GraphQlPath;
        context.Request.QueryString = new QueryString($"?profile={ProfileName}");
        context.Request.ContentType = "application/json";
        var body = JsonSerializer.Serialize(new { query });
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        context.User = BuildPrincipal(role, tenantId);
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(200, "GraphQL responses are always HTTP 200");
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var json = await reader.ReadToEndAsync();
        return GraphQlResponse.Parse(json);
    }

    private static ClaimsPrincipal BuildPrincipal(string role, int tenantId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "user-1"),
            new(LocalAuthClaims.Provider, "local"),
            new(LocalAuthClaims.Tenant, tenantId.ToString()),
            new(ClaimTypes.Role, role),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    // ---- Table deny ----

    [Fact]
    public async Task DirectQuery_ReadDeniedTable_RejectedWithGenericError()
    {
        var response = await ExecuteAsync("query { documents { data { id title } } }", role: "user", tenantId: 1);

        response.Errors.Should().ContainSingle();
        response.Errors[0].Should().Be("Access denied by authorization policy.");
        response.Errors[0].Should().NotContainAny("Documents", "documents", "body");
    }

    // ---- Column deny ----

    [Fact]
    public async Task DirectQuery_ReadDeniedColumn_RejectedWithGenericError()
    {
        var response = await ExecuteAsync("query { orders { data { id total secret } } }", role: "user", tenantId: 1);

        response.Errors.Should().ContainSingle();
        response.Errors[0].Should().Be("The query references a field that is not permitted by authorization policy.");
        response.Errors[0].Should().NotContainAny("secret", "Orders", "orders");
    }

    // ---- Row-scope deny ----

    [Fact]
    public async Task DirectQuery_RowScope_RestrictsResultsToCallerTenant()
    {
        var response = await ExecuteAsync("query { orders { data { id total } } }", role: "user", tenantId: 1);

        response.Errors.Should().BeEmpty();
        response.OrderCount("orders").Should().Be(1, "row scope limits a non-admin to their own tenant's rows");
    }

    // ---- Mutation is enforced through the same entry point ----

    [Fact]
    public async Task DirectMutation_WriteDeniedColumn_RejectedWithGenericError()
    {
        var response = await ExecuteAsync(
            "mutation { orders(update: { id: 1, tenant_id: 1, total: 10.0, secret: \"leak\" }) }",
            role: "user", tenantId: 1);

        response.Errors.Should().ContainSingle();
        response.Errors[0].Should()
            .Be("The mutation writes a field that is not permitted by authorization policy.");
    }

    [Fact]
    public async Task DirectMutation_DeniedAction_RejectedWithGenericError()
    {
        var response = await ExecuteAsync(
            "mutation { orders(delete: { id: 1 }) }", role: "user", tenantId: 1);

        response.Errors.Should().ContainSingle();
        response.Errors[0].Should().Be("Access denied by authorization policy.");
    }

    // ---- Admin allow: passes all four scenarios through the entry point ----

    [Fact]
    public async Task DirectRequests_AdminRole_PassAllFourScenarios()
    {
        // 1. Table deny — admin reads the read-denied table.
        var tableResponse = await ExecuteAsync("query { documents { data { id title } } }", role: "admin", tenantId: 1);
        tableResponse.Errors.Should().BeEmpty("an admin bypasses table read-deny");

        // 2. Column deny — admin selects the read-denied column.
        var columnResponse = await ExecuteAsync("query { orders { data { id total secret } } }", role: "admin", tenantId: 1);
        columnResponse.Errors.Should().BeEmpty("an admin bypasses column read-deny");

        // 3. Row-scope deny — admin sees rows across every tenant.
        var rowScopeResponse = await ExecuteAsync("query { orders { data { id total } } }", role: "admin", tenantId: 1);
        rowScopeResponse.Errors.Should().BeEmpty();
        rowScopeResponse.OrderCount("orders").Should().Be(2, "an admin is not narrowed by the row-scope filter");

        // 4. Action deny — admin performs the denied delete.
        var mutationResponse = await ExecuteAsync(
            "mutation { orders(delete: { id: 1 }) }", role: "admin", tenantId: 1);
        mutationResponse.Errors.Should().BeEmpty("an admin bypasses mutation action-deny");
    }

    /// <summary>
    /// Minimal reader over the GraphQL JSON response envelope — just the error
    /// messages and, for the row-scope assertions, the count of rows returned
    /// for a top-level field.
    /// </summary>
    private sealed class GraphQlResponse
    {
        public required IReadOnlyList<string> Errors { get; init; }
        private JsonElement _data;

        public static GraphQlResponse Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var errors = new List<string>();
            if (root.TryGetProperty("errors", out var errorsElement)
                && errorsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errorsElement.EnumerateArray())
                    if (error.TryGetProperty("message", out var message))
                        errors.Add(message.GetString() ?? string.Empty);
            }

            var response = new GraphQlResponse { Errors = errors };
            if (root.TryGetProperty("data", out var dataElement))
                response._data = dataElement.Clone();
            return response;
        }

        /// <summary>
        /// Count of rows returned for a top-level paged field. The field carries
        /// the <c>{table}_paged</c> envelope, so the rows live under its
        /// <c>data</c> property.
        /// </summary>
        public int OrderCount(string field)
        {
            if (_data.ValueKind != JsonValueKind.Object
                || !_data.TryGetProperty(field, out var fieldElement)
                || fieldElement.ValueKind != JsonValueKind.Object
                || !fieldElement.TryGetProperty("data", out var rows)
                || rows.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"Response has no paged field '{field}'.");
            }

            return rows.GetArrayLength();
        }
    }
}
