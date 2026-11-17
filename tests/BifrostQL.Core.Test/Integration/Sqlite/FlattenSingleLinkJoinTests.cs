using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// The intent seam flattens each single-link join's columns into the root row under
/// <c>{parentTable}.{column}</c>. Two links to one parent table (billing and shipping
/// address) share that key, so the second join's value overwrote the first and the
/// billing street read as the shipping street. Such an intent is refused; one link to
/// the table is the paired control.
/// </summary>
public sealed class FlattenSingleLinkJoinTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_flatten_single_link;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        foreach (var sql in new[]
        {
            "DROP TABLE IF EXISTS orders",
            "DROP TABLE IF EXISTS addresses",
            "CREATE TABLE addresses (id INTEGER PRIMARY KEY, street TEXT NOT NULL)",
            """
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                billing_address_id INTEGER NOT NULL REFERENCES addresses(id),
                shipping_address_id INTEGER NOT NULL REFERENCES addresses(id))
            """,
            "INSERT INTO addresses (id, street) VALUES (1, 'bill st'), (2, 'ship st')",
            "INSERT INTO orders (id, billing_address_id, shipping_address_id) VALUES (1, 1, 2)",
        })
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private static QueryIntentExecutor BuildExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>())).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["dbSchema"] = DbSchema.FromModel(model),
                ["connFactory"] = factory,
            });
        });
        return new QueryIntentExecutor(pathCache, NullQueryTransformerService.Instance);
    }

    private static GqlObjectQuery OrdersQuery(IDbModel model, params string[] addressColumns)
    {
        var orders = model.GetTableFromDbName("orders");
        var query = new GqlObjectQuery
        {
            DbTable = orders,
            SchemaName = orders.TableSchema,
            TableName = orders.DbName,
            GraphQlName = orders.GraphQlName,
            Path = orders.GraphQlName,
            ScalarColumns =
            {
                new GqlObjectColumn("id"),
                new GqlObjectColumn("billing_address_id"),
                new GqlObjectColumn("shipping_address_id"),
            },
        };
        foreach (var fkColumn in addressColumns)
        {
            var link = orders.SingleLinks.Values.Single(l => l.ChildId.ColumnName == fkColumn);
            query.Links.Add(new GqlObjectQuery
            {
                DbTable = link.ParentTable,
                SchemaName = link.ParentTable.TableSchema,
                TableName = link.ParentTable.DbName,
                GraphQlName = link.ParentFieldName,
                FieldName = link.ParentFieldName,
                ScalarColumns = { new GqlObjectColumn("street") },
            });
        }
        return query;
    }

    [Fact]
    public async Task OneLinkToATable_FlattensItsColumns()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);

        var result = await executor.ExecuteAsync(new QueryIntent
        {
            Query = OrdersQuery(model, "billing_address_id"),
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        result.Rows.Single()["addresses.street"].Should().Be("bill st");
    }

    [Fact]
    public async Task TwoLinksToOneTable_AreRefusedRatherThanOverwritten()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);

        var act = () => executor.ExecuteAsync(new QueryIntent
        {
            Query = OrdersQuery(model, "billing_address_id", "shipping_address_id"),
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>()
            .WithMessage("*more than one link to 'addresses'*");
    }
}
