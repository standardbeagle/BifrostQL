using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Fts;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for FTS slice 1 — the metadata contract parsed from <c>search</c> /
/// <c>search-language</c> into a typed <see cref="FtsConfig"/>. The per-dialect SQL
/// lowering (next slice) consumes this config, so its parse and its fail-fast behavior
/// are pinned here: a <c>search</c> list naming a missing or non-string column would
/// otherwise surface the <c>_search</c> operator over a meaningless match target with no
/// error until a query arrived.
/// </summary>
public class FtsConfigTests
{
    // Columns mirror a small articles table: title/body are text, published_on is not.
    private static IDbTable TableWithMetadata(params (string key, object? value)[] metadata)
    {
        var table = Substitute.For<IDbTable>();
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in metadata)
            dict[key] = value;
        var columns = new (string name, string type)[]
            {
                ("id", "int"),
                ("title", "nvarchar"),
                ("body", "text"),
                ("published_on", "datetime2"),
            }
            .ToDictionary(
                c => c.name,
                c => new ColumnDto
                {
                    ColumnName = c.name,
                    GraphQlName = c.name,
                    NormalizedName = ColumnDto.NormalizeColumn(c.name),
                    DataType = c.type,
                },
                StringComparer.OrdinalIgnoreCase);
        table.DbName.Returns("articles");
        table.TableSchema.Returns("dbo");
        table.Metadata.Returns(dict);
        table.ColumnLookup.Returns(columns);
        table.GetMetadataValue(Arg.Any<string>())
            .Returns(ci => dict.TryGetValue((string)ci[0], out var v) ? v?.ToString() : null);
        return table;
    }

    [Fact]
    public void FromTable_NoSearchKey_ReturnsNone()
    {
        var table = TableWithMetadata();

        var config = FtsConfig.FromTable(table);

        config.IsSearchable.Should().BeFalse();
        config.Should().BeSameAs(FtsConfig.None);
    }

    [Fact]
    public void FromTable_SearchColumns_ResolvesListAndLanguage()
    {
        // Arrange: two string columns declared searchable, with a language hint.
        var table = TableWithMetadata(
            (MetadataKeys.Fts.Search, "title,body"),
            (MetadataKeys.Fts.SearchLanguage, "english"));

        // Act
        var config = FtsConfig.FromTable(table);

        // Assert
        config.IsSearchable.Should().BeTrue();
        config.SearchColumns.Should().Equal("title", "body");
        config.Language.Should().Be("english");
    }

    [Fact]
    public void FromTable_NoLanguage_LeavesLanguageNull()
    {
        var table = TableWithMetadata((MetadataKeys.Fts.Search, "title"));

        var config = FtsConfig.FromTable(table);

        config.IsSearchable.Should().BeTrue();
        config.Language.Should().BeNull();
    }

    [Fact]
    public void FromTable_MissingColumn_Throws()
    {
        // A search over a column that does not exist matches nothing — reject it.
        var table = TableWithMetadata((MetadataKeys.Fts.Search, "title,nope"));

        var act = () => FtsConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*nope*does not exist*");
    }

    [Fact]
    public void FromTable_NonStringColumn_Throws()
    {
        // published_on is datetime2 — a full-text match over it is meaningless.
        var table = TableWithMetadata((MetadataKeys.Fts.Search, "title,published_on"));

        var act = () => FtsConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*published_on*string type*");
    }

    [Fact]
    public void FromTable_DuplicateColumn_Throws()
    {
        var table = TableWithMetadata((MetadataKeys.Fts.Search, "title,Title"));

        var act = () => FtsConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*more than once*");
    }

    [Fact]
    public void FromTable_PresentButEmpty_Throws()
    {
        // 'search' present but naming no column is a silent fail-open (reads as not
        // searchable despite declaring it) — reject like the CDC/History empty guards.
        var table = TableWithMetadata((MetadataKeys.Fts.Search, ", ,"));

        var act = () => FtsConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*names no column*");
    }
}
