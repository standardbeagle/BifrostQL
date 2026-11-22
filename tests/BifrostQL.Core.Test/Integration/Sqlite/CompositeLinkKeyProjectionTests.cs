using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// A link over a composite key reads every key column off the parent row to find its
/// children, so the parent SELECT must project every one of them, selected or not. Each
/// fact selects none of the key columns. Two tenants share customer id 1, so a key read
/// from one column alone could not tell their rows apart.
/// </summary>
public sealed class CompositeLinkKeyProjectionTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_composite_link_key_projection;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        foreach (var sql in new[]
        {
            "DROP TABLE IF EXISTS orders",
            "DROP TABLE IF EXISTS customers",
            "CREATE TABLE customers (tenant_id INTEGER NOT NULL, id INTEGER NOT NULL, name TEXT NOT NULL, PRIMARY KEY (tenant_id, id))",
            """
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, customer_id INTEGER NOT NULL, label TEXT NOT NULL,
                FOREIGN KEY (tenant_id, customer_id) REFERENCES customers(tenant_id, id))
            """,
            "INSERT INTO customers (tenant_id, id, name) VALUES (1, 1, 't1-cust'), (2, 1, 't2-cust'), (2, 2, 't2-other')",
            "INSERT INTO orders (id, tenant_id, customer_id, label) VALUES (10, 1, 1, 'o10'), (11, 2, 1, 'o11'), (12, 2, 1, 'o12')",
        })
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            await cmd.ExecuteNonQueryAsync();
        }
        _model = await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task ToOneLink_OverACompositeKey_FindsItsParent_WithNoKeyColumnSelected()
    {
        var customer = _model.Tables.Single(t => t.DbName == "orders").SingleLinks.Values
            .Single(l => l.ParentTable.DbName == "customers").ParentFieldName;

        var data = await DataAsync($$"""{ orders(sort: [id_asc]) { data { label {{customer}} { name } } } }""");

        data.Should().Be(
            $$$"""{"orders":{"data":[{"label":"o10","{{{customer}}}":{"name":"t1-cust"}},""" +
            $$$"""{"label":"o11","{{{customer}}}":{"name":"t2-cust"}},{"label":"o12","{{{customer}}}":{"name":"t2-cust"}}]}}""");
    }

    [Fact]
    public async Task ToManyLink_OverACompositeKey_CollectsItsChildren_WithNoKeyColumnSelected()
    {
        var orders = _model.Tables.Single(t => t.DbName == "customers").MultiLinks.Values
            .Single(l => l.ChildTable.DbName == "orders").ChildFieldName;

        var data = await DataAsync($$"""{ customers(sort: [name_asc]) { data { name {{orders}}(sort: [label_asc]) { data { label } } } } }""");

        data.Should().Be(
            $$$"""{"customers":{"data":[{"name":"t1-cust","{{{orders}}}":{"data":[{"label":"o10"}]}},""" +
            $$$"""{"name":"t2-cust","{{{orders}}}":{"data":[{"label":"o11"},{"label":"o12"}]}},""" +
            $$$"""{"name":"t2-other","{{{orders}}}":{"data":[]}}]}}""");
    }

    private async Task<string> DataAsync(string query)
    {
        var schema = DbSchema.FromModel(_model);
        var result = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, NullQueryTransformerService.Instance),
            });
        });
        var json = new GraphQLSerializer().Serialize(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JsonDocument.Parse(json).RootElement.GetProperty("data").GetRawText();
    }
}
