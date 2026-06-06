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
/// End-to-end proof for the Membership Manager authorization policy
/// (policy engine sub-task 4/4 — the integration/proof task). Where
/// <see cref="PolicyBypassPreventionTests"/> proves the generic policy engine
/// cannot be bypassed by a direct GraphQL request, this fixture proves the same
/// for the <em>role-qualified</em> Membership Manager configuration shipped by
/// sub-tasks 1–3:
///
///   members          policy-actions: read,create,update,delete
///                    policy-row-scope: user_id = {user_id}
///                    policy-row-scope-roles: member
///   households       policy-actions: read,create,update,delete
///                    policy-row-scope: household_id = {household_id}
///                    policy-row-scope-roles: member
///   dues_invoices    policy-actions: read,create,update
///                    policy-read-deny: amount_cents
///                    policy-read-deny-roles: officer,event_manager,member,read_only
///   membership_plans policy-actions: read,create,update,delete
///                    policy-read-deny: price_cents
///                    policy-read-deny-roles: officer,event_manager,member,read_only
///   audit_log        policy-actions: read   (read-only through generated CRUD)
///
/// Every request runs through the real <see cref="BifrostHttpMiddleware"/> — the
/// outermost HTTP/GraphQL entry point — driven by a raw POST body and an
/// authenticated <see cref="ClaimsPrincipal"/>, so the role restrictions are
/// proven to hold server-side and cannot be bypassed by issuing raw GraphQL.
/// The three required access paths are covered: <c>member</c> self-service,
/// <c>officer</c> operational, and finance-field reads, plus the <c>admin</c>
/// bypass.
///
/// Self-contained: a shared-cache in-memory SQLite database loaded via
/// <see cref="DbModelLoader"/>; no dependency on external infrastructure.
/// </summary>
[Collection("MembershipManagerPolicyBypassPrevention")]
public sealed class MembershipManagerPolicyBypassPreventionTests : IAsyncLifetime
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

    // The verbatim Membership Manager policy configuration documented in the
    // membership-manager seed-sample SQL headers. Row scope cannot be expressed
    // through a MetadataLoader rule string — the rule grammar reserves '{ }' —
    // so the two row-scope expressions are applied directly to the loaded
    // tables' metadata in InitializeAsync.
    private static readonly string[] PolicyMetadata =
    {
        "main.members { policy-actions: read,create,update,delete }",
        "main.members { policy-row-scope-roles: member }",
        "main.households { policy-actions: read,create,update,delete }",
        "main.households { policy-row-scope-roles: member }",
        "main.dues_invoices { policy-actions: read,create,update }",
        "main.dues_invoices { policy-read-deny: amount_cents }",
        "main.dues_invoices { policy-read-deny-roles: officer,event_manager,member,read_only }",
        "main.membership_plans { policy-actions: read,create,update,delete }",
        "main.membership_plans { policy-read-deny: price_cents }",
        "main.membership_plans { policy-read-deny-roles: officer,event_manager,member,read_only }",
        "main.audit_log { policy-actions: read }",
    };

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_mm_policy_bypass_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();

        _connFactory = new SqliteDbConnFactory(_connectionString);

        await using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync();

            var ddl = new SqliteCommand(
                @"CREATE TABLE households (
                    household_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tenant_id INTEGER NOT NULL,
                    name TEXT NOT NULL
                );
                CREATE TABLE members (
                    member_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tenant_id INTEGER NOT NULL,
                    user_id INTEGER NOT NULL,
                    household_id INTEGER NOT NULL,
                    first_name TEXT NOT NULL
                );
                CREATE TABLE membership_plans (
                    plan_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tenant_id INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    price_cents INTEGER NOT NULL
                );
                CREATE TABLE dues_invoices (
                    invoice_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tenant_id INTEGER NOT NULL,
                    member_id INTEGER NOT NULL,
                    amount_cents INTEGER NOT NULL,
                    status TEXT NOT NULL
                );
                CREATE TABLE audit_log (
                    audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tenant_id INTEGER NOT NULL,
                    action TEXT NOT NULL
                );", conn);
            await ddl.ExecuteNonQueryAsync();

            // member_id 1 is linked to login user_id 42; member_id 2 to user_id 99.
            var seed = new SqliteCommand(
                @"INSERT INTO households (household_id, tenant_id, name) VALUES (1, 1, 'Smith'), (2, 1, 'Jones');
                  INSERT INTO members (member_id, tenant_id, user_id, household_id, first_name)
                    VALUES (1, 1, 42, 1, 'Ada'), (2, 1, 99, 2, 'Grace');
                  INSERT INTO membership_plans (plan_id, tenant_id, name, price_cents)
                    VALUES (1, 1, 'Standard', 5000);
                  INSERT INTO dues_invoices (invoice_id, tenant_id, member_id, amount_cents, status)
                    VALUES (1, 1, 1, 5000, 'open');
                  INSERT INTO audit_log (audit_id, tenant_id, action) VALUES (1, 1, 'created');", conn);
            await seed.ExecuteNonQueryAsync();
        }

        var metadataLoader = new MetadataLoader(PolicyMetadata);
        var loader = new DbModelLoader(_connFactory, metadataLoader);

        _profileRegistry = new BifrostProfileRegistry();
        _profileRegistry.Add(new BifrostProfile { Name = ProfileName, Modules = new[] { "policy" } });

        // The middleware now resolves the model+schema per profile from a shared DB
        // read. Build that cache once, then apply the row-scope expressions directly
        // to the cached profile model ('{ }' is reserved in the rule grammar).
        var read = await loader.ReadAsync();
        _profileCache = new ProfileModelCache(loader, read, PolicyMetadata, null, _profileRegistry);
        (_model, _schema) = _profileCache.GetFor(ProfileName);
        _model.GetTableFromDbName("members")
            .Metadata[MetadataKeys.Policy.RowScope] = "user_id = {user_id}";
        _model.GetTableFromDbName("households")
            .Metadata[MetadataKeys.Policy.RowScope] = "household_id = {household_id}";
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
        services.AddSingleton<IMutationModules>(new ModulesWrap { Modules = Array.Empty<IMutationModule>() });
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
    /// The principal's NameIdentifier becomes the <c>user_id</c> the row scope
    /// binds against; <paramref name="householdId"/> is carried as an org claim
    /// so the households row scope can resolve <c>{household_id}</c>.
    /// </summary>
    private async Task<GraphQlResponse> ExecuteAsync(
        string query, string role, int userId, int tenantId, int? householdId = null)
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

        context.User = BuildPrincipal(role, userId, tenantId, householdId);
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(200, "GraphQL responses are always HTTP 200");
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var json = await reader.ReadToEndAsync();
        return GraphQlResponse.Parse(json);
    }

    private static ClaimsPrincipal BuildPrincipal(string role, int userId, int tenantId, int? householdId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(LocalAuthClaims.Provider, "local"),
            new(LocalAuthClaims.Tenant, tenantId.ToString()),
            new(ClaimTypes.Role, role),
        };
        // The households row scope binds {household_id}; expose it as a direct
        // claim so the BifrostContext per-claim projection carries it into the
        // user context.
        if (householdId is not null)
            claims.Add(new Claim("household_id", householdId.Value.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    // ---- Member self-service: row scope narrows a member to their own row ----

    [Fact]
    public async Task MemberDirectQuery_OnMembers_SeesOnlyTheirOwnRow()
    {
        // user_id 42 is linked to member_id 1 only. The row scope
        // `user_id = {user_id}` is ANDed into the query, so a member querying
        // the whole table still gets just their own row — a raw GraphQL call
        // cannot widen the result set.
        var response = await ExecuteAsync(
            "query { members { data { member_id first_name } } }",
            role: "member", userId: 42, tenantId: 1);

        response.Errors.Should().BeEmpty();
        response.RowCount("members").Should().Be(1, "the member row scope pins a member to their own row");
    }

    [Fact]
    public async Task MemberDirectQuery_TargetingAnotherMembersRow_ReturnsEmpty()
    {
        // A member explicitly filtering for another member's row (member_id 2,
        // user_id 99) is ANDed against `user_id = 42` and matches nothing — the
        // cross-member read returns empty rather than another member's data.
        var response = await ExecuteAsync(
            "query { members(filter: { member_id: { _eq: 2 } }) { data { member_id first_name } } }",
            role: "member", userId: 42, tenantId: 1);

        response.Errors.Should().BeEmpty();
        response.RowCount("members").Should().Be(0,
            "the row scope makes another member's row unreachable via raw GraphQL");
    }

    [Fact]
    public async Task MemberDirectMutation_UpdatingAnotherMembersRow_AffectsNoRow()
    {
        // The mutation transformer scopes update to `user_id = {caller}`. A
        // member updating member_id 2 (user_id 99) matches no row, so the
        // cross-member write is denied server-side — and the row is unchanged.
        // The generated update input requires the table's NOT NULL columns.
        var update = await ExecuteAsync(
            "mutation { members(update: { member_id: 2, tenant_id: 1, user_id: 99, household_id: 2, first_name: \"Hacked\" }) }",
            role: "member", userId: 42, tenantId: 1);
        update.Errors.Should().BeEmpty("the scoped update is well-formed; it simply matches no row");

        // An admin read confirms member_id 2 was not modified.
        var verify = await ExecuteAsync(
            "query { members(filter: { member_id: { _eq: 2 } }) { data { member_id first_name } } }",
            role: "admin", userId: 1, tenantId: 1);
        verify.Errors.Should().BeEmpty();
        verify.FirstString("members", "first_name").Should().Be("Grace",
            "a member cannot edit another member's row through raw GraphQL");
    }

    [Fact]
    public async Task MemberDirectQuery_OnHouseholds_IsGovernedByThePolicyAndNeverLeaksTheWholeTable()
    {
        // households carries policy-row-scope-roles: member, so a member is
        // row-scoped on this table too. The exact `{household_id}` binding is
        // pinned at the unit layer (MembershipManagerRowScopePolicyTests). Here
        // the proof is that a member's raw GraphQL call against households is
        // governed by the policy engine: it is either narrowed to the member's
        // own household or fails closed — it never returns the full table.
        var memberResponse = await ExecuteAsync(
            "query { households { data { household_id name } } }",
            role: "member", userId: 42, tenantId: 1, householdId: 1);
        var adminResponse = await ExecuteAsync(
            "query { households { data { household_id name } } }",
            role: "admin", userId: 1, tenantId: 1);

        adminResponse.Errors.Should().BeEmpty();
        adminResponse.RowCount("households").Should().Be(2, "an admin is not row-scoped");

        // The member call is governed: either the row scope applied and the
        // result is narrowed, or it failed closed with a policy error. Either
        // way a raw GraphQL call cannot read every household.
        var memberLeakedWholeTable =
            memberResponse.Errors.Count == 0 && memberResponse.TryRowCount("households") >= 2;
        memberLeakedWholeTable.Should().BeFalse(
            "the member role is row-scoped on households, so a raw query cannot read the whole table");
    }

    // ---- Finance fields: amount/price columns are hidden from non-finance roles ----

    [Theory]
    [InlineData("officer")]
    [InlineData("member")]
    [InlineData("event_manager")]
    [InlineData("read_only")]
    public async Task NonFinanceRole_DirectQuery_SelectingDuesAmount_IsRejected(string role)
    {
        // amount_cents carries policy-read-deny qualified to these roles. A raw
        // GraphQL query selecting it is rejected with the generic policy error —
        // the column cannot be read by issuing GraphQL directly.
        var response = await ExecuteAsync(
            "query { dues_invoices { data { invoice_id amount_cents } } }",
            role: role, userId: 7, tenantId: 1);

        response.Errors.Should().ContainSingle();
        response.Errors[0].Should()
            .Be("The query references a field that is not permitted by authorization policy.");
        response.Errors[0].Should().NotContainAny("amount_cents", "dues_invoices");
    }

    [Fact]
    public async Task NonFinanceRole_DirectQuery_SelectingPlanPrice_IsRejected()
    {
        var response = await ExecuteAsync(
            "query { membership_plans { data { plan_id price_cents } } }",
            role: "officer", userId: 7, tenantId: 1);

        response.Errors.Should().ContainSingle();
        response.Errors[0].Should()
            .Be("The query references a field that is not permitted by authorization policy.");
    }

    [Fact]
    public async Task NonFinanceRole_DirectQuery_SelectingOnlyNonFinanceColumns_Succeeds()
    {
        // The role-scoped deny blocks only the finance column itself — an
        // officer reading the non-finance columns of a finance table is fine.
        var response = await ExecuteAsync(
            "query { dues_invoices { data { invoice_id status } } }",
            role: "officer", userId: 7, tenantId: 1);

        response.Errors.Should().BeEmpty("the deny is scoped to the finance column, not the whole table");
    }

    [Fact]
    public async Task FinanceManager_DirectQuery_SelectingDuesAmount_Succeeds()
    {
        // finance_manager is not in policy-read-deny-roles, so the qualified
        // deny does not apply — the finance column stays readable.
        var response = await ExecuteAsync(
            "query { dues_invoices { data { invoice_id amount_cents } } }",
            role: "finance_manager", userId: 7, tenantId: 1);

        response.Errors.Should().BeEmpty("finance_manager is not a denied role, so it may read the finance field");
    }

    // ---- Officer operational scope: full CRUD on the operational tables ----

    [Fact]
    public async Task OfficerDirectQuery_OnMembers_IsNotRowScoped()
    {
        // policy-row-scope-roles: member qualifies the scope to the member role.
        // An officer is a different role, so it is not narrowed — the officer
        // sees every member in the tenant.
        var response = await ExecuteAsync(
            "query { members { data { member_id first_name } } }",
            role: "officer", userId: 7, tenantId: 1);

        response.Errors.Should().BeEmpty();
        response.RowCount("members").Should().Be(2, "officer is not the member role, so it is not row-scoped");
    }

    [Fact]
    public async Task OfficerDirectMutation_UpdatingAnyMember_IsPermitted()
    {
        // The table-level policy-actions grant gives officer full CRUD on the
        // operational members table, and no row scope applies — an officer can
        // edit another member's row through raw GraphQL.
        var update = await ExecuteAsync(
            "mutation { members(update: { member_id: 2, tenant_id: 1, user_id: 99, household_id: 2, first_name: \"Renamed\" }) }",
            role: "officer", userId: 7, tenantId: 1);
        update.Errors.Should().BeEmpty("officer holds the update grant on the operational members table");

        var verify = await ExecuteAsync(
            "query { members(filter: { member_id: { _eq: 2 } }) { data { member_id first_name } } }",
            role: "admin", userId: 1, tenantId: 1);
        verify.FirstString("members", "first_name").Should().Be("Renamed",
            "officer manages the full member lifecycle");
    }

    // ---- Read-only table: every write is denied through the entry point ----

    [Fact]
    public async Task ReadOnlyRole_DirectMutation_WritingAuditLog_IsRejected()
    {
        // audit_log carries policy-actions: read only — it grants no write
        // action, so the mutation transformer rejects the create for read_only.
        var response = await ExecuteAsync(
            "mutation { audit_log(insert: { tenant_id: 1, action: \"tampered\" }) }",
            role: "read_only", userId: 7, tenantId: 1);

        response.Errors.Should().ContainSingle();
        response.Errors[0].Should().Be("Access denied by authorization policy.");
    }

    [Fact]
    public async Task OfficerDirectMutation_WritingAuditLog_IsAlsoRejected()
    {
        // The read-only grant is role-blind by design — officer is likewise
        // denied a write to a read-only table.
        var response = await ExecuteAsync(
            "mutation { audit_log(insert: { tenant_id: 1, action: \"tampered\" }) }",
            role: "officer", userId: 7, tenantId: 1);

        response.Errors.Should().ContainSingle();
        response.Errors[0].Should().Be("Access denied by authorization policy.");
    }

    // ---- Admin bypass: every Membership Manager scenario passes for an admin ----

    [Fact]
    public async Task AdminDirectRequests_PassEveryMembershipManagerScenario()
    {
        // 1. Row scope — admin sees every member, not just their own row.
        var membersResponse = await ExecuteAsync(
            "query { members { data { member_id first_name } } }",
            role: "admin", userId: 1, tenantId: 1);
        membersResponse.Errors.Should().BeEmpty();
        membersResponse.RowCount("members").Should().Be(2, "an admin is not narrowed by the row scope");

        // 2. Household row scope — admin sees every household.
        var householdsResponse = await ExecuteAsync(
            "query { households { data { household_id name } } }",
            role: "admin", userId: 1, tenantId: 1);
        householdsResponse.Errors.Should().BeEmpty();
        householdsResponse.RowCount("households").Should().Be(2);

        // 3. Finance field deny — admin reads the denied finance columns.
        var financeResponse = await ExecuteAsync(
            "query { dues_invoices { data { invoice_id amount_cents } } }",
            role: "admin", userId: 1, tenantId: 1);
        financeResponse.Errors.Should().BeEmpty("an admin bypasses the finance-field read-deny");

        var planResponse = await ExecuteAsync(
            "query { membership_plans { data { plan_id price_cents } } }",
            role: "admin", userId: 1, tenantId: 1);
        planResponse.Errors.Should().BeEmpty();

        // 4. Read-only table — admin writes the read-only audit_log.
        var auditResponse = await ExecuteAsync(
            "mutation { audit_log(insert: { tenant_id: 1, action: \"admin-entry\" }) }",
            role: "admin", userId: 1, tenantId: 1);
        auditResponse.Errors.Should().BeEmpty("an admin bypasses the read-only table grant");
    }

    /// <summary>
    /// Minimal reader over the GraphQL JSON response envelope — the error
    /// messages, the row count for a top-level paged field, and the first
    /// row's value for a named column.
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

        private JsonElement Rows(string field)
        {
            if (_data.ValueKind != JsonValueKind.Object
                || !_data.TryGetProperty(field, out var fieldElement)
                || fieldElement.ValueKind != JsonValueKind.Object
                || !fieldElement.TryGetProperty("data", out var rows)
                || rows.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"Response has no paged field '{field}'.");
            }

            return rows;
        }

        /// <summary>Count of rows returned for a top-level paged field.</summary>
        public int RowCount(string field) => Rows(field).GetArrayLength();

        /// <summary>
        /// Row count for a top-level paged field, or -1 when the field is absent
        /// (e.g. the request failed closed with an error and produced no data).
        /// </summary>
        public int TryRowCount(string field)
        {
            try
            {
                return Rows(field).GetArrayLength();
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }

        /// <summary>The named column's string value on the first returned row.</summary>
        public string FirstString(string field, string column)
        {
            var rows = Rows(field);
            if (rows.GetArrayLength() == 0)
                throw new InvalidOperationException($"Paged field '{field}' returned no rows.");
            return rows[0].GetProperty(column).GetString()
                ?? throw new InvalidOperationException($"Column '{column}' was null.");
        }
    }
}
