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
/// Verifies that a forward single-link (many-to-one) join declared on a
/// query-intent tree is flattened into the returned rows: each root row carries
/// the joined parent's selected columns under a table-qualified key. This is the
/// Core seam the pgwire SQL-subset JOIN relies on to surface joined columns
/// through <see cref="IQueryIntentExecutor"/>, which otherwise returns only the
/// root result set. WHERE filtering on the intent is exercised here too, against
/// a real SQLite database.
/// </summary>
public sealed class QueryIntentJoinFlattenTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_intent_join_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS orders");
        await Exec("DROP TABLE IF EXISTS customers");
        await Exec(
            """
            CREATE TABLE customers (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                customer_id INTEGER NOT NULL,
                amount INTEGER NOT NULL,
                FOREIGN KEY (customer_id) REFERENCES customers(id)
            )
            """);
        await Exec("INSERT INTO customers(id, name) VALUES (1, 'alice'), (2, 'bob')");
        await Exec(
            """
            INSERT INTO orders(id, customer_id, amount) VALUES
                (10, 1, 100),
                (11, 1, 250),
                (12, 2, 400)
            """);
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private static QueryIntentExecutor BuildExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var loader = new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>()));
            var model = await loader.LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["dbSchema"] = DbSchema.FromModel(model),
                ["connFactory"] = factory,
            });
        });

        // No filter transformers registered: this test isolates the join flatten.
        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        });
        return new QueryIntentExecutor(pathCache, transformerService);
    }

    private static GqlObjectQuery BuildOrdersJoinCustomers(IDbModel model, out string linkField)
    {
        var orders = model.GetTableFromDbName("orders");

        // Resolve the forward single-link (orders → customers) exactly as the
        // pgwire translator does: by the relationship whose parent is customers.
        var link = orders.SingleLinks.First(kv =>
            string.Equals(kv.Value.ParentTable.DbName, "customers", StringComparison.OrdinalIgnoreCase));
        linkField = link.Key;

        var customers = link.Value.ParentTable;
        var linkNode = new GqlObjectQuery
        {
            DbTable = customers,
            SchemaName = customers.TableSchema,
            TableName = customers.DbName,
            GraphQlName = link.Key,
            FieldName = link.Key,
            ScalarColumns = { new GqlObjectColumn("name") },
        };

        return new GqlObjectQuery
        {
            DbTable = orders,
            SchemaName = orders.TableSchema,
            TableName = orders.DbName,
            GraphQlName = orders.GraphQlName,
            Path = orders.GraphQlName,
            ScalarColumns = { new GqlObjectColumn("id"), new GqlObjectColumn("amount") },
            Links = { linkNode },
        };
    }

    [Fact]
    public async Task SingleLinkJoin_FlattensParentColumnsOntoEachRootRow()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);

        var result = await executor.ExecuteAsync(new QueryIntent
        {
            Query = BuildOrdersJoinCustomers(model, out _),
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        result.Rows.Should().HaveCount(3);
        // Every root order row carries the joined customer name under the
        // table-qualified key the pgwire translator projects.
        foreach (var row in result.Rows)
            row.Should().ContainKey("customers.name");

        var byId = result.Rows.ToDictionary(r => Convert.ToInt64(r["id"]));
        ((string?)byId[10]["customers.name"]).Should().Be("alice");
        ((string?)byId[11]["customers.name"]).Should().Be("alice");
        ((string?)byId[12]["customers.name"]).Should().Be("bob");
    }

    [Fact]
    public async Task WhereFilterOnIntent_ActuallyRestrictsRows()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);

        var orders = model.GetTableFromDbName("orders");
        var query = new GqlObjectQuery
        {
            DbTable = orders,
            SchemaName = orders.TableSchema,
            TableName = orders.DbName,
            GraphQlName = orders.GraphQlName,
            Path = orders.GraphQlName,
            ScalarColumns = { new GqlObjectColumn("id"), new GqlObjectColumn("amount") },
            Filter = TableFilter.FromObject(
                new Dictionary<string, object?>
                {
                    ["amount"] = new Dictionary<string, object?> { ["_gt"] = 150 },
                },
                orders.DbName),
        };

        var result = await executor.ExecuteAsync(new QueryIntent
        {
            Query = query,
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        result.Rows.Select(r => Convert.ToInt64(r["id"])).Should().BeEquivalentTo(new[] { 11L, 12L });
        result.Sql.Should().NotContain("150", "WHERE literals must bind as parameters, not inline");
    }
}
