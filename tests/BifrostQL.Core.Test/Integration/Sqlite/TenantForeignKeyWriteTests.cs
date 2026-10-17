using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Resolvers.BulkBatch;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Server;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BatchAction = BifrostQL.Core.Resolvers.BatchMutationPipeline.BatchAction;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// A write may not point a foreign key at another tenant's row.
///
/// <para><see cref="TenantMutationTransformer"/> pins a row's OWN tenant column but never
/// looked at the values it REFERENCES, so a tenant-1 caller could insert a contact whose
/// <c>account_id</c> named tenant 2's account (the FK constraint knows nothing of
/// tenants). <see cref="TenantForeignKeyMutationTransformer"/> refuses that on every
/// write path — a closed set, one fact per path: single insert, single update, upsert,
/// batch, bulk batch (gated off the fast path), tree-sync, filtered update.</para>
///
/// <para>The fixture is what makes these facts able to fail: TWO tenants, a child and a
/// parent that are both tenant-filtered, a shared untenanted parent, and a composite FK
/// whose first column alone matches a row in the CALLER's tenant (so a check that reads
/// only the first FK column passes the cross-tenant reference). Built on the production
/// <c>AddBifrostQL</c> container, so the transformer is the one every host registers.</para>
/// </summary>
public sealed class TenantForeignKeyWriteTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_tenant_fk_write_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    private static readonly string[] Rules =
    {
        "main.accounts { tenant-filter: tenant_id }",
        "main.contacts { tenant-filter: tenant_id }",
        "main.projects { tenant-filter: tenant_id }",
        "main.tasks { tenant-filter: tenant_id }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        foreach (var drop in new[] { "tasks", "projects", "contacts", "countries", "accounts" })
            await Exec($"DROP TABLE IF EXISTS {drop}");
        await Exec("CREATE TABLE accounts (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, name TEXT NOT NULL)");
        await Exec("CREATE TABLE countries (code TEXT PRIMARY KEY, name TEXT NOT NULL)");
        await Exec(
            """
            CREATE TABLE contacts (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                account_id INTEGER NULL REFERENCES accounts(id),
                country_code TEXT NULL REFERENCES countries(code),
                name TEXT NOT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE projects (
                org INTEGER NOT NULL,
                code TEXT NOT NULL,
                tenant_id INTEGER NOT NULL,
                title TEXT NOT NULL,
                PRIMARY KEY (org, code)
            )
            """);
        await Exec(
            """
            CREATE TABLE tasks (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                project_org INTEGER NULL,
                project_code TEXT NULL,
                title TEXT NOT NULL,
                FOREIGN KEY (project_org, project_code) REFERENCES projects(org, code)
            )
            """);
        await Exec("INSERT INTO accounts(id, tenant_id, name) VALUES (1, 1, 'mine'), (2, 2, 'theirs')");
        await Exec("INSERT INTO countries(code, name) VALUES ('US', 'United States')");
        await Exec("INSERT INTO contacts(id, tenant_id, account_id, country_code, name) VALUES (10, 1, 1, NULL, 'alice'), (20, 2, 2, NULL, 'bob')");
        // (1,'A') is the caller's; (1,'B') is tenant 2's and shares the first FK column.
        await Exec("INSERT INTO projects(org, code, tenant_id, title) VALUES (1, 'A', 1, 'ours'), (1, 'B', 2, 'theirs')");
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string table, string where)
    {
        await using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM {table} WHERE {where}", _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // ---- single-row insert ---------------------------------------------------

    [Fact]
    public async Task SingleInsert_SameTenantParent_Succeeds()
    {
        // Positive control: without it, "refuse every FK" would pass the refusals below.
        var result = await ExecuteAsync("mutation { contacts(insert: { name: \"c\", account_id: 1 }) }");

        ShouldSucceed(result);
        (await CountAsync("contacts", "name = 'c' AND account_id = 1 AND tenant_id = 1")).Should().Be(1);
    }

    [Fact]
    public async Task SingleInsert_OtherTenantParent_IsRefused()
    {
        var result = await ExecuteAsync("mutation { contacts(insert: { name: \"c\", account_id: 2 }) }");

        ShouldBeRefused(result);
        (await CountAsync("contacts", "name = 'c'")).Should().Be(0);
    }

    [Fact]
    public async Task SingleInsert_NullForeignKey_Passes()
    {
        var result = await ExecuteAsync("mutation { contacts(insert: { name: \"c\", account_id: null }) }");

        ShouldSucceed(result);
        (await CountAsync("contacts", "name = 'c' AND account_id IS NULL")).Should().Be(1);
    }

    [Fact]
    public async Task SingleInsert_SharedUntenantedParent_Passes()
    {
        var result = await ExecuteAsync("mutation { contacts(insert: { name: \"c\", country_code: \"US\" }) }");

        ShouldSucceed(result);
        (await CountAsync("contacts", "name = 'c' AND country_code = 'US'")).Should().Be(1);
    }

    [Fact]
    public async Task MissingParent_And_OtherTenantParent_AnswerByteIdentically()
    {
        // Invariant 9: a different answer for "no such account" and "an account you may not
        // see" lets a caller enumerate other tenants' keys.
        var otherTenant = Describe(await ExecuteAsync("mutation { contacts(insert: { name: \"c\", account_id: 2 }) }"));
        var missing = Describe(await ExecuteAsync("mutation { contacts(insert: { name: \"c\", account_id: 9999 }) }"));

        otherTenant.Should().Be(missing);
        otherTenant.Should().Contain("A referenced row is not available.");
    }

    // ---- the other write paths ----------------------------------------------

    [Fact]
    public async Task SingleUpdate_RepointToOtherTenantParent_IsRefused()
    {
        var result = await ExecuteAsync("mutation { contacts(update: { id: 10, name: \"alice\", account_id: 2 }) }");

        ShouldBeRefused(result);
        (await CountAsync("contacts", "id = 10 AND account_id = 1")).Should().Be(1);
    }

    [Fact]
    public async Task SingleUpdate_RepointToSameTenantParent_Succeeds()
    {
        await Exec("INSERT INTO accounts(id, tenant_id, name) VALUES (3, 1, 'mine too')");

        var result = await ExecuteAsync("mutation { contacts(update: { id: 10, name: \"alice\", account_id: 3 }) }");

        ShouldSucceed(result);
        (await CountAsync("contacts", "id = 10 AND account_id = 3")).Should().Be(1);
    }

    [Fact]
    public async Task Upsert_OtherTenantParent_IsRefused()
    {
        var result = await ExecuteAsync("mutation { contacts(upsert: { id: 11, name: \"c\", account_id: 2 }) }");

        ShouldBeRefused(result);
        (await CountAsync("contacts", "id = 11")).Should().Be(0);
    }

    [Fact]
    public async Task Batch_OtherTenantParent_IsRefused_AndTheBatchRollsBack()
    {
        var result = await ExecuteAsync(
            "mutation { contacts_batch(actions: [ { insert: { name: \"ok\", account_id: 1 } }, " +
            "{ insert: { name: \"bad\", account_id: 2 } } ]) }");

        ShouldBeRefused(result);
        (await CountAsync("contacts", "name IN ('ok', 'bad')")).Should().Be(0);
    }

    [Fact]
    public async Task FilteredUpdate_SetToOtherTenantParent_IsRefused()
    {
        var result = await ExecuteAsync(
            "mutation { contacts(updateWhere: { set: { account_id: 2 }, where: { name: { _eq: \"alice\" } } }) }",
            filteredUpdate: true);

        ShouldBeRefused(result);
        (await CountAsync("contacts", "id = 10 AND account_id = 1")).Should().Be(1);
    }

    [Fact]
    public async Task TreeSync_ChildOfAParentInsertedEarlierInTheTree_Passes()
    {
        var result = await ExecuteAsync(
            "mutation { accounts(sync: { name: \"new\", contacts: [ { name: \"child\" } ] }) }");

        ShouldSucceed(result);
        (await CountAsync("contacts c JOIN accounts a ON a.id = c.account_id",
            "c.name = 'child' AND a.name = 'new' AND a.tenant_id = 1 AND c.tenant_id = 1")).Should().Be(1);
    }

    [Fact]
    public async Task TreeSync_OtherTenantParent_IsRefused()
    {
        var result = await ExecuteAsync("mutation { contacts(sync: { name: \"c\", account_id: 2 }) }");

        ShouldBeRefused(result);
        (await CountAsync("contacts", "name = 'c'")).Should().Be(0);
    }

    [Fact]
    public async Task BulkBatch_TableWithTenantScopedForeignKey_IsKeptOffTheFastPath()
    {
        // The bulk fast path transforms rows before any write transaction exists, so the
        // FK check cannot run there; it must keep such a table on the per-row path, while
        // a table without a tenant-scoped FK stays eligible (applicability, not registration).
        var model = await LoadModelAsync();
        await using var provider = BuildProductionServices();
        var transformers = provider.GetRequiredService<IMutationTransformers>();
        var contacts = model.GetTableFromDbName("contacts");
        var countries = model.GetTableFromDbName("countries");
        contacts.Metadata[MetadataKeys.Batch.BulkThreshold] = "1";
        countries.Metadata[MetadataKeys.Batch.BulkThreshold] = "1";
        var ctx = new MutationPipelineContext
        {
            Model = model,
            ConnFactory = new SqliteDbConnFactory(ConnString),
            Transformers = transformers,
            UserContext = TenantContext(),
            Services = provider,
        };
        var actions = new[] { new BatchAction(MutationAction.Insert, new Dictionary<string, object?> { ["name"] = "x" }) };

        BulkBatchPlanBuilder.IsEligible(contacts, actions, ctx, out var reason).Should().BeFalse();
        reason.Should().Contain("write transaction");
        BulkBatchPlanBuilder.IsEligible(countries, actions, ctx, out _).Should().BeTrue(
            "a table with no tenant-scoped foreign key keeps the fast path");
    }

    // ---- composite foreign key ----------------------------------------------

    [Fact]
    public async Task CompositeForeignKey_SameTenantParent_Succeeds()
    {
        var result = await ExecuteAsync("mutation { tasks(insert: { title: \"t\", project_org: 1, project_code: \"A\" }) }");

        ShouldSucceed(result);
        (await CountAsync("tasks", "title = 't'")).Should().Be(1);
    }

    [Fact]
    public async Task CompositeForeignKey_OtherTenantParent_IsRefused_EvenWhenTheFirstColumnMatchesOurs()
    {
        var result = await ExecuteAsync("mutation { tasks(insert: { title: \"t\", project_org: 1, project_code: \"B\" }) }");

        ShouldBeRefused(result);
        (await CountAsync("tasks", "title = 't'")).Should().Be(0);
    }

    // ---- harness -------------------------------------------------------------

    private static IDictionary<string, object?> TenantContext() =>
        new Dictionary<string, object?> { ["tenant_id"] = 1 };

    private static void ShouldSucceed(ExecutionResult result) =>
        result.Errors.Should().BeNullOrEmpty(
            $"errors: {string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>())}");

    private static void ShouldBeRefused(ExecutionResult result)
    {
        result.Errors.Should().NotBeNullOrEmpty();
        var error = BifrostErrorOf(result.Errors![0]);
        error.Message.Should().Be("A referenced row is not available.");
        error.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
    }

    private static BifrostExecutionError BifrostErrorOf(ExecutionError error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
            if (e is BifrostExecutionError bifrost)
                return bifrost;
        throw new InvalidOperationException($"not a Bifrost error: {error.Message}");
    }

    /// <summary>The whole observable answer: GraphQL message and code, and the carried error.</summary>
    private static string Describe(ExecutionResult result) =>
        string.Join(" | ", result.Errors?.Select(e =>
        {
            var inner = BifrostErrorOf(e);
            return $"{e.Code}:{e.Message}:{inner.GetType().FullName}:{inner.ErrorCode}:{inner.Message}";
        }) ?? new[] { "<no errors>" });

    private static async Task<IDbModel> LoadModelAsync(bool filteredUpdate = false)
    {
        var model = await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Rules)).LoadAsync();
        if (filteredUpdate)
            model.GetTableFromDbName("contacts").Metadata[MetadataKeys.FilteredUpdate.Enabled] = FilteredUpdateConfig.EnabledValue;
        return model;
    }

    private static async Task<ExecutionResult> ExecuteAsync(string mutation, bool filteredUpdate = false)
    {
        var model = await LoadModelAsync(filteredUpdate);
        var schema = DbSchema.FromModel(model);
        await using var provider = BuildProductionServices();
        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = TenantContext();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema, NullQueryTransformerService.Instance),
            });
        });
    }

    private static ServiceProvider BuildProductionServices()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bifrost:DisableAuth"] = "true",
                ["Bifrost:Path"] = "/graphql",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBifrostQL(o => o
            .BindConfiguration(config.GetSection("Bifrost"))
            .BindConnectionString(ConnString)
            .BindProvider("sqlite"));
        return services.BuildServiceProvider();
    }
}
