using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BifrostQL.Samples.HostedSpa;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// S14: the HostedSpa membership-manager sample carries every guard shape the Track
/// case study hand-wrote as ONE metadata line in <c>samples/HostedSpa/appsettings.json</c>,
/// and the docs never claim a control the sample does not carry.
///
/// Every fact drives the real sample host (<see cref="WebApplicationFactory{Program}"/>
/// over <c>Program</c>) through its own <c>/graphql</c> door with a local-auth login
/// cookie, one denied caller and one allowed caller per shape:
///
///   delete[events.manage]                  member refused, officer allowed
///   write-requires: dues.manage            officer refused, finance allowed
///   read-requires: members.manage (mask)   member reads null, officer reads the value
///   policy-row-scope + -exempt             member sees own row, officer sees all
///   _grants                                the delegate IGrantResolver's permissions
///   _dbSchema                              member vs officer differ as the README shows
///
/// The two doc-truth facts read <c>samples/HostedSpa/README.md</c> and
/// <c>docs/src/content/docs/guides/authorization.md</c> off disk: the README's discovery
/// query must execute, and every metadata fragment the authorization guide's
/// "From application guards to metadata" table quotes must appear verbatim in the
/// sample's metadata.
/// </summary>
public sealed class HostedSpaPolicyTests
{
    private const string AccessDenied = "Access denied by authorization policy.";
    private const string FieldWriteDenied = "The mutation writes a field that is not permitted by authorization policy.";

    // ---- delete[events.manage] ----

    [Fact]
    public async Task EventDelete_MemberIsRefused_OfficerIsAllowed()
    {
        const string deleteEvent = "mutation { events(delete: { event_id: 1 }) }";

        await using var factory = new PolicyFactory();

        var member = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(member, HostedSpaLogins.Member);
        Errors(await GraphQlAsync(member, deleteEvent))
            .Should().ContainSingle().Which.Should().Be(AccessDenied);
        Rows(await GraphQlAsync(member, "{ events { data { event_id } } }"), "events")
            .Should().ContainSingle("a refused delete must not remove the row");

        var officer = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(officer, HostedSpaLogins.Officer);
        Errors(await GraphQlAsync(officer, deleteEvent)).Should().BeEmpty();
        Rows(await GraphQlAsync(officer, "{ events { data { event_id } } }"), "events")
            .Should().BeEmpty("the officer's delete removed the seeded event");
    }

    // ---- write-requires: dues.manage on dues_payments.amount_cents ----

    [Fact]
    public async Task PaymentAmountWrite_OfficerIsRefused_FinanceIsAllowed()
    {
        const string insertPayment =
            "mutation { dues_payments(insert: { tenant_id: 1, invoice_id: 1, amount_cents: 500, paid_on: \"2025-01-05\", method: \"cash\" }) }";

        await using var factory = new PolicyFactory();

        var officer = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(officer, HostedSpaLogins.Officer);
        Errors(await GraphQlAsync(officer, insertPayment))
            .Should().ContainSingle().Which.Should().Be(FieldWriteDenied);

        var finance = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(finance, HostedSpaLogins.Finance);
        Errors(await GraphQlAsync(finance, insertPayment)).Should().BeEmpty();
        var payments = Rows(await GraphQlAsync(finance, "{ dues_payments { data { amount_cents } } }"), "dues_payments");
        payments.Should().ContainSingle().Which.GetProperty("amount_cents").GetInt32().Should().Be(500);
    }

    // ---- read-requires: members.manage; deny-mode: null on app_users.roles ----

    [Fact]
    public async Task AppUserRolesRead_MemberSeesNull_OfficerSeesValue()
    {
        const string readRoles = "{ app_users { data { user_id roles } } }";

        await using var factory = new PolicyFactory();

        var member = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(member, HostedSpaLogins.Member);
        var masked = await GraphQlAsync(member, readRoles);
        Errors(masked).Should().BeEmpty("a masked column answers null, not a refusal");
        var maskedRows = Rows(masked, "app_users");
        maskedRows.Should().NotBeEmpty();
        maskedRows.Should().OnlyContain(r => r.GetProperty("roles").ValueKind == JsonValueKind.Null);

        var officer = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(officer, HostedSpaLogins.Officer);
        var visible = Rows(await GraphQlAsync(officer, readRoles), "app_users");
        visible.Single(r => r.GetProperty("user_id").GetInt64() == 1)
            .GetProperty("roles").GetString().Should().Be(SampleDatabase.FirstAdminRole);
        visible.Single(r => r.GetProperty("user_id").GetInt64() == HostedSpaLogins.Officer.UserId)
            .GetProperty("roles").GetString().Should().Be(HostedSpaLogins.Officer.Roles);
    }

    // ---- policy-row-scope: user_id = {user_id}; policy-row-scope-exempt: members.manage ----

    [Fact]
    public async Task MembersRowScope_MemberSeesOwnRowOnly_OfficerSeesEveryRow()
    {
        const string readMembers = "{ members { data { member_id user_id _can { update delete } } } }";

        await using var factory = new PolicyFactory();

        var member = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(member, HostedSpaLogins.Member);
        var own = Rows(await GraphQlAsync(member, readMembers), "members");
        var ownRow = own.Should().ContainSingle("the row scope narrows a member to their own row").Subject;
        ownRow.GetProperty("member_id").GetInt64().Should().Be(HostedSpaLogins.MemberProfileId);
        ownRow.GetProperty("user_id").GetInt64().Should().Be(HostedSpaLogins.Member.UserId);
        ownRow.GetProperty("_can").GetProperty("update").GetBoolean().Should().BeTrue();

        var officer = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(officer, HostedSpaLogins.Officer);
        var all = Rows(await GraphQlAsync(officer, readMembers), "members");
        all.Select(r => r.GetProperty("member_id").GetInt64())
            .Should().BeEquivalentTo(new long[] { 1, 2, HostedSpaLogins.MemberProfileId },
                "the exempt grant removes the row predicate");
    }

    // ---- _grants: the delegate IGrantResolver's rows ----

    [Theory]
    [InlineData("officer", new[] { "events.manage", "members.manage", "officer" })]
    [InlineData("finance", new[] { "dues.manage", "finance" })]
    [InlineData("member", new[] { "member" })]
    public async Task Grants_SeededRole_ReturnsRoleAndTheResolvedPermissions(string role, string[] expected)
    {
        await using var factory = new PolicyFactory();
        var client = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(client, HostedSpaLogins.ByRole(role));

        var root = await GraphQlAsync(client, "{ _grants }");

        Errors(root).Should().BeEmpty();
        root.GetProperty("data").GetProperty("_grants").EnumerateArray()
            .Select(g => g.GetString())
            .Should().Equal(expected);
    }

    // ---- _dbSchema: per-caller projection ----

    [Fact]
    public async Task DbSchema_MemberAndOfficer_DifferInAllowedActionsAndColumnFlags()
    {
        await using var factory = new PolicyFactory();

        var member = await DbSchemaAsync(factory, HostedSpaLogins.Member);
        var officer = await DbSchemaAsync(factory, HostedSpaLogins.Officer);
        var finance = await DbSchemaAsync(factory, HostedSpaLogins.Finance);

        AllowedActions(member, "events").Should().Equal("read", "create", "update");
        AllowedActions(officer, "events").Should().Equal("read", "create", "update", "delete");

        ColumnFlag(officer, "dues_payments", "amount_cents", "writable").Should().BeFalse();
        ColumnFlag(finance, "dues_payments", "amount_cents", "writable").Should().BeTrue();

        ColumnFlag(member, "app_users", "roles", "readable").Should().BeFalse();
        ColumnFlag(officer, "app_users", "roles", "readable").Should().BeTrue();

        AllowedActions(member, "audit_log").Should().Equal("read", "create");
        member.EnumerateArray().Select(t => t.GetProperty("graphQlName").GetString())
            .Should().NotContain("tenants",
                "a table with no policy is denied by default and absent from the projection");
    }

    // ---- Doc truth ----

    [Fact]
    public async Task ReadmeDiscoveryQuery_ExecutesAgainstTheSample()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "samples", "HostedSpa", "README.md"));
        var fence = Regex.Match(readme, "```graphql\\s*\\n(?<query>.*?)```", RegexOptions.Singleline);
        fence.Success.Should().BeTrue("the README documents the discovery query in a graphql fence");

        await using var factory = new PolicyFactory();
        var officer = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(officer, HostedSpaLogins.Officer);

        var root = await GraphQlAsync(officer, fence.Groups["query"].Value);

        Errors(root).Should().BeEmpty("the README query must execute as written");
        var data = root.GetProperty("data");
        data.GetProperty("_dbSchema").GetArrayLength().Should().BeGreaterThan(0);
        data.GetProperty("_grants").GetArrayLength().Should().BeGreaterThan(0);
        data.GetProperty("members").GetProperty("data").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public void AuthorizationGuideTable_EveryQuotedMetadataFragment_IsInTheSampleMetadata()
    {
        var guide = File.ReadAllText(Path.Combine(
            RepoRoot(), "docs", "src", "content", "docs", "guides", "authorization.md"));
        var section = Regex.Match(guide,
            "^## From application guards to metadata\\s*$(?<body>.*?)(?=^## )",
            RegexOptions.Multiline | RegexOptions.Singleline);
        section.Success.Should().BeTrue("the authorization guide carries the guards-to-metadata section");

        var fragments = section.Groups["body"].Value
            .Split('\n')
            .Where(line => line.TrimStart().StartsWith("| ", StringComparison.Ordinal))
            .SelectMany(line => Regex.Matches(line, "`(?<fragment>[^`]+)`").Select(m => m.Groups["fragment"].Value))
            .ToList();
        fragments.Should().HaveCountGreaterThanOrEqualTo(7, "the table quotes one metadata line per guard shape");

        var sampleMetadata = SampleMetadataLines();
        foreach (var fragment in fragments)
        {
            sampleMetadata.Should().Contain(line => line.Contains(fragment, StringComparison.Ordinal),
                $"the guide claims `{fragment}` and the sample must carry it");
        }
    }

    // ---- admin: the sample's only shipped login holds every grant the metadata names ----

    [Fact]
    public async Task Admin_RecordPayment_IsAllowed()
    {
        await using var factory = new PolicyFactory();
        var admin = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(admin, HostedSpaLogins.Admin);

        var response = await admin.PostAsJsonAsync(
            "/workflows/membership/record-payment",
            new { invoiceId = 1, amountCents = 12000, method = "check", paidOn = "2025-01-05" });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the admin holds dues.manage, so the record-payment pre-flight admits it");
        Rows(await GraphQlAsync(admin, "{ dues_payments { data { amount_cents } } }"), "dues_payments")
            .Should().ContainSingle().Which.GetProperty("amount_cents").GetInt32().Should().Be(12000);
    }

    [Fact]
    public async Task Admin_EventDelete_IsAllowed()
    {
        await using var factory = new PolicyFactory();
        var admin = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(admin, HostedSpaLogins.Admin);

        Errors(await GraphQlAsync(admin, "mutation { events(delete: { event_id: 1 }) }"))
            .Should().BeEmpty("the admin holds events.manage, which delete[events.manage] requires");
        Rows(await GraphQlAsync(admin, "{ events { data { event_id } } }"), "events")
            .Should().BeEmpty("the admin's delete removed the seeded event");
    }

    [Fact]
    public async Task Admin_AppUserRolesRead_SeesValue()
    {
        await using var factory = new PolicyFactory();
        var admin = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(admin, HostedSpaLogins.Admin);

        var rows = Rows(await GraphQlAsync(admin, "{ app_users { data { user_id roles } } }"), "app_users");

        rows.Single(r => r.GetProperty("user_id").GetInt64() == HostedSpaLogins.Admin.UserId)
            .GetProperty("roles").GetString().Should().Be(SampleDatabase.FirstAdminRole,
                "the admin holds members.manage, which read-requires names");
        rows.Single(r => r.GetProperty("user_id").GetInt64() == HostedSpaLogins.Officer.UserId)
            .GetProperty("roles").GetString().Should().Be(HostedSpaLogins.Officer.Roles);
    }

    [Fact]
    public async Task Admin_MembersRowScope_SeesEveryRow()
    {
        await using var factory = new PolicyFactory();
        var admin = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(admin, HostedSpaLogins.Admin);

        var all = Rows(await GraphQlAsync(admin, "{ members { data { member_id _can { update delete } } } }"), "members");

        all.Select(r => r.GetProperty("member_id").GetInt64())
            .Should().BeEquivalentTo(new long[] { 1, 2, HostedSpaLogins.MemberProfileId },
                "the admin holds members.manage, the row-scope-exempt grant");
        all.Should().OnlyContain(r => r.GetProperty("_can").GetProperty("delete").GetBoolean());
    }

    [Fact]
    public async Task Admin_Grants_ListsTheWholeCatalogue()
    {
        await using var factory = new PolicyFactory();
        var admin = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(admin, HostedSpaLogins.Admin);

        var root = await GraphQlAsync(admin, "{ _grants }");

        Errors(root).Should().BeEmpty();
        root.GetProperty("data").GetProperty("_grants").EnumerateArray()
            .Select(g => g.GetString())
            .Should().Equal("admin", "dues.manage", "events.manage", "members.manage");
    }

    // ---- helpers ----

    private static async Task<JsonElement> DbSchemaAsync(PolicyFactory factory, HostedSpaLogins.Login login)
    {
        var client = factory.CreateClient();
        await HostedSpaLogins.SignInAsync(client, login);
        var root = await GraphQlAsync(client,
            "{ _dbSchema { graphQlName allowedActions columns { graphQlName readable writable } } }");
        Errors(root).Should().BeEmpty();
        return root.GetProperty("data").GetProperty("_dbSchema");
    }

    private static JsonElement Table(JsonElement dbSchema, string table) =>
        dbSchema.EnumerateArray().Single(t => t.GetProperty("graphQlName").GetString() == table);

    private static string[] AllowedActions(JsonElement dbSchema, string table) =>
        Table(dbSchema, table).GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()!).ToArray();

    private static bool ColumnFlag(JsonElement dbSchema, string table, string column, string flag) =>
        Table(dbSchema, table).GetProperty("columns").EnumerateArray()
            .Single(c => c.GetProperty("graphQlName").GetString() == column)
            .GetProperty(flag).GetBoolean();

    private static async Task<JsonElement> GraphQlAsync(HttpClient client, string query)
    {
        var response = await client.PostAsJsonAsync("/graphql", new { query });
        response.StatusCode.Should().Be(HttpStatusCode.OK, "GraphQL responses are always HTTP 200");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static List<string> Errors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            return new List<string>();
        return errors.EnumerateArray().Select(e => e.GetProperty("message").GetString() ?? string.Empty).ToList();
    }

    private static List<JsonElement> Rows(JsonElement root, string table)
    {
        Errors(root).Should().BeEmpty($"the {table} read must resolve without GraphQL errors");
        return root.GetProperty("data").GetProperty(table).GetProperty("data").EnumerateArray().ToList();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BifrostQL.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test runs inside the BifrostQL checkout");
        return dir!.FullName;
    }

    private static List<string> SampleMetadataLines()
    {
        var path = Path.Combine(RepoRoot(), "samples", "HostedSpa", "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return doc.RootElement.GetProperty("BifrostQL").GetProperty("Metadata")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    /// <summary>
    /// Hosts the HostedSpa sample over a fresh SQLite file seeded with the sample's own
    /// data plus the officer / finance / member logins (<see cref="HostedSpaLogins"/>).
    /// </summary>
    public sealed class PolicyFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"hostedspa-policy-{Guid.NewGuid():N}.db");

        protected override IHost CreateHost(IHostBuilder builder)
        {
            HostedSpaLogins.Seed(_dbPath);
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:bifrost"] = $"Data Source={_dbPath}",
                }));
            return base.CreateHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }
}

/// <summary>
/// The role-holding logins the HostedSpa integration tests add beside the sample's seeded
/// first-admin: an officer, a finance user, and a member whose login is linked to its own
/// <c>members</c> row so the row scope has something to narrow to. Seeded straight into the
/// SQLite file BEFORE the host boots — <c>SampleDatabase.EnsureCreated</c> then sees the
/// file and leaves it alone — with the same <see cref="PasswordHasher{TUser}"/> the
/// sample's local auth verifies against.
/// </summary>
public static class HostedSpaLogins
{
    public sealed record Login(long UserId, string Email, string DisplayName, string Roles);

    public const string Password = "Sample!2025";

    /// <summary>The <c>members</c> row linked to <see cref="Member"/>.</summary>
    public const long MemberProfileId = 3;

    /// <summary>The sample's own seeded first-admin (<see cref="SampleDatabase.SeedFirstAdmin"/>).</summary>
    public static readonly Login Admin = new(1, SampleDatabase.FirstAdminEmail, "Club Admin", SampleDatabase.FirstAdminRole);

    public static readonly Login Officer = new(3, "officer@riverside-tennis.example", "Pat Officer", "officer");
    public static readonly Login Finance = new(4, "finance@riverside-tennis.example", "Sam Finance", "finance");
    public static readonly Login Member = new(5, "lee@riverside-tennis.example", "Lee Member", "member");

    public static Login ByRole(string role) => role switch
    {
        "admin" => Admin,
        "officer" => Officer,
        "finance" => Finance,
        "member" => Member,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public static void Seed(string dbPath)
    {
        SampleDatabase.EnsureCreated(dbPath);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        var hasher = new PasswordHasher<string>();
        foreach (var login in new[] { Officer, Finance, Member })
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO app_users (user_id, tenant_id, email, display_name, password_hash, roles)
                VALUES ($id, 1, $email, $name, $hash, $roles);
                """;
            insert.Parameters.AddWithValue("$id", login.UserId);
            insert.Parameters.AddWithValue("$email", login.Email);
            insert.Parameters.AddWithValue("$name", login.DisplayName);
            insert.Parameters.AddWithValue("$hash", hasher.HashPassword(login.Email, Password));
            insert.Parameters.AddWithValue("$roles", login.Roles);
            insert.ExecuteNonQuery();
        }

        using var member = connection.CreateCommand();
        member.CommandText =
            """
            INSERT INTO members (member_id, tenant_id, user_id, first_name, last_name, email, status, joined_on)
            VALUES ($memberId, 1, $userId, 'Lee', 'Member', 'lee@example.com', 'active', '2024-03-01');
            """;
        member.Parameters.AddWithValue("$memberId", MemberProfileId);
        member.Parameters.AddWithValue("$userId", Member.UserId);
        member.ExecuteNonQuery();
    }

    /// <summary>
    /// Signs the cookie-tracking client in through the sample's <c>POST /auth/login</c>,
    /// the way <see cref="HostedSpaLocalAuthTests"/> does; later requests carry the cookie.
    /// <see cref="Admin"/> is the sample's seeded first-admin and signs in with its password.
    /// </summary>
    public static async Task SignInAsync(HttpClient client, Login login)
    {
        var password = ReferenceEquals(login, Admin) ? SampleDatabase.FirstAdminPassword : Password;
        var response = await client.PostAsJsonAsync(
            "/auth/login", new { login = login.Email, password });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, $"{login.Roles} must be able to sign in");
    }
}
