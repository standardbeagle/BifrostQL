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

    private static DbTable TableWith(IDictionary<string, object?> metadata, params ColumnDto[] columns) => new()
    {
        DbName = "orders", GraphQlName = "orders", NormalizedName = "orders",
        TableSchema = "dbo", TableType = "BASE TABLE",
        ColumnLookup = columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase),
        GraphQlLookup = columns.ToDictionary(c => c.GraphQlName, StringComparer.OrdinalIgnoreCase),
        Metadata = metadata,
    };

    [Fact]
    public void A_table_built_from_a_plain_dictionary_hits_the_memo()
    {
        var built = TableWith(new Dictionary<string, object?> { [MetadataKeys.Policy.ReadDeny] = "secret" });

        built.Metadata.Should().BeOfType<MetadataDictionary>();
        PolicyConfigCollector.FromTable(built).Should().BeSameAs(PolicyConfigCollector.FromTable(built));
    }

    [Fact]
    public void A_write_through_the_dictionary_handed_to_a_table_init_cannot_desync_its_policy()
    {
        // The init setter used to wrap this reference, so a write through it changed the
        // table's metadata without bumping the version and the memo kept serving the policy
        // parsed before it: Metadata said "secret,salary" while FromTable denied only "secret".
        var source = new Dictionary<string, object?> { [MetadataKeys.Policy.ReadDeny] = "secret" };
        var built = TableWith(source);
        PolicyConfigCollector.FromTable(built).ReadDenyColumns.Should().BeEquivalentTo(new[] { "secret" });

        source[MetadataKeys.Policy.ReadDeny] = "secret,salary";

        var stored = built.Metadata[MetadataKeys.Policy.ReadDeny]!.ToString()!.Split(',');
        PolicyConfigCollector.FromTable(built).ReadDenyColumns.Should().BeEquivalentTo(stored);
    }

    [Fact]
    public void A_write_through_the_dictionary_handed_to_a_column_init_cannot_desync_the_table_policy()
    {
        var source = new Dictionary<string, object?> { [MetadataKeys.Policy.DenyMode] = "null" };
        var column = new ColumnDto { ColumnName = "salary", GraphQlName = "salary", DataType = "int", Metadata = source };
        var built = TableWith(new Dictionary<string, object?> { [MetadataKeys.Policy.ReadDeny] = "salary" }, column);
        PolicyConfigCollector.FromTable(built).ColumnDenyModes["salary"].Should().Be("null");

        source[MetadataKeys.Policy.DenyMode] = "refuse";

        PolicyConfigCollector.FromTable(built).ColumnDenyModes["salary"]
            .Should().Be(column.Metadata[MetadataKeys.Policy.DenyMode]!.ToString());
    }

    [Fact]
    public void A_plain_dictionary_keeps_its_key_comparer_when_copied()
    {
        var built = TableWith(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Policy-Read-Deny"] = "x" });

        built.Metadata.ContainsKey("policy-read-deny").Should().BeTrue();
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
