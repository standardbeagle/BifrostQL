using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Model;
using FluentAssertions;
using NSubstitute;
using System.Data.Common;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Tests for the read/build split in <see cref="DbModelLoader"/>: a single DB
/// read (<c>ReadAsync</c>) can drive many model builds (<c>BuildModel</c>) with
/// caller-supplied <see cref="IMetadataLoader"/>s, so per-profile metadata varies
/// without re-reading the database.
/// </summary>
public sealed class DbModelLoaderSplitTests
{
    /// <summary>
    /// A CRM-like read: companies (single-PK parent) and a shared notes table with
    /// entity_type/entity_id discriminator columns but NO polymorphic metadata baked
    /// in — the polymorphic link only appears when a metadata rule supplies it.
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

    private static DbModelLoader MakeLoader(SchemaData read)
    {
        var connFactory = Substitute.For<IDbConnFactory>();
        var connection = Substitute.For<DbConnection>();
        var schemaReader = Substitute.For<ISchemaReader>();

        connFactory.GetConnection().Returns(connection);
        connFactory.SchemaReader.Returns(schemaReader);
        schemaReader.ReadSchemaAsync(connection).Returns(read);

        // Default metadata loader for the loader instance is irrelevant to these
        // tests; BuildModel takes a caller-supplied loader.
        return new DbModelLoader(connFactory, new MetadataLoader(Array.Empty<string>()));
    }

    [Fact]
    public async Task ReadOnce_BuildMany_DivergeOnMetadata()
    {
        // Arrange — one DB read.
        var read = await MakeLoader(BuildCrmRead()).ReadAsync();

        // Act — two builds from the SAME read with different metadata loaders.
        var plainModel = MakeLoader(read).BuildModel(read, new MetadataLoader(Array.Empty<string>()));
        var polyModel = MakeLoader(read).BuildModel(read, new MetadataLoader(new[]
        {
            "*.notes { polymorphic-type-column: entity_type; polymorphic-id-column: entity_id; polymorphic-map: company=companies }"
        }));

        // Assert — empty metadata yields no synthetic polymorphic MultiLink.
        plainModel.GetTableFromDbName("companies").MultiLinks.Should().NotContainKey("notes");

        // Assert — the polymorphic rule yields a notes MultiLink on companies.
        polyModel.GetTableFromDbName("companies").MultiLinks.Should().ContainKey("notes");

        // Assert — the two builds are independent instances from one read.
        polyModel.Should().NotBeSameAs(plainModel);
    }

    [Fact]
    public async Task LoadAsync_StillDelegatesToReadAndBuild()
    {
        // Arrange
        var loader = MakeLoader(BuildCrmRead());

        // Act
        var model = await loader.LoadAsync();

        // Assert — LoadAsync remains intact and produces a working model.
        model.Should().NotBeNull();
        model.GetTableFromDbName("companies").MultiLinks.Should().NotContainKey("notes");
    }
}
