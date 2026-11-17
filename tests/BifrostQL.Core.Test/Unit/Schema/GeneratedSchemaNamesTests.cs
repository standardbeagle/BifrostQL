using System.Text.RegularExpressions;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Unit.Schema;

/// <summary>
/// <see cref="GeneratedSchemaNames"/> is the list the validator checks for cross-table name
/// collisions. A per-table name the schema generator emits but the list omits would collide
/// unchecked, so this parses a generated SDL and requires every per-table name to be listed,
/// and every listed name to be emitted.
/// </summary>
public sealed class GeneratedSchemaNamesTests
{
    private static readonly Regex TypeDefinition =
        new(@"^\s*(?:type|input|enum)\s+([_A-Za-z][_0-9A-Za-z]*)", RegexOptions.Multiline);

    private static readonly Regex FieldName = new(@"^\s*([_A-Za-z][_0-9A-Za-z]*)\s*[(:]");

    private static void WithHistoryColumns(DbModelTestFixture.TableBuilder t)
    {
        t.WithSchema("dbo").WithPrimaryKey(MetadataKeys.History.Column.Id);
        foreach (var col in MetadataKeys.History.HistoryColumns)
        {
            if (!string.Equals(col, MetadataKeys.History.Column.Id, StringComparison.OrdinalIgnoreCase))
                t.WithColumn(col, "nvarchar", isNullable: true);
        }
    }

    // Every per-table feature the generator gates a name on: numeric columns (aggregate
    // fields type), filtered update (set / update_where inputs), history (trail field, and a
    // target table that keeps only its types).
    private static IDbModel RichModel() =>
        DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.History.Table, "dbo.__history")
            .WithTable("orders", t => t.WithSchema("dbo").WithPrimaryKey("id")
                .WithColumn("total", "int")
                .WithMetadata(MetadataKeys.FilteredUpdate.Enabled, "enabled")
                .WithMetadata(MetadataKeys.History.Enabled, MetadataKeys.History.AllOperations))
            .WithTable("notes", t => t.WithSchema("dbo").WithPrimaryKey("code", "nvarchar")
                .WithColumn("body", "nvarchar"))
            .WithTable("__history", WithHistoryColumns)
            .Build();

    // A one-table model with the same column scalars: what it emits beyond its own listed
    // names is the schema-wide part (shared enums, per-scalar filter inputs, metadata types).
    private static IDbModel BaselineModel() =>
        DbModelTestFixture.Create()
            .WithTable("baseline", t => t.WithSchema("dbo").WithPrimaryKey("id").WithColumn("label", "nvarchar"))
            .Build();

    private static (HashSet<string> Types, HashSet<string> Query, HashSet<string> Mutation) SchemaWide()
    {
        var model = BaselineModel();
        var (types, query, mutation) = Parse(SchemaGenerator.SchemaTextFromModel(model));
        foreach (var name in GeneratedSchemaNames.For(model, model.Tables.Single(), new HashSet<IDbTable>()))
        {
            var set = name.Space switch
            {
                GeneratedNameSpace.Type => types,
                GeneratedNameSpace.QueryField => query,
                _ => mutation,
            };
            set.Remove(name.Name).Should().BeTrue($"the baseline must emit its listed name '{name.Name}'");
        }
        return (types, query, mutation);
    }

    private static (HashSet<string> Types, HashSet<string> Query, HashSet<string> Mutation) Parse(string sdl)
    {
        var types = TypeDefinition.Matches(sdl).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return (types, Block(sdl, "database"), Block(sdl, "databaseInput"));
    }

    // The field names of one root object type: the lines between "type <name> {" and its "}".
    private static HashSet<string> Block(string sdl, string typeName)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var inside = false;
        foreach (var line in sdl.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!inside)
            {
                inside = trimmed == $"type {typeName} {{";
                continue;
            }
            if (trimmed == "}")
                break;
            if (FieldName.Match(trimmed) is { Success: true } match)
                fields.Add(match.Groups[1].Value);
        }
        return fields;
    }

    [Fact]
    public void GeneratedSdl_PerTableNames_AreExactlyTheListedNames()
    {
        var model = RichModel();
        var generated = Parse(SchemaGenerator.SchemaTextFromModel(model));
        var global = SchemaWide();

        var historyTargets = HistorySurface.ResolveTargets(model);
        historyTargets.Should().ContainSingle("the fixture must exercise the history-target branch");
        var listed = model.Tables.SelectMany(t => GeneratedSchemaNames.For(model, t, historyTargets)).ToList();
        HashSet<string> Listed(GeneratedNameSpace space) =>
            listed.Where(n => n.Space == space).Select(n => n.Name).ToHashSet(StringComparer.Ordinal);

        generated.Types.Except(global.Types).Should().BeEquivalentTo(Listed(GeneratedNameSpace.Type));
        generated.Query.Except(global.Query).Should().BeEquivalentTo(Listed(GeneratedNameSpace.QueryField));
        generated.Mutation.Except(global.Mutation).Should().BeEquivalentTo(Listed(GeneratedNameSpace.MutationField));

        // The gated names are present, so the fixture really reaches each branch.
        Listed(GeneratedNameSpace.Type).Should().Contain(new[] { "orders_aggregateFields", "orders_set", "orders_update_where" });
        Listed(GeneratedNameSpace.Type).Should().NotContain("notes_aggregateFields");
        Listed(GeneratedNameSpace.QueryField).Should().Contain("ordersHistory").And.NotContain("__history");
    }
}
