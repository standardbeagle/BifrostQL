using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using FluentAssertions;
using NSubstitute;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Tests for the READ-side enum mapping applied by <see cref="ReaderEnum"/>:
/// a stored enum-column value is mapped to its GraphQL enum name; drift
/// (a stored value that is not a declared member) resolves to null while
/// the rest of the row is unaffected; non-enum columns pass through unchanged.
/// </summary>
public class ReaderEnumEnumMappingTests
{
    private const string EnumTable = "OrderStatus";
    private const string ValueColumn = "Code";

    /// <summary>
    /// OrderStatus is the lookup/enum table (value column = Code).
    /// Orders.status carries enum-ref metadata → enum by override.
    /// Orders.id is a plain primary-key column → not enum.
    /// </summary>
    private static IDbModel BuildModel()
    {
        return DbModelTestFixture.Create()
            .WithTable(EnumTable, t => t
                .WithSchema("dbo")
                .WithMetadata(EnumTableConfig.MetadataKey, ValueColumn)
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Code", "varchar")
                .WithColumn("Label", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("status", "varchar")
                .WithColumnMetadata("status", MetadataKeys.Enum.Ref, "dbo.OrderStatus"))
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

    /// <summary>
    /// Builds a ReaderEnum over two hand-built rows:
    ///   row 0: id=5, status="active"  (a declared enum member)
    ///   row 1: id=6, status="gone"    (drift — not a declared member)
    /// </summary>
    private static ReaderEnum BuildReader(IDbModel model, EnumColumnMap map)
    {
        var orders = model.GetTableFromDbName("Orders");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(orders)
            .Build();

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = 0,
            ["status"] = 1,
        };
        var data = new List<object?[]>
        {
            new object?[] { 5, "active" },
            new object?[] { 6, "gone" },
        };
        var tables = new Dictionary<string, (IDictionary<string, int> index, IList<object?[]> data)>
        {
            [query.KeyName] = (index, data),
        };

        return new ReaderEnum(query, tables, map);
    }

    private static IBifrostFieldContext FieldContext(string fieldName, string? alias = null)
    {
        var ctx = Substitute.For<IBifrostFieldContext>();
        ctx.FieldName.Returns(fieldName);
        ctx.FieldAlias.Returns(alias);
        return ctx;
    }

    /// <summary>
    /// Builds a ReaderEnum where the enum column is projected under an alias
    /// (e.g. query <c>{ orders { current: status } }</c>). The data index is
    /// keyed by the projected alias (<c>AS current</c>), while enum resolution
    /// must still key on the SCHEMA field name (<c>status</c>).
    /// </summary>
    private static ReaderEnum BuildAliasedReader(IDbModel model, EnumColumnMap map)
    {
        var orders = model.GetTableFromDbName("Orders");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(orders)
            .Build();

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = 0,
            ["current"] = 1,
        };
        var data = new List<object?[]>
        {
            new object?[] { 5, "active" },
        };
        var tables = new Dictionary<string, (IDictionary<string, int> index, IList<object?[]> data)>
        {
            [query.KeyName] = (index, data),
        };

        return new ReaderEnum(query, tables, map);
    }

    [Fact]
    public async Task Get_AliasedEnumColumn_MapsToEnumName()
    {
        var model = BuildModel();
        var reader = BuildAliasedReader(model, BuildMap(model));

        // Field "status" aliased as "current"; data index is keyed by the alias,
        // enum resolution must key on the un-aliased schema field name.
        var result = await reader.Get(0, FieldContext("status", alias: "current"));

        result.Should().Be("ACTIVE");
    }

    [Fact]
    public async Task Get_DeclaredEnumValue_MapsToEnumName()
    {
        var model = BuildModel();
        var reader = BuildReader(model, BuildMap(model));

        var result = await reader.Get(0, FieldContext("status"));

        result.Should().Be("ACTIVE");
    }

    [Fact]
    public async Task Get_DriftValue_ResolvesToNull()
    {
        var model = BuildModel();
        var reader = BuildReader(model, BuildMap(model));

        var result = await reader.Get(1, FieldContext("status"));

        result.Should().BeNull();
    }

    [Fact]
    public async Task Get_NonEnumColumn_PassesThroughUnchanged()
    {
        var model = BuildModel();
        var reader = BuildReader(model, BuildMap(model));

        var result = await reader.Get(0, FieldContext("id"));

        result.Should().Be(5);
    }

    /// <summary>
    /// A single-link (many-to-one) projection reads scalar columns through
    /// <see cref="SingleRowLookup"/>, which carries the connected table's DbName
    /// and routes scalar reads through the parent ReaderEnum's mapping. The same
    /// value→name mapping and drift→null policy must apply at this depth.
    /// </summary>
    private static SingleRowLookup BuildSingleLink(ReaderEnum root, object? statusValue)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = 0,
        };
        var row = new object?[] { statusValue };
        return new SingleRowLookup(row, index, root, "Orders");
    }

    [Fact]
    public async Task Nested_SingleLink_DeclaredEnumValue_MapsToEnumName()
    {
        var model = BuildModel();
        var root = BuildReader(model, BuildMap(model));
        var nested = BuildSingleLink(root, "active");

        var result = await nested.Get(FieldContext("status"));

        result.Should().Be("ACTIVE");
    }

    [Fact]
    public async Task Nested_SingleLink_DriftValue_ResolvesToNull()
    {
        var model = BuildModel();
        var root = BuildReader(model, BuildMap(model));
        var nested = BuildSingleLink(root, "gone");

        var result = await nested.Get(FieldContext("status"));

        result.Should().BeNull();
    }
}
