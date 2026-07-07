using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
using Xunit.Abstractions;
using RootExecutionNode = GraphQL.Execution.RootExecutionNode;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Soft-delete data-shaping across profiles under fail-closed profile filtering. The crm
/// sample seeds 3 soft-deleted deal rows (deleted_at IS NOT NULL). Soft-delete is a
/// data-integrity module (priority 100, below <see cref="BifrostProfile.ApplicationPriorityFloor"/>),
/// so it applies under EVERY profile — including the client-controlled empty "default"
/// profile. A profile can no longer strip the filter by omitting the module name; deleted
/// rows are only reachable via the module's explicit escape hatch (the server-side
/// include_deleted user-context override, or the _includeDeleted query argument).
/// </summary>
public sealed class SoftDeleteShapeTests
{
    private readonly ITestOutputHelper _out;
    public SoftDeleteShapeTests(ITestOutputHelper o) => _out = o;

    private const string Rule = "*.deals { soft-delete: deleted_at }";

    [Fact]
    public async Task DealsTotal_SoftDeleteAppliesUnderEveryProfile_FailClosed()
    {
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        var connectionString = $"Data Source=softdelete_shape_{System.Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        var factory = DbConnFactoryResolver.Create(connectionString, BifrostDbProvider.Sqlite);

        foreach (var (schema, size) in new[] { ("crm", (string?)null), ("crm", "sample") })
        {
            var sql = (size == null ? await QuickstartSchemas.LoadSchemaSql(schema) : await QuickstartSchemas.LoadSeedSql(schema, size))!;
            var stmts = sql.Split(';', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            await QuickstartSchemas.ExecuteStatementsAsync(factory, stmts, default);
        }

        // Ground truth straight from the database.
        int allDeals, deletedDeals;
        await using (var probe = new SqliteConnection(connectionString))
        {
            await probe.OpenAsync();
            allDeals = System.Convert.ToInt32(await new SqliteCommand("SELECT COUNT(*) FROM deals", probe).ExecuteScalarAsync());
            deletedDeals = System.Convert.ToInt32(await new SqliteCommand("SELECT COUNT(*) FROM deals WHERE deleted_at IS NOT NULL", probe).ExecuteScalarAsync());
        }
        deletedDeals.Should().Be(3, "the crm sample seeds exactly 3 soft-deleted deals");

        var model = await new DbModelLoader(factory, new MetadataLoader(new[] { Rule })).LoadAsync();

        // The full set of registered filter transformers (as the host would register).
        var allFilters = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new SoftDeleteFilterTransformer(),
                new TenantFilterTransformer(),
            },
        };

        // sales profile: soft-delete listed in the module set -> deleted deals hidden.
        var salesProfile = new BifrostProfile { Name = "sales", Modules = new[] { "soft-delete", "polymorphic" } };
        var salesTotal = await QueryDealsTotalAsync(model, factory, allFilters, salesProfile);

        // default profile: empty module set. Fail-closed profile filtering keeps the
        // soft-delete module active anyway, so deleted deals are hidden here too — a
        // client-selectable profile must never widen visibility past the data rules.
        var defaultProfile = new BifrostProfile { Name = "default", Modules = System.Array.Empty<string>() };
        var defaultTotal = await QueryDealsTotalAsync(model, factory, allFilters, defaultProfile);

        // Server-side escape hatch: the host (not the client) can opt into deleted rows
        // via the include_deleted user-context flag; all rows stay reachable that way.
        var overrideTotal = await QueryDealsTotalAsync(
            model, factory, allFilters, defaultProfile,
            userContext: new Dictionary<string, object?> { [SoftDeleteFilterTransformer.IncludeDeletedKey] = true });

        _out.WriteLine($"sales total = {salesTotal}, default total = {defaultTotal}, override total = {overrideTotal}, raw = {allDeals}");

        salesTotal.Should().Be(allDeals - deletedDeals,
            "the sales profile hides the seeded soft-deleted deals");
        defaultTotal.Should().Be(allDeals - deletedDeals,
            "fail-closed: the default profile must NOT expose soft-deleted deals just because its module list is empty");
        overrideTotal.Should().Be(allDeals,
            "the server-side include_deleted override is the sanctioned way to see deleted rows");
    }

    private async Task<int> QueryDealsTotalAsync(
        IDbModel model,
        IDbConnFactory factory,
        IFilterTransformers allFilters,
        BifrostProfile profile,
        Dictionary<string, object?>? userContext = null)
    {
        var schema = DbSchema.FromModel(model, profile);
        var profileFilters = BifrostProfileRegistry.FilterBy(allFilters, profile);
        var transformerService = new QueryTransformerService(profileFilters);
        var executor = new SqlExecutionManager(model, schema, transformerService);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap { Transformers = System.Array.Empty<IMutationTransformer>() });
        using var sp = services.BuildServiceProvider();

        var extensions = new Dictionary<string, object?>
        {
            { "connFactory", factory },
            { "model", model },
            { "tableReaderFactory", executor },
        };
        var result = await new DocumentExecuter().ExecuteAsync(o =>
        {
            o.Schema = schema;
            o.Query = "query { deals { total } }";
            o.Extensions = new Inputs(extensions);
            o.RequestServices = sp;
            o.UserContext = userContext ?? new Dictionary<string, object?>();
        });

        result.Errors.Should().BeNullOrEmpty();
        var root = (Dictionary<string, object?>)((RootExecutionNode)result.Data!).ToValue()!;
        var json = JsonSerializer.Serialize(root["deals"]);
        var paged = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        return paged["total"].GetInt32();
    }
}
