using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Server.OData;
using BifrostQL.Server.Test.OData;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using GraphQL.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// End-to-end proof that an <see cref="BifrostQL.Core.Auth.IGrantResolver"/> registered via
/// <c>AddBifrostGrantResolver</c> feeds the SAME per-request user context on the GraphQL
/// front door and on a non-GraphQL adapter (OData) — both assemble identity through the
/// shared <see cref="IBifrostAuthContextFactory"/> (E17), so a grant the resolver returns
/// is visible to the policy engine on both wires (S2).
///
/// Policy shapes used:
///   <c>docs</c>  — readable, but column <c>salary</c> is read-denied to callers holding
///                  grant <c>x</c> (<c>policy-read-deny-roles: x</c>): the deny only fires
///                  when the caller is seen as HOLDING the grant.
///   <c>vault</c> — <c>policy-actions: create</c>: reads are denied to every non-admin, so
///                  the grant <c>admin</c> (the evaluator's admin role) is REQUIRED to read.
///
/// The throwing-resolver fact proves the E9 fail-closed half: a resolver fault empties the
/// permission set, logs a Warning naming the identity id, and the request ends in a policy
/// deny (GraphQL error envelope), never a 500.
/// </summary>
[Collection("GrantResolverWiring")]
public sealed class GrantResolverWiringTests : IAsyncLifetime
{
    private const string GraphQlPath = "/graphql";
    private const string ProfileName = "policy";

    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private IDbModel _model = null!;
    private ISchema _schema = null!;
    private ProfileModelCache _profileCache = null!;
    private BifrostProfileRegistry _profileRegistry = null!;

    private static readonly string[] PolicyMetadata =
    {
        "main.docs { policy-actions: read }",
        "main.docs { policy-read-deny: salary }",
        "main.docs { policy-read-deny-roles: x }",
        "main.vault { policy-actions: create }",
    };

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_grant_wiring_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
        _connFactory = new SqliteDbConnFactory(_connectionString);

        await using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync();
            var cmd = new SqliteCommand(
                @"CREATE TABLE docs (Id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, salary TEXT NULL);
                  CREATE TABLE vault (Id INTEGER PRIMARY KEY AUTOINCREMENT, secret TEXT NOT NULL);
                  INSERT INTO docs (title, salary) VALUES ('a', '100');
                  INSERT INTO vault (secret) VALUES ('s3cret');", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var metadataLoader = new MetadataLoader(PolicyMetadata);
        var loader = new DbModelLoader(_connFactory, metadataLoader);
        _profileRegistry = new BifrostProfileRegistry();
        _profileRegistry.Add(new BifrostProfile { Name = ProfileName, Modules = new[] { "policy" } });
        var read = await loader.ReadAsync();
        _profileCache = new ProfileModelCache(loader, read, PolicyMetadata, null, _profileRegistry);
        (_model, _schema) = _profileCache.GetFor(ProfileName);
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    private ServiceProvider BuildRequestServices(
        Func<BifrostQL.Core.Auth.AppIdentity, IServiceProvider, CancellationToken, ValueTask<IReadOnlyCollection<string>>>? resolver,
        ListLoggerFactory? loggerFactory = null)
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
        if (loggerFactory != null)
            services.AddSingleton<ILoggerFactory>(loggerFactory);
        if (resolver != null)
            services.AddBifrostGrantResolver(resolver);
        return services.BuildServiceProvider();
    }

    private async Task<GraphQlResponse> ExecuteGraphQlAsync(
        string query,
        Func<BifrostQL.Core.Auth.AppIdentity, IServiceProvider, CancellationToken, ValueTask<IReadOnlyCollection<string>>>? resolver,
        ListLoggerFactory? loggerFactory = null)
    {
        var middleware = new BifrostHttpMiddleware(
            next: _ => Task.CompletedTask,
            serializer: new GraphQLSerializer(),
            documentExecutor: new DocumentExecuter(),
            logger: NullLogger<BifrostHttpMiddleware>.Instance);

        await using var provider = BuildRequestServices(resolver, loggerFactory);
        var context = new DefaultHttpContext { RequestServices = provider };
        context.RequestServices.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

        context.Request.Method = HttpMethods.Post;
        context.Request.Path = GraphQlPath;
        context.Request.QueryString = new QueryString($"?profile={ProfileName}");
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { query })));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(BifrostQL.Server.Auth.LocalAuthClaims.Provider, "local"),
        }, authenticationType: "Test"));
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(200, "GraphQL responses are always HTTP 200");
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return GraphQlResponse.Parse(json);
    }

    private static ValueTask<IReadOnlyCollection<string>> Grants(params string[] grants)
        => new(grants);

    // ---- GraphQL front door ----

    [Fact]
    public async Task GraphQl_ResolverGrantsX_PolicyNamingXSeesCallerHoldingIt()
    {
        var response = await ExecuteGraphQlAsync(
            "query { docs { data { id salary } } }",
            (identity, sp, ct) => Grants("x"));

        response.Errors.Should().ContainSingle()
            .Which.Should().Be("The query references a field that is not permitted by authorization policy.");
    }

    [Fact]
    public async Task GraphQl_NoGrants_PolicyNamingXDoesNotFire()
    {
        // Control: without the grant the qualified deny does not apply, so the same
        // query succeeds — proving the deny above is driven by the resolver's grant.
        var response = await ExecuteGraphQlAsync(
            "query { docs { data { id salary } } }",
            (identity, sp, ct) => Grants());

        response.Errors.Should().BeEmpty();
        response.SalaryOfFirstDoc().Should().Be("100");
    }

    [Fact]
    public async Task GraphQl_AdminGrant_ReadsTableThatRequiresIt()
    {
        // vault allows only create; reading it requires the admin grant.
        var response = await ExecuteGraphQlAsync(
            "query { vault { data { id } } }",
            (identity, sp, ct) => Grants("admin"));

        response.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task GraphQl_ThrowingResolver_EmptyGrants_PolicyDeny_NotServerError()
    {
        var loggerFactory = new ListLoggerFactory();

        var response = await ExecuteGraphQlAsync(
            "query { vault { data { id } } }",
            (identity, sp, ct) => throw new InvalidOperationException("grant store down"),
            loggerFactory);

        // The deny is the policy engine's answer for a caller with no permissions —
        // carried in the GraphQL error envelope (HTTP 200), never an exception/500.
        response.Errors.Should().ContainSingle()
            .Which.Should().Be("Access denied by authorization policy.");
        loggerFactory.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("user-1"),
            "a resolver fault must log a Warning naming the identity id");
    }

    // ---- OData front door (non-GraphQL adapter, E17: same shared context) ----

    private const string ODataSalaryDenyMetadata = "main.docs { policy-actions: read; policy-read-deny: salary; policy-read-deny-roles: x }";
    private static readonly string[] ODataSeed =
    {
        "CREATE TABLE docs (id INTEGER PRIMARY KEY, title TEXT NOT NULL, salary TEXT);",
        "INSERT INTO docs (title, salary) VALUES ('a', '100');",
    };

    private static async Task<(int Status, string Body)> ExecuteODataAsync(
        Func<BifrostQL.Core.Auth.AppIdentity, IServiceProvider, CancellationToken, ValueTask<IReadOnlyCollection<string>>> resolver)
    {
        await using var harness = await ODataRealDbHarness.StartAsync(
            "grant-wiring", new[] { ODataSalaryDenyMetadata }, ODataSeed);

        var services = new ServiceCollection();
        services.AddBifrostGrantResolver(resolver);
        await using var provider = services.BuildServiceProvider();

        var authenticator = new ODataAuthenticator(BifrostAuthContextFactory.Instance, basicStore: null);
        var middleware = new ODataMiddleware(
            _ => throw new InvalidOperationException("the OData endpoint terminates the request"),
            new ODataOptions { Endpoint = ODataRealDbHarness.EndpointPath },
            authenticator,
            harness.Reads,
            NullLogger<ODataMiddleware>.Instance);

        var ctx = new DefaultHttpContext
        {
            User = ODataTestAuth.Principal(),
            RequestServices = provider,
        };
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/docs";
        ctx.Request.QueryString = new QueryString("?$select=id,salary");
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task OData_ResolverGrantsX_PolicyNamingXSeesCallerHoldingIt()
    {
        // Holding grant x, the salary column is read-denied, hence hidden from the
        // caller-visible EDM; $select naming it is refused exactly as an unknown property.
        var (status, body) = await ExecuteODataAsync((identity, sp, ct) => Grants("x"));

        status.Should().NotBe(200);
        body.Should().Contain("unknown property");
    }

    [Fact]
    public async Task OData_NoGrants_DeniedColumnIsServed()
    {
        // Control: without the grant the qualified deny does not apply and salary is served.
        var (status, body) = await ExecuteODataAsync((identity, sp, ct) => Grants());

        status.Should().Be(200);
        body.Should().Contain("100");
    }

    // ---- helpers ----

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

        public string? SalaryOfFirstDoc()
        {
            return _data.GetProperty("docs").GetProperty("data")[0].GetProperty("salary").GetString();
        }
    }

    private sealed class ListLoggerFactory : ILoggerFactory
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new ListLogger(Entries);
        public void Dispose() { }

        private sealed class ListLogger : ILogger
        {
            private readonly List<(LogLevel, string)> _entries;
            public ListLogger(List<(LogLevel, string)> entries) => _entries = entries;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
