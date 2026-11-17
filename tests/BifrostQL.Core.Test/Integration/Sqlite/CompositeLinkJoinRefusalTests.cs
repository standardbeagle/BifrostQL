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
/// Relationship filters, <c>_agg</c> paths and many-to-many collections join on one
/// column pair. Over a <c>(tenant_id, id)</c> key that pair is <c>id</c> alone, which
/// matches the other tenant's row with the same id. Each path refuses a composite link
/// instead. Every fixture has two tenants sharing an id, so the old single-column join
/// returns the foreign tenant's row (the RED), and a paired single-column control shows
/// the path itself still works.
/// </summary>
public sealed class CompositeLinkJoinRefusalTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_composite_link_refusal;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        foreach (var sql in new[]
        {
            "DROP TABLE IF EXISTS enrollments",
            "DROP TABLE IF EXISTS courses",
            "DROP TABLE IF EXISTS students",
            "DROP TABLE IF EXISTS orders",
            "DROP TABLE IF EXISTS regions",
            "DROP TABLE IF EXISTS customers",
            "CREATE TABLE regions (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
            "CREATE TABLE customers (tenant_id INTEGER NOT NULL, id INTEGER NOT NULL, name TEXT NOT NULL, PRIMARY KEY (tenant_id, id))",
            """
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, customer_id INTEGER NOT NULL,
                region_id INTEGER NOT NULL REFERENCES regions(id),
                FOREIGN KEY (tenant_id, customer_id) REFERENCES customers(tenant_id, id))
            """,
            "CREATE TABLE students (tenant_id INTEGER NOT NULL, id INTEGER NOT NULL, name TEXT NOT NULL, PRIMARY KEY (tenant_id, id))",
            "CREATE TABLE courses (tenant_id INTEGER NOT NULL, id INTEGER NOT NULL, title TEXT NOT NULL, PRIMARY KEY (tenant_id, id))",
            """
            CREATE TABLE enrollments (
                id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, student_id INTEGER NOT NULL, course_id INTEGER NOT NULL,
                FOREIGN KEY (tenant_id, student_id) REFERENCES students(tenant_id, id),
                FOREIGN KEY (tenant_id, course_id) REFERENCES courses(tenant_id, id))
            """,
            "INSERT INTO regions (id, name) VALUES (1, 'north')",
            "INSERT INTO customers (tenant_id, id, name) VALUES (1, 1, 't1-cust'), (2, 1, 't2-cust')",
            "INSERT INTO orders (id, tenant_id, customer_id, region_id) VALUES (10, 1, 1, 1)",
            "INSERT INTO students (tenant_id, id, name) VALUES (1, 1, 't1-student'), (2, 1, 't2-student')",
            "INSERT INTO courses (tenant_id, id, title) VALUES (1, 7, 't1-course'), (2, 7, 't2-course')",
            "INSERT INTO enrollments (id, tenant_id, student_id, course_id) VALUES (1, 1, 1, 7)",
        })
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            await cmd.ExecuteNonQueryAsync();
        }
        _model = await new DbModelLoader(
            new SqliteDbConnFactory(ConnString),
            new MetadataLoader(new[] { "main.students { many-to-many: courses:enrollments }" })).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<ExecutionResult> RunAsync(string query)
    {
        var schema = DbSchema.FromModel(_model);
        return await new DocumentExecuter().ExecuteAsync(options =>
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
    }

    private static string Refusal(ExecutionResult result)
    {
        result.Errors.Should().NotBeNullOrEmpty(new GraphQLSerializer().Serialize(result));
        return result.Errors![0].Message;
    }

    private static JsonElement Data(ExecutionResult result)
    {
        result.Errors.Should().BeNullOrEmpty();
        return JsonDocument.Parse(new GraphQLSerializer().Serialize(result)).RootElement.GetProperty("data").Clone();
    }

    private string LinkField(string table, string target) =>
        _model.Tables.Single(t => t.DbName == table).SingleLinks.Values.Single(l => l.ParentTable.DbName == target).ParentFieldName;

    [Fact]
    public async Task RelationshipFilter_OverCompositeKey_IsRefused()
    {
        var customer = LinkField("orders", "customers");

        var result = await RunAsync($$"""{ orders(filter: { {{customer}}: { name: { _eq: "t2-cust" } } }) { data { id } } }""");

        Refusal(result).Should().Contain("composite key");
    }

    [Fact]
    public async Task RelationshipFilter_OverSingleColumnKey_StillFilters()
    {
        var region = LinkField("orders", "regions");

        var result = await RunAsync($$"""{ orders(filter: { {{region}}: { name: { _eq: "north" } } }) { data { id } } }""");

        Data(result).GetProperty("orders").GetProperty("data").EnumerateArray()
            .Select(o => o.GetProperty("id").GetInt32()).Should().Equal(10);
    }

    [Fact]
    public async Task AggregatePath_OverCompositeKey_IsRefused()
    {
        var result = await RunAsync(
            "{ customers(sort: [tenant_id_asc]) { data { tenant_id _agg(operation: Count, value: { orders: { column: id } }) } } }");

        Refusal(result).Should().Contain("composite key");
    }

    [Fact]
    public async Task AggregatePath_OverSingleColumnKey_StillAggregates()
    {
        var result = await RunAsync(
            "{ regions { data { id _agg(operation: Count, value: { orders: { column: id } }) } } }");

        Data(result).GetProperty("regions").GetProperty("data")[0].GetProperty("_agg").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ManyToManyCollection_OverCompositeKey_IsRefused()
    {
        var result = await RunAsync(
            "{ students(filter: { tenant_id: { _eq: 1 } }) { data { name courses { data { title } } } } }");

        Refusal(result).Should().Contain("composite key");
    }
}
