using System.Text.Json;
using BifrostQL.Core.QueryModel.VisualQuery;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.QueryModel;

/// <summary>
/// Contract tests for <see cref="VisualQuerySpec"/>. The acceptance criterion is
/// that the spec round-trips field-for-field through System.Text.Json using the
/// same camelCase options the Photino bridge uses (see NativeBridgeHost), so the
/// TypeScript mirror stays interchangeable with the C# records.
/// </summary>
public sealed class VisualQuerySpecTests
{
    // Mirrors NativeBridgeHost.JsonOptions: camelCase names, case-insensitive read.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static VisualQuerySpec SampleSpec() => new(
        Tables:
        [
            new VisualTable("dbo.users", "u"),
            new VisualTable("dbo.orders", null),
        ],
        Columns:
        [
            new VisualColumn("dbo.users", "id", null, Show: true, Sort: VisualSort.Asc, SortOrder: 1),
            new VisualColumn("dbo.users", "name", "user_name", Show: true, Sort: VisualSort.None, SortOrder: null),
            new VisualColumn("dbo.orders", "total", null, Show: false, Sort: VisualSort.Desc, SortOrder: 2),
        ],
        Joins:
        [
            new VisualJoin("dbo.orders", ["user_id", "tenant_id"], "dbo.users", ["id", "tenant_id"], VisualJoinType.Inner),
        ],
        Filter: new VisualFilter(
            VisualFilterOp.And,
            Children:
            [
                new VisualFilter(VisualFilterOp.Leaf, null,
                    new VisualCriterion("dbo.users", "id", VisualFilterOperator.Gt, 5)),
                new VisualFilter(VisualFilterOp.Or,
                    Children:
                    [
                        new VisualFilter(VisualFilterOp.Leaf, null,
                            new VisualCriterion("dbo.orders", "total", VisualFilterOperator.Between, new[] { 10, 100 })),
                        new VisualFilter(VisualFilterOp.Leaf, null,
                            new VisualCriterion("dbo.users", "name", VisualFilterOperator.Null, null)),
                    ],
                    Criterion: null),
            ],
            Criterion: null),
        RowLimit: 100);

    [Fact]
    public void Serialize_UsesCamelCaseFieldNames()
    {
        var json = JsonSerializer.Serialize(SampleSpec(), Options);

        json.Should().Contain("\"tables\"");
        json.Should().Contain("\"leftColumns\"");
        json.Should().Contain("\"rightColumns\"");
        json.Should().Contain("\"sortOrder\"");
        json.Should().Contain("\"rowLimit\"");
        json.Should().Contain("\"criterion\"");
        // No PascalCase leakage.
        json.Should().NotContain("\"Tables\"");
        json.Should().NotContain("\"LeftColumns\"");
    }

    [Fact]
    public void RoundTrip_IsByteForByteStable()
    {
        var spec = SampleSpec();

        var json = JsonSerializer.Serialize(spec, Options);
        var back = JsonSerializer.Deserialize<VisualQuerySpec>(json, Options)!;
        var json2 = JsonSerializer.Serialize(back, Options);

        json2.Should().Be(json);
    }

    [Fact]
    public void RoundTrip_PreservesStructuralFields()
    {
        var spec = SampleSpec();

        var back = JsonSerializer.Deserialize<VisualQuerySpec>(
            JsonSerializer.Serialize(spec, Options), Options)!;

        back.Tables.Should().HaveCount(2);
        back.Tables[0].Alias.Should().Be("u");
        back.Tables[1].Alias.Should().BeNull();

        back.Columns.Should().HaveCount(3);
        back.Columns[0].Sort.Should().Be(VisualSort.Asc);
        back.Columns[2].Show.Should().BeFalse();

        // Composite-FK join: parallel column arrays preserved in order.
        back.Joins.Should().ContainSingle();
        back.Joins[0].LeftColumns.Should().Equal("user_id", "tenant_id");
        back.Joins[0].RightColumns.Should().Equal("id", "tenant_id");
        back.Joins[0].Type.Should().Be(VisualJoinType.Inner);

        back.Filter!.Op.Should().Be(VisualFilterOp.And);
        back.Filter.Children.Should().HaveCount(2);
        back.RowLimit.Should().Be(100);
    }

    [Fact]
    public void NullableArms_OmittedAndOptional()
    {
        // A minimal spec — empty collections, no filter, no row limit — must
        // round-trip without throwing.
        var spec = new VisualQuerySpec([], [], [], Filter: null, RowLimit: null);

        var json = JsonSerializer.Serialize(spec, Options);
        var back = JsonSerializer.Deserialize<VisualQuerySpec>(json, Options)!;

        back.Filter.Should().BeNull();
        back.RowLimit.Should().BeNull();
        back.Tables.Should().BeEmpty();
    }
}
