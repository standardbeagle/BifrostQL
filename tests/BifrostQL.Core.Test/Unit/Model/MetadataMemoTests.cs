using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Model;

/// <summary>
/// The per-table memo behind <see cref="PolicyConfigCollector.FromTable"/> and
/// <see cref="ComputedColumnConfigCollector.FromTable(IDbTable, IDbModel?)"/>: it serves the
/// parsed config while metadata is unchanged, and a write after load (as hosts and tests make)
/// is seen on the next call. The memo's version is process-wide, so a metadata write by any
/// test running in parallel drops it; the class runs alone so the same-instance facts hold.
/// </summary>
[Collection(MetadataMemoCollection.Name)]
public sealed class MetadataMemoTests
{
    private static DbTable Table() => new()
    {
        DbName = "orders",
        GraphQlName = "orders",
        NormalizedName = "orders",
        TableSchema = "dbo",
        TableType = "BASE TABLE",
        ColumnLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase),
        GraphQlLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void Policy_is_parsed_once_while_metadata_is_unchanged()
    {
        var table = Table();
        table.Metadata[MetadataKeys.Policy.ReadDeny] = "secret";

        PolicyConfigCollector.FromTable(table).Should().BeSameAs(PolicyConfigCollector.FromTable(table));
    }

    [Fact]
    public void A_policy_write_after_a_memoized_read_is_seen()
    {
        var table = Table();
        table.Metadata[MetadataKeys.Policy.ReadDeny] = "secret";
        PolicyConfigCollector.FromTable(table).ReadDenyColumns.Should().BeEquivalentTo(new[] { "secret" });

        table.Metadata[MetadataKeys.Policy.ReadDeny] = "secret,salary";

        PolicyConfigCollector.FromTable(table).ReadDenyColumns.Should().BeEquivalentTo(new[] { "secret", "salary" });
    }

    [Fact]
    public void A_table_built_with_its_own_metadata_dictionary_still_signals_writes()
    {
        var built = new DbTable
        {
            DbName = "orders", GraphQlName = "orders", NormalizedName = "orders",
            TableSchema = "dbo", TableType = "BASE TABLE",
            ColumnLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase),
            GraphQlLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase),
            Metadata = new Dictionary<string, object?> { [MetadataKeys.Policy.ReadDeny] = "secret" },
        };
        PolicyConfigCollector.FromTable(built).ReadDenyColumns.Should().BeEquivalentTo(new[] { "secret" });

        built.Metadata.Remove(MetadataKeys.Policy.ReadDeny);

        PolicyConfigCollector.FromTable(built).HasPolicy.Should().BeFalse();
    }

    [Fact]
    public void Computed_columns_follow_a_write_after_a_memoized_read()
    {
        var table = Table();
        table.Metadata[MetadataKeys.Computed.Expression] = "label:String:c0";
        var first = ComputedColumnConfigCollector.FromTable(table);
        ComputedColumnConfigCollector.FromTable(table).Should().BeSameAs(first);

        table.Metadata[MetadataKeys.Computed.Expression] = "label:String:c0;title:String:c1";

        ComputedColumnConfigCollector.FromTable(table).Select(c => c.Name)
            .Should().BeEquivalentTo(new[] { "label", "title" });
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MetadataMemoCollection
{
    public const string Name = "MetadataMemo";
}
