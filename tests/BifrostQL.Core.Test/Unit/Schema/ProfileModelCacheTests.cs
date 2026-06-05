using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using FluentAssertions;
using NSubstitute;
using System.Data.Common;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// Tests for <see cref="ProfileModelCache"/>: from one shared DB read it builds and
/// memoizes a (model, schema) per profile name. The base/"dev" profile carries no
/// metadata (raw schema); a registered "poly" profile overlays a polymorphic rule that
/// shapes its model. Builds are independent per profile and cached per name.
/// </summary>
public sealed class ProfileModelCacheTests
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
            Array.Empty<string>(),
            additionalMetadata: null,
            registry: MakePolyRegistry());
    }

    [Fact]
    public async Task GetFor_UnknownOrEmptyProfile_BuildsRawModelWithoutPolymorphicLink()
    {
        // Arrange
        var cache = await MakeCacheAsync();

        // Act — "dev" is not registered → empty default profile, no overlay.
        var dev = cache.GetFor("dev");

        // Assert — no synthetic polymorphic MultiLink on companies.
        dev.Model.GetTableFromDbName("companies").MultiLinks.Should().NotContainKey("notes");
    }

    [Fact]
    public async Task GetFor_NullProfile_BehavesAsDefault()
    {
        // Arrange
        var cache = await MakeCacheAsync();

        // Act
        var byNull = cache.GetFor(null);

        // Assert — raw model, no overlay.
        byNull.Model.GetTableFromDbName("companies").MultiLinks.Should().NotContainKey("notes");
    }

    [Fact]
    public async Task GetFor_RegisteredProfile_AppliesItsMetadataToModelAndSchema()
    {
        // Arrange
        var cache = await MakeCacheAsync();

        // Act
        var poly = cache.GetFor("poly");
        poly.Schema.Initialize();

        // Assert — the profile's polymorphic rule yields a notes MultiLink on companies.
        poly.Model.GetTableFromDbName("companies").MultiLinks.Should().ContainKey("notes");

        // Assert — the built schema exposes the companies type.
        poly.Schema.AllTypes.Any(t => t.Name == "companies").Should().BeTrue();
    }

    [Fact]
    public async Task GetFor_SameProfileTwice_ReturnsCachedInstance()
    {
        // Arrange
        var cache = await MakeCacheAsync();

        // Act
        var first = cache.GetFor("dev");
        var second = cache.GetFor("dev");

        // Assert — memoized: same model instance on repeat.
        second.Model.Should().BeSameAs(first.Model);
        second.Schema.Should().BeSameAs(first.Schema);
    }

    [Fact]
    public async Task GetFor_DifferentProfiles_ProduceDistinctModelInstances()
    {
        // Arrange
        var cache = await MakeCacheAsync();

        // Act
        var dev = cache.GetFor("dev");
        var poly = cache.GetFor("poly");

        // Assert — independent builds per profile.
        poly.Model.Should().NotBeSameAs(dev.Model);
    }

    [Fact]
    public async Task Reset_ClearsMemo_SoNextGetForRebuilds()
    {
        // Arrange
        var cache = await MakeCacheAsync();
        var before = cache.GetFor("dev");

        // Act
        cache.Reset();
        var after = cache.GetFor("dev");

        // Assert — a fresh build after reset.
        after.Model.Should().NotBeSameAs(before.Model);
    }
}
