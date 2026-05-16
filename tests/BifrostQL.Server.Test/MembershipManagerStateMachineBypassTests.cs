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

[Collection("MembershipManagerStateMachineBypass")]
public sealed class MembershipManagerStateMachineBypassTests : IAsyncLifetime
{
    private const string GraphQlPath = "/graphql";

    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private IDbModel _model = null!;
    private ISchema _schema = null!;

    private static readonly string[] Metadata =
    {
        "main.members { policy-actions: read,create,update,delete }",
        "main.audit_log { policy-actions: read }",
        "main.members { state-column: status; initial-state: pending; states: pending,active,inactive,deceased; transitions: pending->active[officer,admin]@member.activated|active->inactive[officer,admin]@member.inactivated|inactive->active[admin]@member.reactivated|active->deceased[officer,admin]@member.deceased|inactive->deceased[officer,admin]@member.deceased }",
    };

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_mm_state_machine_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();

        _connFactory = new SqliteDbConnFactory(_connectionString);

        await using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync();
            var ddl = new SqliteCommand(
                @"CREATE TABLE members (
                    member_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tenant_id INTEGER NOT NULL,
                    user_id INTEGER,
                    household_id INTEGER,
                    first_name TEXT NOT NULL,
                    last_name TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'pending'
                );
                CREATE TABLE audit_log (
                    audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tenant_id INTEGER NOT NULL,
                    actor_user_id INTEGER,
                    action TEXT NOT NULL,
                    entity_type TEXT NOT NULL,
                    entity_id TEXT NOT NULL,
                    summary TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now'))
                );", conn);
            await ddl.ExecuteNonQueryAsync();

            var seed = new SqliteCommand(
                @"INSERT INTO members (member_id, tenant_id, user_id, household_id, first_name, last_name, status)
                    VALUES
                    (1, 1, 42, 1, 'Ada', 'Lovelace', 'pending'),
                    (2, 1, 99, 2, 'Grace', 'Hopper', 'inactive');", conn);
            await seed.ExecuteNonQueryAsync();
        }

        var metadataLoader = new MetadataLoader(Metadata);
        var loader = new DbModelLoader(_connFactory, metadataLoader);
        _model = await loader.LoadAsync();
        _schema = DbSchema.FromModel(_model);
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    [Fact]
    public async Task DirectGraphQl_CannotSkipFromPendingToDeceased()
    {
        var response = await ExecuteAsync(
            "mutation { members(update: { member_id: 1, tenant_id: 1, user_id: 42, household_id: 1, first_name: \"Ada\", last_name: \"Lovelace\", status: \"deceased\" }) }",
            role: "officer", userId: 7, tenantId: 1);

        response.Errors.Should().ContainSingle();
        response.Errors[0].Should().Be("State transition is not permitted.");
        (await AuditCountAsync()).Should().Be(0, "rejected transitions do not emit audit rows");
    }

    [Fact]
    public async Task MemberRole_CannotActivatePendingMember()
    {
        var response = await ExecuteAsync(
            "mutation { members(update: { member_id: 1, tenant_id: 1, user_id: 42, household_id: 1, first_name: \"Ada\", last_name: \"Lovelace\", status: \"active\" }) }",
            role: "member", userId: 42, tenantId: 1);

        response.Errors.Should().ContainSingle();
        response.Errors[0].Should().Be("State transition is not permitted.");
        (await MemberStatusAsync(1)).Should().Be("pending");
    }

    [Fact]
    public async Task AdminBypass_AllowsRestrictedTransitionAndWritesAuditRow()
    {
        var response = await ExecuteAsync(
            "mutation { members(update: { member_id: 2, tenant_id: 1, user_id: 99, household_id: 2, first_name: \"Grace\", last_name: \"Hopper\", status: \"active\" }) }",
            role: "admin", userId: 1, tenantId: 1);

        response.Errors.Should().BeEmpty(response.RawJson);
        (await MemberStatusAsync(2)).Should().Be("active");

        var audit = await FirstAuditAsync();
        audit.Action.Should().Be("members.inactive->active");
        audit.EntityType.Should().Be("members");
        audit.EntityId.Should().Be("2");
        audit.ActorUserId.Should().Be(1);
        audit.Summary.Should().Be("member.reactivated");
    }

    private ServiceProvider BuildRequestServices()
    {
        var filterTransformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new PolicyFilterTransformer() },
        };

        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(GraphQlPath, () => new Inputs(new Dictionary<string, object?>
        {
            { "connFactory", _connFactory },
            { "model", _model },
            { "dbSchema", _schema },
        }));

        var services = new ServiceCollection();
        services.AddSingleton<IFilterTransformers>(filterTransformers);
        services.AddSingleton<IMutationModules>(new ModulesWrap { Modules = Array.Empty<IMutationModule>() });
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new PolicyMutationTransformer(),
                new StateMachineMutationTransformer(),
            },
        });
        services.AddSingleton<IQueryTransformerService>(new QueryTransformerService(filterTransformers));
        services.AddSingleton(pathCache);
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddSingleton<IDocumentExecuter, DocumentExecuter>();
        services.AddSingleton<IBifrostWorkflowExecutor>(sp =>
            new BifrostWorkflowExecutor(
                sp.GetRequiredService<IDocumentExecuter>(),
                sp.GetRequiredService<PathCache<Inputs>>(),
                sp));
        services.AddSingleton(sp =>
            new StateTransitionObservers(new IStateTransitionObserver[]
            {
                new StateTransitionAuditObserver(sp.GetRequiredService<IBifrostWorkflowExecutor>()),
            }));
        return services.BuildServiceProvider();
    }

    private async Task<GraphQlResponse> ExecuteAsync(string query, string role, int userId, int tenantId)
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
        context.Request.ContentType = "application/json";
        var body = JsonSerializer.Serialize(new { query });
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.User = BuildPrincipal(role, userId, tenantId);
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(200, "GraphQL responses are always HTTP 200");
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return GraphQlResponse.Parse(await reader.ReadToEndAsync());
    }

    private static ClaimsPrincipal BuildPrincipal(string role, int userId, int tenantId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(LocalAuthClaims.Provider, "local"),
            new(LocalAuthClaims.Tenant, tenantId.ToString()),
            new(ClaimTypes.Role, role),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private async Task<string> MemberStatusAsync(int memberId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        var command = new SqliteCommand("SELECT status FROM members WHERE member_id = $member_id", conn);
        command.Parameters.AddWithValue("$member_id", memberId);
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"No member row {memberId}."));
    }

    private async Task<int> AuditCountAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        var command = new SqliteCommand("SELECT COUNT(*) FROM audit_log", conn);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<AuditRow> FirstAuditAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        var command = new SqliteCommand(
            @"SELECT action, entity_type, entity_id, actor_user_id, summary
              FROM audit_log
              ORDER BY audit_id
              LIMIT 1", conn);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("No audit row was written.");

        return new AuditRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4));
    }

    private sealed record AuditRow(
        string Action,
        string EntityType,
        string EntityId,
        int ActorUserId,
        string Summary);

    private sealed class GraphQlResponse
    {
        public required IReadOnlyList<string> Errors { get; init; }
        public required string RawJson { get; init; }

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

            return new GraphQlResponse { Errors = errors, RawJson = json };
        }
    }
}
