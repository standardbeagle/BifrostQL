using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using FluentAssertions;

namespace BifrostQL.Core.Test.Modules;

public sealed class ContextValueCoercionTests
{
    [Theory]
    [InlineData("42")]
    [MemberData(nameof(ContextValues))]
    public void TenantFilter_CoercesContextValueToBigint(object value)
    {
        var model = TenantModel();
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = new Dictionary<string, object?> { ["tenant_id"] = value },
            QueryType = QueryType.Standard
        };

        var filter = new TenantFilterTransformer().GetAdditionalFilter(model.GetTableFromDbName("Orders"), context);

        filter!.Next!.Value.Should().BeOfType<long>().Which.Should().Be(42L);
    }

    [Theory]
    [InlineData("42")]
    [MemberData(nameof(ContextValues))]
    public async Task TenantMutation_CoercesFilterAndInsertPinToBigint(object value)
    {
        var model = TenantModel();
        var table = model.GetTableFromDbName("Orders");
        var context = new MutationTransformContext
        {
            Model = model,
            UserContext = new Dictionary<string, object?> { ["tenant_id"] = value }
        };
        var transformer = new TenantMutationTransformer();

        var update = await transformer.TransformAsync(table, MutationType.Update,
            new Dictionary<string, object?>(), context);
        var insert = await transformer.TransformAsync(table, MutationType.Insert,
            new Dictionary<string, object?>(), context);

        update.AdditionalFilter!.Next!.Value.Should().BeOfType<long>().Which.Should().Be(42L);
        insert.Data["account_id"].Should().BeOfType<long>().Which.Should().Be(42L);
    }

    [Fact]
    public void RowScope_CoercesContextValueToBigint()
    {
        var model = TenantModel();
        var table = model.GetTableFromDbName("Orders");

        var filter = RowScopeCompiler.Compile(
            "account_id = {user_id}", table, model.TypeMapper,
            new Dictionary<string, object?> { ["user_id"] = "7" });

        filter.Next!.Value.Should().BeOfType<long>().Which.Should().Be(7L);
    }

    [Theory]
    [InlineData("abc")]
    [MemberData(nameof(InvalidContextValues))]
    public void InvalidContextValue_FailsClosed(object value)
    {
        var model = TenantModel();
        var table = model.GetTableFromDbName("Orders");
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = new Dictionary<string, object?> { ["tenant_id"] = value },
            QueryType = QueryType.Standard
        };

        var exception = Assert.Throws<BifrostExecutionError>(() =>
            new TenantFilterTransformer().GetAdditionalFilter(table, context));

        exception.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
    }

    [Fact]
    public void GuidContextValue_CoercesToGuid()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", table => table
                .WithPrimaryKey("id")
                .WithColumn("id", "int")
                .WithColumn("account_id", "uuid")
                .WithMetadata("tenant-filter", "account_id"))
            .Build();
        var expected = Guid.NewGuid();
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = new Dictionary<string, object?> { ["tenant_id"] = expected.ToString() },
            QueryType = QueryType.Standard
        };

        var filter = new TenantFilterTransformer().GetAdditionalFilter(model.GetTableFromDbName("Orders"), context);

        filter!.Next!.Value.Should().BeOfType<Guid>().Which.Should().Be(expected);
    }

    [Fact]
    public void InvalidGuidContextValue_FailsClosed()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", table => table
                .WithPrimaryKey("id")
                .WithColumn("id", "int")
                .WithColumn("account_id", "uuid")
                .WithMetadata("tenant-filter", "account_id"))
            .Build();
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = new Dictionary<string, object?> { ["tenant_id"] = "not-a-guid" },
            QueryType = QueryType.Standard
        };

        var exception = Assert.Throws<BifrostExecutionError>(() =>
            new TenantFilterTransformer().GetAdditionalFilter(model.GetTableFromDbName("Orders"), context));

        exception.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
    }

    // The coercer and the GraphQL schema judge a column's type from the same source: the
    // model's dialect mapper. The schema declares the column with the mapper's GraphQL type;
    // the tenant filter binds the context value as that type's CLR value. The coercer used to
    // keep its own name table, so a Postgres int4 or a SQL Server money column was declared
    // Int / Decimal in the schema while the claim bound as a string.
    [Theory]
    [MemberData(nameof(DialectColumnTypes))]
    public void TenantFilter_BindsTheClrTypeOfTheSchemasGraphQlType(string dialect, string dataType, string claim)
    {
        var mapper = Mappers[dialect];
        var model = DbModelTestFixture.Create()
            .WithTypeMapper(mapper)
            .WithTable("Orders", table => table
                .WithPrimaryKey("id")
                .WithColumn("id", "int")
                .WithColumn("account_id", dataType)
                .WithMetadata("tenant-filter", "account_id"))
            .Build();
        var context = new QueryTransformContext
        {
            Model = model,
            UserContext = new Dictionary<string, object?> { ["tenant_id"] = claim },
            QueryType = QueryType.Standard
        };

        var bound = new TenantFilterTransformer().GetAdditionalFilter(model.GetTableFromDbName("Orders"), context)!.Next!.Value;
        var schemaType = model.TypeMapper.GetGraphQlType(model.GetTableFromDbName("Orders").ColumnLookup["account_id"].EffectiveDataType);

        bound!.GetType().Should().Be(ClrTypeOfGraphQlScalar(schemaType, dataType),
            $"{dialect} '{dataType}' is {schemaType} in the schema");
    }

    private static readonly IReadOnlyDictionary<string, ITypeMapper> Mappers = new Dictionary<string, ITypeMapper>
    {
        ["sqlserver"] = SqlServerTypeMapper.Instance,
        ["postgres"] = PostgresTypeMapper.Instance,
        ["mysql"] = MySqlTypeMapper.Instance,
        ["sqlite"] = SqliteTypeMapper.Instance,
    };

    // The GraphQL scalar's runtime value type. uuid is declared String by every dialect but
    // binds as a Guid, the one discriminator the mappers do not carry.
    private static Type ClrTypeOfGraphQlScalar(string graphQlType, string dataType) =>
        dataType is "uuid" or "uniqueidentifier" ? typeof(Guid) : graphQlType switch
        {
            "Int" => typeof(int),
            "BigInt" => typeof(long),
            "Short" => typeof(short),
            "Byte" => typeof(byte),
            "Decimal" => typeof(decimal),
            "Float" => typeof(double),
            "Boolean" => typeof(bool),
            _ => typeof(string),
        };

    public static IEnumerable<object[]> DialectColumnTypes => new[]
    {
        new object[] { "sqlserver", "int", "7" },
        new object[] { "sqlserver", "bigint", "7" },
        new object[] { "sqlserver", "smallint", "7" },
        new object[] { "sqlserver", "tinyint", "7" },
        new object[] { "sqlserver", "money", "7.25" },
        new object[] { "sqlserver", "smallmoney", "7.25" },
        new object[] { "sqlserver", "decimal", "7.25" },
        new object[] { "sqlserver", "float", "7.25" },
        new object[] { "sqlserver", "bit", "true" },
        new object[] { "sqlserver", "uniqueidentifier", "6f9619ff-8b86-d011-b42d-00c04fc964ff" },
        new object[] { "sqlserver", "nvarchar", "acme" },
        new object[] { "postgres", "int4", "7" },
        new object[] { "postgres", "int8", "7" },
        new object[] { "postgres", "int2", "7" },
        new object[] { "postgres", "float8", "7.25" },
        new object[] { "postgres", "numeric", "7.25" },
        new object[] { "postgres", "bool", "true" },
        new object[] { "postgres", "uuid", "6f9619ff-8b86-d011-b42d-00c04fc964ff" },
        new object[] { "postgres", "text", "acme" },
        new object[] { "mysql", "int", "7" },
        new object[] { "mysql", "bigint", "7" },
        new object[] { "mysql", "mediumint", "7" },
        new object[] { "mysql", "double", "7.25" },
        new object[] { "mysql", "decimal", "7.25" },
        new object[] { "mysql", "varchar", "acme" },
        new object[] { "sqlite", "integer", "7" },
        new object[] { "sqlite", "real", "7.25" },
        new object[] { "sqlite", "text", "acme" },
    };

    private static IDbModel TenantModel() => DbModelTestFixture.Create()
        .WithTable("Orders", table => table
            .WithPrimaryKey("id")
            .WithColumn("id", "int")
            .WithColumn("account_id", "bigint")
            .WithMetadata("tenant-filter", "account_id"))
        .Build();

    public static IEnumerable<object[]> ContextValues => new[]
    {
        new object[] { "42" },
        new object[] { new[] { "42" } }
    };

    public static IEnumerable<object[]> InvalidContextValues => new[]
    {
        new object[] { "abc" },
        new object[] { new[] { "1", "2" } }
    };
}
