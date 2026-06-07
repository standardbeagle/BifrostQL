using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// Tests for <see cref="EnumColumnMap"/> — the pure lookup-table-enum resolver
/// (Approach A: the referencing column holds the value string, no id translation).
/// </summary>
public class EnumColumnMapTests
{
    private const string EnumTable = "OrderStatus";
    private const string ValueColumn = "Code";

    /// <summary>
    /// Scenario: OrderStatus is the lookup/enum table (value column = Code).
    /// Orders.StatusCode has an FK to OrderStatus.Code (value column) → enum by FK.
    /// Orders.PriorityName has enum-ref metadata → enum by override (no FK).
    /// Orders.CustomerId has an FK to Customers (NOT an enum table) → not enum.
    /// Orders.Total is a plain column → not enum.
    /// </summary>
    private static IDbModel BuildModel()
    {
        return DbModelTestFixture.Create()
            .WithTable("Customers", t => t
                .WithSchema("dbo")
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .WithTable(EnumTable, t => t
                .WithSchema("dbo")
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Code", "varchar")
                .WithColumn("Label", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("CustomerId", "int")
                .WithColumn("StatusCode", "varchar")
                .WithColumn("PriorityName", "varchar")
                .WithColumnMetadata("PriorityName", MetadataKeys.Enum.Ref, "dbo.OrderStatus")
                .WithColumn("Total", "decimal"))
            // FK to the value column (Approach A) — enum by FK
            .WithForeignKey("FK_Orders_StatusCode", "Orders", "StatusCode", EnumTable, ValueColumn)
            // FK to a non-enum table — not an enum
            .WithForeignKey("FK_Orders_Customer", "Orders", "CustomerId", "Customers", "Id")
            .Build();
    }

    private static EnumColumnMap BuildMap(IDbModel model)
    {
        var entries = EnumValueSanitizer.SanitizeAll(new[] { "active", "pending", "on hold" });
        var enumValues = new Dictionary<string, IReadOnlyList<EnumValueEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            [EnumTable] = entries,
        };
        var resolvedValueColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [EnumTable] = ValueColumn,
        };
        return EnumColumnMap.Build(model, enumValues, resolvedValueColumns);
    }

    [Fact]
    public void Build_FkTargetingValueColumn_DetectedAsEnum()
    {
        var map = BuildMap(BuildModel());

        map.TryGetEnumType("Orders", "StatusCode", out var enumName).Should().BeTrue();
        enumName.Should().Be("OrderStatusValues");
        map.HasAnyFor("Orders").Should().BeTrue();
    }

    [Fact]
    public void Build_EnumRefMetadata_OverridesAndDetectedAsEnum()
    {
        var map = BuildMap(BuildModel());

        map.TryGetEnumType("Orders", "PriorityName", out var enumName).Should().BeTrue();
        enumName.Should().Be("OrderStatusValues");
    }

    [Fact]
    public void Build_NonEnumColumns_NotDetected()
    {
        var map = BuildMap(BuildModel());

        // plain column
        map.TryGetEnumType("Orders", "Total", out _).Should().BeFalse();
        // FK to a non-enum table
        map.TryGetEnumType("Orders", "CustomerId", out _).Should().BeFalse();
    }

    [Fact]
    public void EnumTables_ExposesEnumTableMetadata()
    {
        var map = BuildMap(BuildModel());

        map.EnumTables.Should().ContainKey(EnumTable);
        map.EnumTables[EnumTable].EnumName.Should().Be("OrderStatusValues");
        map.EnumTables[EnumTable].Entries.Should().HaveCount(3);
    }

    [Fact]
    public void ValueToName_NameToValue_RoundTrip_WithDriftToNull()
    {
        var map = BuildMap(BuildModel());

        map.ValueToName("Orders", "StatusCode", "active").Should().Be("ACTIVE");
        map.NameToValue("Orders", "StatusCode", "ACTIVE").Should().Be("active");

        // value containing a space sanitizes to ON_HOLD
        map.ValueToName("Orders", "StatusCode", "on hold").Should().Be("ON_HOLD");
        map.NameToValue("Orders", "StatusCode", "ON_HOLD").Should().Be("on hold");

        // drift → null both directions
        map.ValueToName("Orders", "StatusCode", "deleted").Should().BeNull();
        map.NameToValue("Orders", "StatusCode", "UNKNOWN").Should().BeNull();

        // non-enum column → null
        map.ValueToName("Orders", "Total", "active").Should().BeNull();
    }

    [Fact]
    public void RewriteFilterValues_TranslatesEqAndIn_RecursivelyThroughNestedAnd()
    {
        var map = BuildMap(BuildModel());

        // { and: [ { StatusCode: { _eq: ACTIVE } },
        //          { and: [ { StatusCode: { _in: [PENDING, ON_HOLD] } } ] } ] }
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            ["and"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["StatusCode"] = new Dictionary<string, object?> { ["_eq"] = "ACTIVE" },
                },
                new Dictionary<string, object?>
                {
                    ["and"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["StatusCode"] = new Dictionary<string, object?>
                            {
                                ["_in"] = new List<object?> { "PENDING", "ON_HOLD" },
                            },
                        },
                    },
                },
            },
        }, "Orders");

        map.RewriteFilterValues(filter, "Orders");

        // _eq operand translated: ACTIVE -> active
        var eqNode = filter.And[0].Next!;
        eqNode.RelationName.Should().Be("_eq");
        eqNode.Value.Should().Be("active");

        // _in operand translated recursively through the nested And: PENDING/ON_HOLD -> pending/on hold
        var inNode = filter.And[1].And[0].Next!;
        inNode.RelationName.Should().Be("_in");
        inNode.Value.Should().BeAssignableTo<IEnumerable<object?>>();
        ((IEnumerable<object?>)inNode.Value!).Should().BeEquivalentTo(new object?[] { "pending", "on hold" });
    }
}
