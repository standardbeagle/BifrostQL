using System.Reflection;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Model;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Pins the one column copy routine (<see cref="ColumnDto"/>'s shared copy, used by
/// the per-build clone in <see cref="DbModelLoader.BuildModel"/> and by
/// <see cref="ColumnDto.WithGraphQlName"/>). The per-build clone once hand-listed its
/// properties and dropped CharacterMaxLength / NumericPrecision / NumericScale, so
/// every production model carried null lengths and schema-derived validation went
/// silent. The reflection facts make a property added later RED until it is copied.
/// </summary>
public sealed class ColumnDtoCopyTests
{
    private static ColumnDto FullyPopulatedColumn() => new()
    {
        TableCatalog = "cat",
        TableSchema = "dbo",
        TableName = "widgets",
        ColumnName = "usr_label",
        GraphQlName = "usr_label",
        NormalizedName = "usr_label",
        DetectedPrefix = "usr_",
        ColumnRef = new ColumnRef("cat", "dbo", "widgets", "usr_label"),
        DataType = "nvarchar",
        CharacterMaxLength = 7,
        NumericPrecision = 11,
        NumericScale = 3,
        IsNullable = true,
        OrdinalPosition = 4,
        IsIdentity = true,
        IsComputed = true,
        HasDefault = true,
        IsPrimaryKey = true,
        IsUnique = true,
        Metadata = new Dictionary<string, object?> { ["label"] = "Label" },
    };

    private static IEnumerable<PropertyInfo> CopiedProperties() =>
        typeof(ColumnDto).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0);

    [Fact]
    public void FullyPopulatedColumn_sets_every_settable_property_to_a_non_default()
    {
        // Guards the fixture itself: a new init property left at its default in
        // FullyPopulatedColumn would compare equal on both sides of a copy that
        // drops it, so the reflection facts below would go vacuous.
        var source = FullyPopulatedColumn();
        foreach (var property in CopiedProperties().Where(p => p.SetMethod != null))
        {
            var value = property.GetValue(source);
            var fallback = property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null;
            value.Should().NotBe(fallback, $"{property.Name} must be populated so a copy that drops it is visible");
        }
    }

    [Fact]
    public void CloneForBuild_copies_every_public_property_and_gives_a_fresh_metadata_dictionary()
    {
        // Arrange
        var source = FullyPopulatedColumn();

        // Act
        var clone = source.CloneForBuild();

        // Assert
        foreach (var property in CopiedProperties())
        {
            if (property.Name == nameof(ColumnDto.Metadata))
                continue;
            property.GetValue(clone).Should().Be(property.GetValue(source), $"{property.Name} must survive the per-build clone");
        }
        clone.Metadata.Should().Equal(source.Metadata);
        clone.Metadata.Should().NotBeSameAs(source.Metadata, "a build mutates column metadata, so each build owns its own copy");
    }

    [Fact]
    public void WithGraphQlName_copies_every_public_property_except_the_name_and_shares_metadata()
    {
        // Arrange
        var source = FullyPopulatedColumn();

        // Act
        var renamed = source.WithGraphQlName("usr_label_1");

        // Assert
        renamed.GraphQlName.Should().Be("usr_label_1");
        foreach (var property in CopiedProperties())
        {
            if (property.Name is nameof(ColumnDto.GraphQlName) or nameof(ColumnDto.Metadata))
                continue;
            property.GetValue(renamed).Should().Be(property.GetValue(source), $"{property.Name} must survive the rename");
        }
        renamed.Metadata.Should().BeSameAs(source.Metadata, "dedup renames a column in place; metadata stays shared");
    }

    [Fact]
    public void BuildModel_preserves_schema_length_precision_and_scale()
    {
        // Arrange — a read whose columns carry the facts the schema reader captured.
        var table = new DbModelTestFixture.TableBuilder("widgets")
            .WithPrimaryKey("id")
            .WithColumn("label", "nvarchar", characterMaxLength: 5)
            .WithColumn("price", "decimal", numericPrecision: 6, numericScale: 2)
            .Build();
        var read = new SchemaData(
            new Dictionary<ColumnRef, List<ColumnConstraintDto>>(),
            Array.Empty<ColumnDto>(),
            new List<IDbTable> { table });
        var connFactory = Substitute.For<IDbConnFactory>();
        var loader = new DbModelLoader(connFactory, new MetadataLoader(Array.Empty<string>()));

        // Act
        var model = loader.BuildModel(read, new MetadataLoader(Array.Empty<string>()));

        // Assert
        var built = model.GetTableFromDbName("widgets");
        built.ColumnLookup["label"].CharacterMaxLength.Should().Be(5);
        built.ColumnLookup["price"].NumericPrecision.Should().Be(6);
        built.ColumnLookup["price"].NumericScale.Should().Be(2);
    }
}
