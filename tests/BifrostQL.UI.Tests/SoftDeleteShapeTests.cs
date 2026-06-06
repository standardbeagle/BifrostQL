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
/// Soft-delete data-shaping across profiles. The crm sample seeds 3 soft-deleted
/// deal rows (deleted_at IS NOT NULL). The sales profile activates the soft-delete
/// module, so deleted deals are hidden; the default profile does not, so all deals
/// (including deleted) are returned. The delta must be exactly the 3 deleted rows.
/// </summary>
public sealed class SoftDeleteShapeTests
{
    private readonly ITestOutputHelper _out;
    public SoftDeleteShapeTests(ITestOutputHelper o) => _out = o;

    private const string Rule = "*.deals { soft-delete: deleted_at }";

    [Fact]
    public async Task DealsTotal_SalesProfileHidesSoftDeleted_DefaultShowsAll()
    {
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        var connectionString = $"Data Source=softdelete_shape_{System.Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        var factory = DbConnFactoryResolver.Create(connectionString, BifrostDbProvider.Sqlite);

        foreach (var (schema, size) in new[] { ("crm", (string?)null), ("crm", "sample") })
        {
            var sql = size == null ? QuickstartSchemas.LoadSchemaSql(schema)! : QuickstartSchemas.LoadSeedSql(schema, size)!;
            var stmts = sql.Split(';', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            await QuickstartSchemas.ExecuteStatementsAsync(factory, stmts, default);
        }

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

        // sales profile: soft-delete module active -> deleted deals hidden.
        var salesProfile = new BifrostProfile { Name = "sales", Modules = new[] { "soft-delete", "polymorphic" } };
        var salesTotal = await QueryDealsTotalAsync(model, factory, allFilters, salesProfile);

        // default profile: no modules active -> soft-delete filtered out -> all deals.
        var defaultProfile = new BifrostProfile { Name = "default", Modules = System.Array.Empty<string>() };
        var defaultTotal = await QueryDealsTotalAsync(model, factory, allFilters, defaultProfile);

        _out.WriteLine($"sales total = {salesTotal}, default total = {defaultTotal}");

        salesTotal.Should().Be(defaultTotal - 3,
            "the sales profile activates soft-delete and must hide exactly the 3 seeded soft-deleted deals");
        defaultTotal.Should().BeGreaterThan(salesTotal,
            "the default/admin shape shows all deals including soft-deleted ones");
    }

    private async Task<int> QueryDealsTotalAsync(
        IDbModel model,
        IDbConnFactory factory,
        IFilterTransformers allFilters,
        BifrostProfile profile)
    {
        var schema = DbSchema.FromModel(model, profile);
        var profileFilters = BifrostProfileRegistry.FilterBy(allFilters, profile);
        var transformerService = new QueryTransformerService(profileFilters);
        var executor = new SqlExecutionManager(model, schema, transformerService);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationModules>(new ModulesWrap { Modules = System.Array.Empty<IMutationModule>() });
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
            o.UserContext = new Dictionary<string, object?>();
        });

        result.Errors.Should().BeNullOrEmpty();
        var root = (Dictionary<string, object?>)((RootExecutionNode)result.Data!).ToValue()!;
        var json = JsonSerializer.Serialize(root["deals"]);
        var paged = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        return paged["total"].GetInt32();
    }
}
