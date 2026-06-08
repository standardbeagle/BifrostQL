using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

public sealed class ComputedColumnModuleTests
{
    [Fact]
    public void TableSchema_IncludesSqlAndProviderComputedFields()
    {
        var table = BuildModel().GetTableFromDbName("Orders");
        var sdl = new TableSchemaGenerator(table).GetTableTypeDefinition(BuildModel(), includeDynamicJoins: false);

        sdl.Should().Contain("totalWithTax : Float");
        sdl.Should().Contain("shippingEstimate : String");
    }

    [Fact]
    public void SqlComputedColumn_RendersPlaceholderExpression()
    {
        var model = BuildModel();
        var table = model.GetTableFromDbName("Orders");
        var computed = ComputedColumnConfigCollector.Find(table, "totalWithTax")!;
        var column = new GqlObjectColumn(computed, "totalWithTax");

        var sql = column.ToSelectSql(model, table, SqlServerDialect.Instance);

        sql.Should().Be("([subtotal] + [tax]) [totalWithTax]");
    }

    [Fact]
    public void ProviderComputedColumn_ProjectsDeclaredDependenciesOnly()
    {
        var model = BuildModel();
        var table = model.GetTableFromDbName("Orders");
        var computed = ComputedColumnConfigCollector.Find(table, "shippingEstimate")!;
        var query = new GqlObjectQuery
        {
            DbTable = table,
            TableName = table.DbName,
            SchemaName = table.TableSchema,
            GraphQlName = table.GraphQlName,
            ScalarColumns = new List<GqlObjectColumn> { new(computed, computed.Name) },
        };

        query.FullColumnNames.Select(c => c.DbDbName).Should().BeEquivalentTo("Id", "destination_zip");
    }

    private static IDbModel BuildModel()
        => DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("subtotal", "decimal")
                .WithColumn("tax", "decimal")
                .WithColumn("destination_zip", "varchar")
                .WithMetadata(MetadataKeys.Computed.Sql, "totalWithTax:Float:({subtotal} + {tax})")
                .WithMetadata(MetadataKeys.Computed.Provider, "shippingEstimate:String:shipping-api:depends=Id,destination_zip"))
            .Build();
}
