using BifrostQL.Core.Model;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// The EAV collector must resolve <c>eav-parent</c> within the meta table's OWN
/// schema. A DbName-only match binds <c>app.settings_meta</c>'s parent to
/// <c>dbo.settings</c> (or vice versa) — the wrong table, applying the wrong
/// security filter (cross-schema data exposure).
/// </summary>
public class EavConfigCollectorSchemaTests
{
    private static DbTable Table(string name, string schema, Action<DbModelTestFixture.TableBuilder> configure)
    {
        var builder = new DbModelTestFixture.TableBuilder(name).WithSchema(schema);
        configure(builder);
        return builder.Build();
    }

    private static DbTable MetaTable(string schema) =>
        Table("settings_meta", schema, t => t
            .WithPrimaryKey("id")
            .WithColumn("setting_id", "int")
            .WithColumn("k", "nvarchar")
            .WithColumn("v", "nvarchar")
            .WithMetadata(MetadataKeys.Eav.Parent, "settings")
            .WithMetadata(MetadataKeys.Eav.ForeignKey, "setting_id")
            .WithMetadata(MetadataKeys.Eav.Key, "k")
            .WithMetadata(MetadataKeys.Eav.Value, "v"));

    private static DbTable Settings(string schema) =>
        Table("settings", schema, t => t.WithPrimaryKey("id").WithColumn("val", "nvarchar"));

    [Fact]
    public void Collect_ParentInSameSchema_BindsWithinSchema()
    {
        // app.settings_meta references "settings"; both app.settings and dbo.settings
        // exist. It must bind to app.settings (the meta table's own schema).
        var tables = new List<IDbTable> { Settings("app"), Settings("dbo"), MetaTable("app") };

        var configs = new EavConfigCollector().Collect(tables);

        configs.Should().ContainSingle();
        configs[0].ParentTableDbName.Should().Be("settings");
        configs[0].TableSchema.Should().Be("app");
    }

    [Fact]
    public void Collect_ParentOnlyInDifferentSchema_DoesNotBindCrossSchema()
    {
        // app.settings_meta references "settings" but only dbo.settings exists — a
        // cross-schema bind must be rejected (no config produced).
        var tables = new List<IDbTable> { Settings("dbo"), MetaTable("app") };

        var configs = new EavConfigCollector().Collect(tables);

        configs.Should().BeEmpty();
    }
}
