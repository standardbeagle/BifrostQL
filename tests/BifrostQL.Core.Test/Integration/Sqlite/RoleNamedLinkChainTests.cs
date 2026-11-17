using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// A link nested beneath a link whose GraphQL field name is not a table name
/// (a role name such as <c>billing_address</c> / <c>orders_by_billing_address</c>,
/// or the self-referential <c>categories_children</c>) must resolve from the
/// parent node's already-resolved table. Re-resolving the parent from its field
/// name treats the field name as a table name, misses, and faults the document.
/// Every query here is at least two link hops deep with a role-named link above
/// the last hop.
/// </summary>
public sealed class RoleNamedLinkChainTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_role_named_link_chain_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("PRAGMA foreign_keys = ON");
        foreach (var table in new[] { "product_categories", "products", "categories", "orders", "addresses", "countries" })
            await Exec($"DROP TABLE IF EXISTS {table}");

        await Exec("CREATE TABLE countries (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await Exec(
            """
            CREATE TABLE addresses (
                id INTEGER PRIMARY KEY,
                street TEXT NOT NULL,
                country_id INTEGER NOT NULL,
                FOREIGN KEY (country_id) REFERENCES countries(id)
            )
            """);
        // Two FKs to the same table: both links get role names.
        await Exec(
            """
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                number TEXT NOT NULL,
                billing_address_id INTEGER NOT NULL,
                shipping_address_id INTEGER NOT NULL,
                FOREIGN KEY (billing_address_id) REFERENCES addresses(id),
                FOREIGN KEY (shipping_address_id) REFERENCES addresses(id)
            )
            """);
        await Exec(
            """
            CREATE TABLE categories (
                id INTEGER PRIMARY KEY,
                parent_id INTEGER NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (parent_id) REFERENCES categories(id)
            )
            """);
        await Exec("CREATE TABLE products (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        // Pure junction: auto-detected as a many-to-many between products and categories.
        await Exec(
            """
            CREATE TABLE product_categories (
                product_id INTEGER NOT NULL,
                category_id INTEGER NOT NULL,
                PRIMARY KEY (product_id, category_id),
                FOREIGN KEY (product_id) REFERENCES products(id),
                FOREIGN KEY (category_id) REFERENCES categories(id)
            )
            """);

        await Exec("INSERT INTO countries(id, name) VALUES (1, 'US'), (2, 'FR')");
        await Exec("INSERT INTO addresses(id, street, country_id) VALUES (10, 'Main St', 1), (20, 'Rue Cler', 2)");
        // Billing and shipping always differ, so a hop taken through the wrong
        // role link returns the other country.
        await Exec(
            """
            INSERT INTO orders(id, number, billing_address_id, shipping_address_id) VALUES
                (100, 'A-1', 10, 20),
                (101, 'A-2', 20, 10)
            """);
        await Exec("INSERT INTO categories(id, parent_id, name) VALUES (1, NULL, 'Root'), (2, 1, 'Child'), (3, 2, 'Grandchild')");
        await Exec("INSERT INTO products(id, name) VALUES (7, 'Widget'), (8, 'Gadget')");
        await Exec("INSERT INTO product_categories(product_id, category_id) VALUES (7, 2), (8, 3)");

        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>()));
        _model = await loader.LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public void Fixture_ExposesRoleNamedLinks()
    {
        // The field names under test are not table names; if the naming scheme
        // changes, these facts stop exercising the bug.
        var orders = _model.GetTableFromDbName("orders");
        orders.SingleLinks.Should().ContainKeys("billing_address", "shipping_address");
        var addresses = _model.GetTableFromDbName("addresses");
        addresses.MultiLinks.Should().ContainKeys("orders_by_billing_address", "orders_by_shipping_address");
        var categories = _model.GetTableFromDbName("categories");
        categories.MultiLinks.Values.Select(l => l.ChildFieldName).Should().Contain("categories_children");
        categories.ManyToManyLinks.Should().ContainKey("products");
        foreach (var name in new[] { "billing_address", "shipping_address", "orders_by_billing_address", "categories_children" })
            _model.TryGetTableByFullGraphQlName(name, out _).Should().BeFalse($"'{name}' must not name a table");
    }

    [Fact]
    public async Task SingleLinkBeneathRoleNamedSingleLink_ResolvesFromParentTable()
    {
        using var doc = await Run(
            """
            {
              orders(sort: [id_asc]) {
                data {
                  number
                  billing_address { street countries { name } }
                  shipping_address { street countries { name } }
                }
              }
            }
            """);

        var rows = Data(doc, "orders");
        rows.Should().HaveCount(2);
        rows[0].GetProperty("number").GetString().Should().Be("A-1");
        rows[0].GetProperty("billing_address").GetProperty("street").GetString().Should().Be("Main St");
        rows[0].GetProperty("billing_address").GetProperty("countries").GetProperty("name").GetString().Should().Be("US");
        rows[0].GetProperty("shipping_address").GetProperty("countries").GetProperty("name").GetString().Should().Be("FR");
        rows[1].GetProperty("billing_address").GetProperty("countries").GetProperty("name").GetString().Should().Be("FR");
        rows[1].GetProperty("shipping_address").GetProperty("street").GetString().Should().Be("Main St");
        rows[1].GetProperty("shipping_address").GetProperty("countries").GetProperty("name").GetString().Should().Be("US");
    }

    [Fact]
    public async Task SelfReferentialChildrenBeneathChildren_ResolvesGrandchildren()
    {
        using var doc = await Run(
            """
            {
              categories(filter: { id: { _eq: 1 } }) {
                data {
                  name
                  categories_children {
                    data {
                      name
                      categories_children { data { name } }
                    }
                  }
                }
              }
            }
            """);

        var rows = Data(doc, "categories");
        rows.Should().ContainSingle();
        var children = rows[0].GetProperty("categories_children").GetProperty("data").EnumerateArray().ToList();
        children.Should().ContainSingle();
        children[0].GetProperty("name").GetString().Should().Be("Child");
        var grandchildren = children[0].GetProperty("categories_children").GetProperty("data").EnumerateArray().ToList();
        grandchildren.Should().ContainSingle();
        grandchildren[0].GetProperty("name").GetString().Should().Be("Grandchild");
    }

    [Fact]
    public async Task ManyToManyBeneathSelfReferentialChildren_ResolvesFromChildTable()
    {
        using var doc = await Run(
            """
            {
              categories(filter: { id: { _eq: 1 } }) {
                data {
                  name
                  categories_children {
                    data {
                      name
                      products { data { name } }
                    }
                  }
                }
              }
            }
            """);

        var rows = Data(doc, "categories");
        rows.Should().ContainSingle();
        var children = rows[0].GetProperty("categories_children").GetProperty("data").EnumerateArray().ToList();
        children.Should().ContainSingle();
        children[0].GetProperty("name").GetString().Should().Be("Child");
        var products = children[0].GetProperty("products").GetProperty("data").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()).ToList();
        products.Should().Equal("Widget");
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<JsonDocument> Run(string query)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);
        var execution = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, BifrostQL.Core.Modules.NullQueryTransformerService.Instance),
            });
        });

        var errors = string.Join(" | ", (execution.Errors ?? new ExecutionErrors()).Select(e => $"{e.Message} :: {e.InnerException?.Message}"));
        execution.Errors.Should().BeNullOrEmpty($"actual errors: {errors}");
        return JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
    }

    private static List<JsonElement> Data(JsonDocument doc, string root) =>
        doc.RootElement.GetProperty("data").GetProperty(root).GetProperty("data").EnumerateArray().ToList();
}
