using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Retention;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for Retention slice 1 — the metadata contract parsed from <c>retain</c> /
/// <c>ttl</c> into a typed <see cref="RetentionConfig"/>. The purge scheduler (a later
/// slice) consumes this config, so its parse and fail-fast behavior are pinned here. The
/// load-bearing invariant under test: <c>retain</c> anchors ONLY on the soft-delete column
/// (it can never purge a live row), while destroying live rows requires the separate,
/// explicit <c>ttl</c> key with its own <c>after &lt;column&gt;</c> anchor.
/// </summary>
public class RetentionConfigTests
{
    // Columns mirror a small table: deleted_at/created_at are timestamps, name is not.
    private static IDbTable TableWithMetadata(params (string key, object? value)[] metadata)
    {
        var table = Substitute.For<IDbTable>();
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in metadata)
            dict[key] = value;
        var columns = new (string name, string type)[]
            {
                ("id", "int"),
                ("deleted_at", "datetime2"),
                ("created_at", "datetime2"),
                ("name", "nvarchar"),
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
        table.DbName.Returns("orders");
        table.TableSchema.Returns("dbo");
        table.Metadata.Returns(dict);
        table.ColumnLookup.Returns(columns);
        table.GetMetadataValue(Arg.Any<string>())
            .Returns(ci => dict.TryGetValue((string)ci[0], out var v) ? v?.ToString() : null);
        return table;
    }

    [Fact]
    public void FromTable_NoKeys_ReturnsNone()
    {
        var table = TableWithMetadata();

        var config = RetentionConfig.FromTable(table);

        config.HasRetention.Should().BeFalse();
        config.Should().BeSameAs(RetentionConfig.None);
    }

    [Fact]
    public void FromTable_Retain_ParsesWindowAndAnchorsOnSoftDelete()
    {
        var table = TableWithMetadata(
            (MetadataKeys.SoftDelete.Column, "deleted_at"),
            (MetadataKeys.Retention.Retain, "90d"));

        var config = RetentionConfig.FromTable(table);

        config.PurgesSoftDeleted.Should().BeTrue();
        config.RetainWindow.Should().Be(TimeSpan.FromDays(90));
        config.RetainAnchorColumn.Should().Be("deleted_at");
        // retain alone never expires live rows.
        config.ExpiresLiveRows.Should().BeFalse();
    }

    [Fact]
    public void FromTable_RetainHours_ParsesHourWindow()
    {
        var table = TableWithMetadata(
            (MetadataKeys.SoftDelete.Column, "deleted_at"),
            (MetadataKeys.Retention.Retain, "12h"));

        var config = RetentionConfig.FromTable(table);

        config.RetainWindow.Should().Be(TimeSpan.FromHours(12));
    }

    [Fact]
    public void FromTable_Ttl_ParsesWindowAndExplicitAnchor()
    {
        var table = TableWithMetadata((MetadataKeys.Retention.Ttl, "30d after created_at"));

        var config = RetentionConfig.FromTable(table);

        config.ExpiresLiveRows.Should().BeTrue();
        config.TtlWindow.Should().Be(TimeSpan.FromDays(30));
        config.TtlAnchorColumn.Should().Be("created_at");
        // ttl does not imply soft-deleted purge.
        config.PurgesSoftDeleted.Should().BeFalse();
    }

    [Fact]
    public void FromTable_RetainWithoutSoftDeleteColumn_Throws()
    {
        // retain hard-purges already-soft-deleted rows; with no soft-delete column it has
        // nothing to anchor on and must never touch live rows.
        var table = TableWithMetadata((MetadataKeys.Retention.Retain, "90d"));

        var act = () => RetentionConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.SoftDelete.Column}*");
    }

    [Fact]
    public void FromTable_RetainWithAfterClause_Throws()
    {
        // retain must not be redirected onto a live-row column via 'after' — that is exactly
        // the destructive live-row expiry ttl exists to make explicit.
        var table = TableWithMetadata(
            (MetadataKeys.SoftDelete.Column, "deleted_at"),
            (MetadataKeys.Retention.Retain, "90d after created_at"));

        var act = () => RetentionConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*bare duration*");
    }

    [Fact]
    public void FromTable_TtlWithoutAfterAnchor_Throws()
    {
        var table = TableWithMetadata((MetadataKeys.Retention.Ttl, "30d"));

        var act = () => RetentionConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{RetentionConfig.AfterKeyword}*");
    }

    [Theory]
    [InlineData("90")]      // no unit
    [InlineData("d")]       // no magnitude
    [InlineData("0d")]      // zero window
    [InlineData("-5d")]     // negative window
    [InlineData("90x")]     // unknown unit
    [InlineData("abc")]     // non-numeric
    [InlineData("9.5d")]    // non-integer magnitude
    public void FromTable_MalformedDuration_Throws(string duration)
    {
        var table = TableWithMetadata((MetadataKeys.Retention.Ttl, $"{duration} after created_at"));

        var act = () => RetentionConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*invalid duration*");
    }

    [Theory]
    [InlineData("1d")]
    [InlineData("365d")]
    [InlineData("24h")]
    public void FromTable_ValidDurations_Parse(string duration)
    {
        var table = TableWithMetadata((MetadataKeys.Retention.Ttl, $"{duration} after created_at"));

        var act = () => RetentionConfig.FromTable(table);

        act.Should().NotThrow();
    }

    [Fact]
    public void FromTable_TtlAnchorMissingColumn_Throws()
    {
        var table = TableWithMetadata((MetadataKeys.Retention.Ttl, "30d after nope"));

        var act = () => RetentionConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*nope*does not exist*");
    }

    [Fact]
    public void FromTable_TtlAnchorNonTimestampColumn_Throws()
    {
        // 'name' is nvarchar — a purge cutoff cannot be measured against it.
        var table = TableWithMetadata((MetadataKeys.Retention.Ttl, "30d after name"));

        var act = () => RetentionConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*name*timestamp*");
    }

    [Fact]
    public void FromTable_RetainAnchorNonTimestampColumn_Throws()
    {
        // A soft-delete column that is not a timestamp cannot anchor a retain window.
        var table = TableWithMetadata(
            (MetadataKeys.SoftDelete.Column, "name"),
            (MetadataKeys.Retention.Retain, "90d"));

        var act = () => RetentionConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*name*timestamp*");
    }

    [Fact]
    public void FromTable_HugeButParseableDuration_ThrowsFriendlyError_NotOverflow()
    {
        // A magnitude that parses as an int but overflows TimeSpan (max ~10.7M days)
        // must surface as the friendly InvalidOperationException the model-load
        // validator already catches — NOT a raw OverflowException that would escape
        // ValidateRetention (which filters InvalidOperationException) unhandled, and
        // NOT abort the purge sweep that calls FromTable directly.
        var table = TableWithMetadata(
            (MetadataKeys.SoftDelete.Column, "deleted_at"),
            (MetadataKeys.Retention.Retain, "99999999d"));

        var act = () => RetentionConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*invalid duration*");
    }
}
