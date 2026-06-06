using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using FluentAssertions;
using GraphQL.Types;
using NSubstitute;
using System.Data.Common;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// Proves that polymorphic <c>notes</c> exposure is gated purely by per-profile metadata.
/// From one shared DB read, <see cref="ProfileModelCache"/> builds an independent (model,
/// schema) per profile. A profile with NO metadata ("dev") gets no polymorphic notes link,
/// so neither its model nor its executable schema exposes a <c>notes</c> field on the
/// companies type. A profile carrying the polymorphic rule ("poly") gets the link in BOTH
/// its model and its schema. Because the schema and the <c>_dbSchema</c> meta-resolver both
/// enumerate the same per-profile model, schema presence tracks metadata exactly.
/// </summary>
public sealed class PolymorphicProfileGatingTests
{
    private static readonly string[] PolyMetadata =
    {
        "*.notes { polymorphic-type-column: entity_type; polymorphic-id-column: entity_id; polymorphic-map: company=companies }"
    };

    /// <summary>
    /// A CRM-like read: companies (single-PK parent) and a shared notes table with
    /// entity_type/entity_id discriminator columns but NO polymorphic metadata baked in.
    /// </summary>
    private static SchemaData BuildCrmRead()
    {
        var companies = MakeTable("companies", t => t
            .WithPrimaryKey("company_id")
            .WithColumn("name", "nvarchar"));
        var notes = MakeTable("notes", t => t
            .WithPrimaryKey("note_id")
            .WithColumn("entity_type", "nvarchar")
            .WithColumn("entity_id", "int")
            .WithColumn("content", "nvarchar"));

        return new SchemaData(
            new Dictionary<ColumnRef, List<ColumnConstraintDto>>(),
            Array.Empty<ColumnDto>(),
            new List<IDbTable> { companies, notes });
    }

    private static DbTable MakeTable(string name, Action<DbModelTestFixture.TableBuilder> configure)
    {
        var builder = new DbModelTestFixture.TableBuilder(name);
        configure(builder);
        return builder.Build();
    }

    private static DbModelLoader MakeLoader()
    {
        var connFactory = Substitute.For<IDbConnFactory>();
        var connection = Substitute.For<DbConnection>();
        var schemaReader = Substitute.For<ISchemaReader>();

        connFactory.GetConnection().Returns(connection);
        connFactory.SchemaReader.Returns(schemaReader);
        connFactory.TypeMapper.Returns(SqlServerTypeMapper.Instance);
        schemaReader.ReadSchemaAsync(connection).Returns(BuildCrmRead());

        // Base rules EMPTY — gating must come solely from per-profile metadata.
        return new DbModelLoader(connFactory, new MetadataLoader(Array.Empty<string>()));
    }

    private static BifrostProfileRegistry MakePolyRegistry()
    {
        var registry = new BifrostProfileRegistry();
        registry.Add(new BifrostProfile
        {
            Name = "poly",
            Modules = new[] { "polymorphic" },
            Metadata = PolyMetadata,
        });
        return registry;
    }

    private static async Task<ProfileModelCache> MakeCacheAsync()
    {
        var loader = MakeLoader();
        var read = await loader.ReadAsync();
        return new ProfileModelCache(
            loader,
            read,
            baseMetadataRules: Array.Empty<string>(),
            additionalMetadata: null,
            registry: MakePolyRegistry());
    }

    private static IObjectGraphType? CompaniesType(ISchema schema) =>
        schema.AllTypes.OfType<IObjectGraphType>().FirstOrDefault(t => t.Name == "companies");

    [Fact]
    public async Task DevProfile_NoMetadata_OmitsNotesFromModelAndSchema()
    {
        // Arrange
        var cache = await MakeCacheAsync();

        // Act — "dev" is unregistered → empty default profile, no polymorphic overlay.
        var dev = cache.GetFor("dev");
        dev.Schema.Initialize();

        // Assert — model has NO notes MultiLink on companies.
        dev.Model.GetTableFromDbName("companies").MultiLinks.Should().NotContainKey("notes");

        // Assert — the executable schema's companies type exposes NO notes field.
        var companiesType = CompaniesType(dev.Schema);
        companiesType.Should().NotBeNull();
        companiesType!.Fields.Any(f => f.Name == "notes").Should().BeFalse(
            "without per-profile polymorphic metadata the notes link is never built, so the schema cannot expose it");
    }

    [Fact]
    public async Task PolyProfile_WithMetadata_ExposesNotesInModelAndSchema()
    {
        // Arrange
        var cache = await MakeCacheAsync();

        // Act — "poly" carries the polymorphic notes map.
        var poly = cache.GetFor("poly");
        poly.Schema.Initialize();

        // Assert — model has the notes MultiLink on companies.
        poly.Model.GetTableFromDbName("companies").MultiLinks.Should().ContainKey("notes");

        // Assert — the executable schema's companies type exposes a notes field.
        var companiesType = CompaniesType(poly.Schema);
        companiesType.Should().NotBeNull();
        companiesType!.Fields.Any(f => f.Name == "notes").Should().BeTrue(
            "the profile's polymorphic rule builds the notes link, which the schema then emits");
    }

    [Fact]
    public async Task DbSchemaEnumeration_TracksPerProfileModel()
    {
        // Arrange — the _dbSchema meta-resolver enumerates model.Tables[].MultiLinks and
        // marks a join polymorphic when TypePredicate != null (see MetaSchemaResolver).
        // Mirror that enumeration over each per-profile model to prove _dbSchema is
        // consistent with the schema BY CONSTRUCTION (both derive from one model).
        var cache = await MakeCacheAsync();
        var dev = cache.GetFor("dev");
        var poly = cache.GetFor("poly");

        // Act
        var devPolyJoins = dev.Model.Tables
            .SelectMany(t => t.MultiLinks.Values)
            .Where(j => j.TypePredicate != null)
            .ToList();
        var polyPolyJoins = poly.Model.Tables
            .SelectMany(t => t.MultiLinks.Values)
            .Where(j => j.TypePredicate != null)
            .ToList();

        // Assert — dev model yields NO polymorphic multiJoin in the _dbSchema view.
        devPolyJoins.Should().BeEmpty(
            "_dbSchema enumerates the same per-profile model the schema does, so a profile " +
            "without polymorphic metadata reports no polymorphic join either");

        // Assert — poly model yields the notes polymorphic multiJoin.
        polyPolyJoins.Should().ContainSingle()
            .Which.ChildTable.GraphQlName.Should().Be("notes");
    }
}
