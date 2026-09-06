using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.QueryModel;

public sealed class SchemaIdentityTests
{
    private static DbTable Table(string schema, string name, params string[] fields)
    {
        var columns = fields.Select((field, i) => new ColumnDto
        {
            ColumnName = field, GraphQlName = field, DataType = "int",
            IsPrimaryKey = i == 0, OrdinalPosition = i + 1,
        }).ToArray();
        return new DbTable
        {
            TableSchema = schema, DbName = name, GraphQlName = name,
            NormalizedName = name, TableType = "BASE TABLE",
            ColumnLookup = columns.ToDictionary(c => c.DbName),
            GraphQlLookup = columns.ToDictionary(c => c.GraphQlName),
        };
    }

    private static (DbModel Model, DbTable Sales) Fixture()
    {
        var sales = Table("sales", "orders", "id", "amount", "tenant_id");
        var archive = Table("archive", "orders", "id", "archived_total");
        return (new DbModel { Tables = new[] { archive, sales } }, sales);
    }

    private static GqlObjectQuery Query(IDbTable table) => new()
    {
        DbTable = table, TableName = table.DbName, SchemaName = table.TableSchema,
        GraphQlName = table.GraphQlName, Path = table.GraphQlName,
        ScalarColumns = { new GqlObjectColumn("id"), new GqlObjectColumn("amount") },
    };

    [Fact]
    public void RootFilter_PreservesResolvedSchema()
    {
        var (model, sales) = Fixture();
        var query = Query(sales);
        query.Filter = TableFilterFactory.Equals(sales, "amount", 7);
        var sqls = new Dictionary<string, ParameterizedSql>();
        query.AddSqlParameterized(model, SqlServerDialect.Instance, sqls, new());
        sqls["orders"].Sql.Should().Contain("[sales].[orders]").And.NotContain("[archive].[orders]");
        sqls["orders"].Parameters.Should().ContainSingle(p => Equals(p.Value, 7));
    }

    [Fact]
    public void ConnectedJoin_PreservesResolvedSchema()
    {
        var (model, sales) = Fixture();
        var query = Query(sales);
        var join = new TableJoin
        {
            ConnectedTable = query, FromTable = query, Name = "orders",
            FromColumn = "id", ConnectedColumn = "id", QueryType = QueryType.Single,
        };
        var sql = GqlObjectQuery.ToConnectedSqlParameterized(model, SqlServerDialect.Instance,
            new(), new ParameterizedSql("SELECT 1 AS [JoinId]", Array.Empty<SqlParameterInfo>()), join);
        sql.Sql.Should().Contain("[sales].[orders]").And.NotContain("[archive].[orders]");
    }

    [Fact]
    public void GroupedAggregateFilter_PreservesResolvedSchema()
    {
        var (model, sales) = Fixture();
        var query = Query(sales);
        query.Filter = TableFilterFactory.Equals(sales, "tenant_id", 9);
        query.GroupedAggregate = new GroupedAggregate
        {
            GroupColumns = new[] { new AggregateGroupColumn(sales.ColumnLookup["amount"], "amount") },
            IncludeCount = true,
            ValueColumns = Array.Empty<AggregateValueColumn>(),
        };
        var sqls = new Dictionary<string, ParameterizedSql>();
        query.AddSqlParameterized(model, SqlServerDialect.Instance, sqls, new());
        sqls["orders"].Sql.Should().Contain("[sales].[orders]").And.Contain("GROUP BY").And.NotContain("[archive].[orders]");
        sqls["orders"].Parameters.Should().ContainSingle(p => Equals(p.Value, 9));
    }

    [Fact]
    public void PivotFilter_PreservesResolvedSchema()
    {
        var (model, sales) = Fixture();
        var query = Query(sales);
        query.Filter = TableFilterFactory.Equals(sales, "tenant_id", 9);
        var filter = query.GetFilterSqlParameterized(model, SqlServerDialect.Instance, new());
        var sql = PivotSqlGenerator.GeneratePivot(SqlServerDialect.Instance,
            PivotQueryConfig.Create("amount", "id", "COUNT", new[] { "tenant_id" }),
            SqlServerDialect.Instance.TableReference(sales.TableSchema, sales.DbName), new object?[] { 7 }, filter);
        sql.Sql.Should().Contain("[sales].[orders]").And.NotContain("[archive].[orders]");
        sql.Parameters.Should().Contain(p => Equals(p.Value, 9));
    }

    [Fact]
    public void TwoHopFilter_LastHopScopePreservesResolvedSchema()
    {
        var (ordersModel, sales) = Fixture();
        var lines = Table("sales", "lines", "id", "order_id");
        var notes = Table("sales", "notes", "id", "line_id");
        lines.SingleLinks.Add("orders", new TableLinkDto
        {
            Name = "orders", ChildTable = lines, ParentTable = sales,
            ChildId = lines.ColumnLookup["order_id"], ParentId = sales.ColumnLookup["id"],
        });
        notes.SingleLinks.Add("lines", new TableLinkDto
        {
            Name = "lines", ChildTable = notes, ParentTable = lines,
            ChildId = notes.ColumnLookup["line_id"], ParentId = lines.ColumnLookup["id"],
        });
        var model = new DbModel { Tables = ordersModel.Tables.Concat(new[] { lines, notes }).ToArray() };
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            ["lines"] = new Dictionary<string, object?>
            {
                ["orders"] = new Dictionary<string, object?>
                {
                    ["amount"] = new Dictionary<string, object?> { ["_eq"] = 7 },
                },
            },
        }, notes);
        filter.Next!.TraversedTableFilter = TableFilterFactory.Equals(sales, "tenant_id", 9);
        var parts = filter.RenderParts(model, SqlServerDialect.Instance, new(), null);
        parts.Joins.Should().Contain("[sales].[orders]").And.Contain("[orders].[tenant_id]")
            .And.NotContain("[archive].[orders]");
        parts.Where.Should().BeEmpty();
        parts.Parameters.Should().Contain(p => Equals(p.Value, 9));
    }
}
