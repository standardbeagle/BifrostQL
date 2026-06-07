using System.Reflection;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using FluentAssertions;
using GraphQL.Types;
using NSubstitute;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Proves the <see cref="SqlExecutionManager"/> wiring rewrites enum-name filter
/// operands to their stored database values before SQL is generated, for the root
/// table and every nested (tree of) join. The rewrite logic itself is covered by
/// <c>EnumColumnMapTests</c>; these tests exercise the integration point.
/// </summary>
public class EnumFilterRewriteTests
{
    private const string EnumTable = "OrderStatus";
    private const string ValueColumn = "Code";

    private static DbModel BuildModel()
    {
        var model = (DbModel)DbModelTestFixture.Create()
            .WithTable(EnumTable, t => t
                .WithSchema("dbo")
                .WithMetadata(EnumTableConfig.MetadataKey, ValueColumn)
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Code", "varchar")
                .WithColumn("Label", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("StatusCode", "varchar")
                .WithColumn("Total", "decimal"))
            .WithForeignKey("FK_Orders_StatusCode", "Orders", "StatusCode", EnumTable, ValueColumn)
            .Build();

        model.EnumColumns = BuildMap(model);
        return model;
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

    private static TableFilter StatusEq(string name) =>
        TableFilter.FromObject(new Dictionary<string, object?>
        {
            ["StatusCode"] = new Dictionary<string, object?> { ["_eq"] = name },
        }, "Orders");

    private static SqlExecutionManager NewManager(IDbModel model) =>
        new(model, Substitute.For<ISchema>());

    private static void InvokeRewrite(SqlExecutionManager manager, GqlObjectQuery query)
    {
        var method = typeof(SqlExecutionManager).GetMethod(
            "ApplyEnumFilterRewrite",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull("SqlExecutionManager must expose the extracted rewrite hook");
        method!.Invoke(manager, new object?[] { query });
    }

    [Fact]
    public void ApplyEnumFilterRewrite_RootFilter_NameRewrittenToValue()
    {
        var model = BuildModel();
        var manager = NewManager(model);
        var query = new GqlObjectQuery
        {
            DbTable = model.GetTableFromDbName("Orders"),
            TableName = "Orders",
            SchemaName = "dbo",
            GraphQlName = "Orders",
            Filter = StatusEq("ACTIVE"),
        };

        InvokeRewrite(manager, query);

        // ACTIVE (GraphQL enum name) -> active (stored db value)
        query.Filter!.Next!.Value.Should().Be("active");
    }

    [Fact]
    public void ApplyEnumFilterRewrite_NestedJoinFilter_RewrittenAcrossTree()
    {
        var model = BuildModel();
        var manager = NewManager(model);

        // Root Orders -> join (self) Orders -> deeper join (self) Orders, each
        // carrying its own status filter, to prove the recursive tree walk.
        var deep = new GqlObjectQuery
        {
            DbTable = model.GetTableFromDbName("Orders"),
            TableName = "Orders",
            SchemaName = "dbo",
            GraphQlName = "Orders",
            Filter = StatusEq("ON_HOLD"),
        };
        var mid = new GqlObjectQuery
        {
            DbTable = model.GetTableFromDbName("Orders"),
            TableName = "Orders",
            SchemaName = "dbo",
            GraphQlName = "Orders",
            Filter = StatusEq("PENDING"),
            Joins = { new TableJoin { Name = "deep", FromColumn = "Id", ConnectedTable = deep } },
        };
        var root = new GqlObjectQuery
        {
            DbTable = model.GetTableFromDbName("Orders"),
            TableName = "Orders",
            SchemaName = "dbo",
            GraphQlName = "Orders",
            Filter = StatusEq("ACTIVE"),
            Joins = { new TableJoin { Name = "mid", FromColumn = "Id", ConnectedTable = mid } },
        };

        InvokeRewrite(manager, root);

        root.Filter!.Next!.Value.Should().Be("active");
        mid.Filter!.Next!.Value.Should().Be("pending");
        deep.Filter!.Next!.Value.Should().Be("on hold");
    }

    [Fact]
    public void EnumColumns_Wiring_RewriteContract_NameToValue()
    {
        // Validates the contract the manager relies on: the map keyed by the
        // table DbName translates the GraphQL field's enum-name operand in place.
        var model = BuildModel();
        var filter = StatusEq("ACTIVE");

        model.EnumColumns!.RewriteFilterValues(filter, "Orders");

        filter.Next!.Value.Should().Be("active");
    }
}
