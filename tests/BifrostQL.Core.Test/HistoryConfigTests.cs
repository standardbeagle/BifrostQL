using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.History;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for History slice 1 — the metadata contract parsed from <c>history</c> /
/// <c>history-table</c> / <c>history-columns</c> into a typed <see cref="HistoryConfig"/>.
/// The diff writer (next slice) consumes this config, so its parse and its fail-fast
/// behavior on typo'd tokens are pinned here: a token that silently disabled tracking
/// would leave an audit trail with an invisible hole.
/// </summary>
public class HistoryConfigTests
{
    private static IDbTable TableWithMetadata(params (string key, object? value)[] metadata)
    {
        var table = Substitute.For<IDbTable>();
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in metadata)
            dict[key] = value;
        table.DbName.Returns("orders");
        table.TableSchema.Returns("dbo");
        table.Metadata.Returns(dict);
        table.GetMetadataValue(Arg.Any<string>())
            .Returns(ci => dict.TryGetValue((string)ci[0], out var v) ? v?.ToString() : null);
        return table;
    }

    [Fact]
    public void FromTable_NoHistoryKey_ReturnsNone()
    {
        // Arrange
        var table = TableWithMetadata();

        // Act
        var config = HistoryConfig.FromTable(table);

        // Assert
        config.RecordsHistory.Should().BeFalse();
        config.Should().BeSameAs(HistoryConfig.None);
    }

    [Fact]
    public void FromTable_EnabledToken_RecordsEveryOperation()
    {
        // Arrange: the plain opt-in switch.
        var table = TableWithMetadata((MetadataKeys.History.Enabled, MetadataKeys.History.AllOperations));

        // Act
        var config = HistoryConfig.FromTable(table);

        // Assert
        config.RecordsHistory.Should().BeTrue();
        config.Records(MutationType.Insert).Should().BeTrue();
        config.Records(MutationType.Update).Should().BeTrue();
        config.Records(MutationType.Delete).Should().BeTrue();
        config.TracksAllColumns.Should().BeTrue();
        config.HistoryTableOverride.Should().BeNull();
    }

    [Fact]
    public void FromTable_OperationSubset_RecordsOnlyThose()
    {
        // Arrange: update-only history — the common "who changed this field" case.
        var table = TableWithMetadata((MetadataKeys.History.Enabled, "update"));

        // Act
        var config = HistoryConfig.FromTable(table);

        // Assert
        config.Records(MutationType.Update).Should().BeTrue();
        config.Records(MutationType.Insert).Should().BeFalse();
        config.Records(MutationType.Delete).Should().BeFalse();
    }

    [Fact]
    public void FromTable_MixedCaseAndSpacing_Parses()
    {
        // Arrange
        var table = TableWithMetadata((MetadataKeys.History.Enabled, " Insert , DELETE "));

        // Act
        var config = HistoryConfig.FromTable(table);

        // Assert
        config.Records(MutationType.Insert).Should().BeTrue();
        config.Records(MutationType.Delete).Should().BeTrue();
        config.Records(MutationType.Update).Should().BeFalse();
    }

    [Fact]
    public void FromTable_UnknownOperation_Throws()
    {
        // Arrange: a typo must not silently drop the operation from the trail.
        var table = TableWithMetadata((MetadataKeys.History.Enabled, "update,bogus"));

        // Act
        var act = () => HistoryConfig.FromTable(table);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("bogus").And.Contain(MetadataKeys.History.Enabled);
    }

    [Fact]
    public void FromTable_PresentButNamesNoOperation_Throws()
    {
        // Arrange: "," parses to zero operations, which would read as RecordsHistory=false —
        // silently opting the table out of the very trail it declared.
        var table = TableWithMetadata((MetadataKeys.History.Enabled, ", ,"));

        // Act
        var act = () => HistoryConfig.FromTable(table);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("names no valid operation");
    }

    [Fact]
    public void FromTable_HistoryColumns_TracksOnlyListedColumns()
    {
        // Arrange
        var table = TableWithMetadata(
            (MetadataKeys.History.Enabled, MetadataKeys.History.AllOperations),
            (MetadataKeys.History.Columns, "status, total"));

        // Act
        var config = HistoryConfig.FromTable(table);

        // Assert
        config.TracksAllColumns.Should().BeFalse();
        config.TrackedColumns.Should().Equal("status", "total");
        config.TracksColumn("STATUS").Should().BeTrue();   // column names match case-insensitively
        config.TracksColumn("internal_notes").Should().BeFalse();
    }

    [Fact]
    public void FromTable_NoHistoryColumns_TracksEveryColumn()
    {
        // Arrange
        var table = TableWithMetadata((MetadataKeys.History.Enabled, "update"));

        // Act
        var config = HistoryConfig.FromTable(table);

        // Assert
        config.TracksAllColumns.Should().BeTrue();
        config.TracksColumn("anything").Should().BeTrue();
    }

    [Fact]
    public void FromTable_HistoryTable_ExposedAsPerTableOverride()
    {
        // Arrange: per-table history table; resolution against the model happens in
        // ModelConfigValidator, so the parse only surfaces the raw override.
        var table = TableWithMetadata(
            (MetadataKeys.History.Enabled, "update"),
            (MetadataKeys.History.Table, " dbo.orders_history "));

        // Act
        var config = HistoryConfig.FromTable(table);

        // Assert
        config.HistoryTableOverride.Should().Be("dbo.orders_history");
    }
}
