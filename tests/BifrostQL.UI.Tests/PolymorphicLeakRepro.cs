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
/// Reproduction harness for the polymorphic cross-type leakage: query a parent's
/// polymorphic child collection through the full GraphQL pipeline and confirm it
/// only returns rows whose discriminator matches that parent.
/// </summary>
public sealed class PolymorphicLeakRepro
{
    private readonly ITestOutputHelper _out;
    public PolymorphicLeakRepro(ITestOutputHelper o) => _out = o;

    private const string Rule =
        "*.notes { polymorphic-type-column: entity_type; polymorphic-id-column: entity_id; polymorphic-map: company=companies, contact=contacts, deal=deals }";

    [Fact]
    public async Task CompanyNotes_DoNotLeakOtherEntityTypes()
    {
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        var connectionString = $"Data Source=poly_repro_{System.Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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

        var model = await new DbModelLoader(factory, new MetadataLoader(new[] { Rule })).LoadAsync();
        var polymorphicProfile = new BifrostProfile
        {
            Name = "sales",
            Modules = new[] { "polymorphic" },
        };
        var schema2 = DbSchema.FromModel(model, polymorphicProfile);

        var companies = model.GetTableFromDbName("companies");
        var notesField = companies.MultiLinks.Keys.First(k => k.Contains("notes", System.StringComparison.OrdinalIgnoreCase));
        _out.WriteLine($"notes field on companies = '{notesField}'");

        var services = new ServiceCollection();
        services.AddSingleton<IMutationModules>(new ModulesWrap { Modules = System.Array.Empty<IMutationModule>() });
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap { Transformers = System.Array.Empty<IMutationTransformer>() });
        using var sp = services.BuildServiceProvider();

        var executor = new SqlExecutionManager(model, schema2);
        var extensions = new Dictionary<string, object?>
        {
            { "connFactory", factory },
            { "model", model },
            { "tableReaderFactory", executor },
        };
        var query = $"query {{ companies(filter: {{ company_id: {{ _eq: 1 }} }}) {{ data {{ company_id {notesField} {{ data {{ entity_type entity_id content }} }} }} }} }}";
        var result = await new DocumentExecuter().ExecuteAsync(o =>
        {
            o.Schema = schema2;
            o.Query = query;
            o.Extensions = new Inputs(extensions);
            o.RequestServices = sp;
            o.UserContext = new Dictionary<string, object?>();
        });

        result.Errors.Should().BeNullOrEmpty();
        var root = (Dictionary<string, object?>)((RootExecutionNode)result.Data!).ToValue()!;
        var json = JsonSerializer.Serialize(root["companies"]);
        var paged = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var rows = paged["data"].EnumerateArray().ToList();
        rows.Should().HaveCount(1);
        // Nested multi-link collections are now paged wrappers: unwrap `.data`.
        var notes = rows[0].GetProperty(notesField).GetProperty("data").EnumerateArray().ToList();
        var types = notes.Select(n => n.GetProperty("entity_type").GetString()).ToList();
        _out.WriteLine("returned note entity_types: " + string.Join(", ", types));

        types.Should().OnlyContain(t => t == "company",
            "company notes must not include 'deal'/'contact' rows that merely share the id");
    }
}
