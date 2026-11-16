using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using FluentAssertions;
using NSubstitute;
using System.Data.Common;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// Lookup-table enums are addressed by schema-qualified table identity, never by a
/// bare DbName. The fixture is the minimal two-schema repro: <c>sales.status</c> is an
/// enum lookup table and <c>hr.status</c> is a plain table with the same name; both
/// schemas have an <c>orders</c> table whose <c>state</c> column has a foreign key to
/// its own schema's <c>status.code</c>. Keyed by bare name, the loader stored the
/// values under "status", the map build then met <c>hr.status</c>, found "its"
/// values and dereferenced a null enum config (the endpoint never started), and
/// <c>hr.orders.state</c> shared <c>sales.orders.state</c>'s column entry, so hr
/// reads, filters and writes were enum-translated.
/// </summary>
public sealed class EnumTableSchemaIdentityTests
{
    private static readonly string[] Rules =
    {
        "sales.status { enum: code }",
    };

    private static DbTable MakeTable(string schema, string name, Action<DbModelTestFixture.TableBuilder> configure)
    {
        var builder = new DbModelTestFixture.TableBuilder(name)
            .WithSchema(schema)
            .WithGraphQlName($"{schema}_{name}");
        configure(builder);
        return builder.Build();
    }

    private static DbForeignKey StateFk(string schema) => new()
    {
        ConstraintName = $"FK_{schema}_orders_state",
        ChildTableSchema = schema,
        ChildTableName = "orders",
        ChildColumnNames = new[] { "state" },
        ParentTableSchema = schema,
        ParentTableName = "status",
        ParentColumnNames = new[] { "code" },
    };

    private static SchemaData TwoSchemaRead(Action<DbModelTestFixture.TableBuilder>? hrOrdersExtra = null,
        Action<DbModelTestFixture.TableBuilder>? salesOrdersExtra = null)
    {
        var tables = new List<IDbTable>
        {
            MakeTable("sales", "status", t => t.WithPrimaryKey("id").WithColumn("code", "varchar")),
            MakeTable("hr", "status", t => t.WithPrimaryKey("id").WithColumn("code", "varchar")),
            MakeTable("sales", "orders", t =>
            {
                t.WithPrimaryKey("id").WithColumn("state", "varchar");
                salesOrdersExtra?.Invoke(t);
            }),
            MakeTable("hr", "orders", t =>
            {
                t.WithPrimaryKey("id").WithColumn("state", "varchar");
                hrOrdersExtra?.Invoke(t);
            }),
        };
        return new SchemaData(
            new Dictionary<ColumnRef, List<ColumnConstraintDto>>(),
            Array.Empty<ColumnDto>(),
            tables,
            new[] { StateFk("sales"), StateFk("hr") },
            Array.Empty<DbIndex>());
    }

    private static DbModelLoader MakeLoader()
    {
        var connFactory = Substitute.For<IDbConnFactory>();
        connFactory.GetConnection().Returns(Substitute.For<DbConnection>());
        connFactory.TypeMapper.Returns(SqlServerTypeMapper.Instance);
        return new DbModelLoader(connFactory, new MetadataLoader(Rules));
    }

    /// <summary>
    /// The load result exactly as <see cref="EnumValueLoader"/> keys it: one entry per
    /// enum-configured table, keyed by <see cref="EnumTableKey.Of"/>.
    /// </summary>
    private static EnumValueLoader.LoadResult LoadResultFor(IDbModel baseModel)
    {
        var values = new Dictionary<EnumTableKey, IReadOnlyList<EnumValueEntry>>();
        var columns = new Dictionary<EnumTableKey, string>();
        foreach (var table in baseModel.Tables)
        {
            var cfg = EnumTableConfig.FromTable(table);
            if (cfg == null)
                continue;
            values[EnumTableKey.Of(table)] = EnumValueSanitizer.SanitizeAll(new[] { "active", "on hold" });
            columns[EnumTableKey.Of(table)] = cfg.ResolveValueColumn(table)!;
        }
        return new EnumValueLoader.LoadResult(values, columns);
    }

    private static (IDbModel Model, GraphQL.Types.ISchema Schema) BuildEndpoint(SchemaData read)
    {
        var loader = MakeLoader();
        var baseModel = loader.BuildModel(read, new MetadataLoader(Rules));
        var cache = new ProfileModelCache(
            loader, read, Rules, additionalMetadata: null, registry: null, enumValues: LoadResultFor(baseModel));
        return cache.GetFor(null);
    }

    [Fact]
    public void ProfileBuild_PlainTableSharesEnumTableName_EndpointSchemaBuilds()
    {
        var read = TwoSchemaRead();

        var act = () => BuildEndpoint(read);

        var (model, _) = act.Should().NotThrow(
            "hr.status is not an enum table, so its bare-name collision with sales.status must not reach the enum config").Subject;
        model.EnumColumns!.EnumTables.Keys.Should().Equal(new EnumTableKey("sales", "status"));
    }

    [Fact]
    public void Lookups_SameNamedTableInOtherSchema_IsNotEnumTranslated()
    {
        var (model, _) = BuildEndpoint(TwoSchemaRead());
        var map = model.EnumColumns!;
        var salesOrders = model.GetTableFromDbName("sales", "orders");
        var hrOrders = model.GetTableFromDbName("hr", "orders");

        // Control: the sales column is an enum, in every direction.
        map.TryGetEnumType(salesOrders, "state", out var enumName).Should().BeTrue();
        enumName.Should().Be("sales_statusValues");
        map.ValueToName(salesOrders, "state", "on hold").Should().Be("ON_HOLD");

        // hr.orders.state references hr.status, a plain table.
        map.HasAnyFor(hrOrders).Should().BeFalse();
        map.TryGetEnumType(hrOrders, "state", out _).Should().BeFalse();
        map.ValueToName(hrOrders, "state", "on hold").Should().BeNull();
        map.NameToValue(hrOrders, "state", "ON_HOLD").Should().BeNull();
    }

    [Fact]
    public void FilterRewrite_SameNamedTableInOtherSchema_LeavesOperandAlone()
    {
        var (model, _) = BuildEndpoint(TwoSchemaRead());
        var map = model.EnumColumns!;

        TableFilter StateEq(IDbTable table) => TableFilter.FromObject(new Dictionary<string, object?>
        {
            ["state"] = new Dictionary<string, object?> { ["_eq"] = "ON_HOLD" },
        }, table);

        var salesOrders = model.GetTableFromDbName("sales", "orders");
        var salesFilter = StateEq(salesOrders);
        map.RewriteFilterValues(salesFilter, salesOrders);
        salesFilter.Next!.Value.Should().Be("on hold", "control: the enum column's operand is translated");

        var hrOrders = model.GetTableFromDbName("hr", "orders");
        var hrFilter = StateEq(hrOrders);
        map.RewriteFilterValues(hrFilter, hrOrders);
        hrFilter.Next!.Value.Should().Be("ON_HOLD");
    }

    [Fact]
    public async Task MutationTransformer_SameNamedTableInOtherSchema_DoesNotApply()
    {
        var (model, _) = BuildEndpoint(TwoSchemaRead());
        var transformer = new EnumValueMutationTransformer();
        var context = new MutationTransformContext { Model = model, UserContext = new Dictionary<string, object?>() };
        var salesOrders = model.GetTableFromDbName("sales", "orders");
        var hrOrders = model.GetTableFromDbName("hr", "orders");

        transformer.AppliesTo(salesOrders, MutationType.Insert, context).Should().BeTrue("control");
        var sales = await transformer.TransformAsync(salesOrders, MutationType.Insert,
            new Dictionary<string, object?> { ["id"] = 1, ["state"] = "ON_HOLD" }, context);
        sales.Data["state"].Should().Be("on hold");

        transformer.AppliesTo(hrOrders, MutationType.Insert, context).Should().BeFalse();
        var hr = await transformer.TransformAsync(hrOrders, MutationType.Insert,
            new Dictionary<string, object?> { ["id"] = 1, ["state"] = "ON_HOLD" }, context);
        hr.Errors.Should().BeEmpty();
        hr.Data["state"].Should().Be("ON_HOLD");
    }

    [Fact]
    public void EnumRef_BareName_ResolvesInTheColumnsOwnSchema()
    {
        var read = TwoSchemaRead(
            hrOrdersExtra: t => t.WithColumn("kind", "varchar").WithColumnMetadata("kind", MetadataKeys.Enum.Ref, "status"),
            salesOrdersExtra: t => t.WithColumn("kind", "varchar").WithColumnMetadata("kind", MetadataKeys.Enum.Ref, "status"));
        var (model, _) = BuildEndpoint(read);
        var map = model.EnumColumns!;

        map.TryGetEnumType(model.GetTableFromDbName("sales", "orders"), "kind", out _).Should().BeTrue(
            "control: the bare ref names sales.status from a sales table");
        map.TryGetEnumType(model.GetTableFromDbName("hr", "orders"), "kind", out _).Should().BeFalse(
            "the bare ref names hr.status from an hr table, and hr.status is not an enum");
    }

    [Fact]
    public void EnumRef_QualifiedName_ResolvesAcrossSchemas()
    {
        var read = TwoSchemaRead(
            hrOrdersExtra: t => t.WithColumn("kind", "varchar").WithColumnMetadata("kind", MetadataKeys.Enum.Ref, "sales.status"));
        var (model, _) = BuildEndpoint(read);

        model.EnumColumns!.TryGetEnumType(model.GetTableFromDbName("hr", "orders"), "kind", out var enumName)
            .Should().BeTrue();
        enumName.Should().Be("sales_statusValues");
    }
}
