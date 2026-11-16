using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
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
/// End to end on SQLite: a per-row <c>_agg</c> statement returns one group per parent
/// on the caller's page, not one per parent in the table. The fixture has more orders
/// than any page, and every order has a distinct line count, so a group read for an
/// off-page order shows up in the statement's row count and a mis-correlated value
/// shows up in the response. The SQL-text half is AggregateParentPageWindowTests.
/// </summary>
public sealed class AggregateParentPageTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_agg_parent_page_test;Mode=Memory;Cache=Shared";
    private const int OrderCount = 12;
    private const int Ceiling = 4;

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;
    private IDbModel _ceilingModel = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS lines");
        await Exec("DROP TABLE IF EXISTS orders");
        await Exec("CREATE TABLE orders (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await Exec(
            """
            CREATE TABLE lines (
                id INTEGER PRIMARY KEY,
                order_id INTEGER NOT NULL,
                FOREIGN KEY (order_id) REFERENCES orders(id)
            )
            """);
        await Exec($"INSERT INTO orders(id, name) VALUES {string.Join(", ", Enumerable.Range(1, OrderCount).Select(i => $"({i}, 'o{i}')"))}");
        // Order i has i lines: every order's count differs from every other's.
        var lineId = 0;
        var lines = Enumerable.Range(1, OrderCount)
            .SelectMany(order => Enumerable.Range(0, order).Select(_ => $"({++lineId}, {order})"));
        await Exec($"INSERT INTO lines(id, order_id) VALUES {string.Join(", ", lines)}");

        _model = await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
        _ceilingModel = await new DbModelLoader(
            new SqliteDbConnFactory(ConnString),
            new MetadataLoader(new[] { $":root {{ {MetadataKeys.Model.MaxQueryRows}: {Ceiling} }}" })).LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    /// <summary>Runs the aggregate statement the query builds and returns the srcId of each group it returns.</summary>
    private async Task<List<long>> AggregateGroupKeysAsync(IDbModel model, int? limit, int? offset)
    {
        var orders = model.GetTableFromDbName("orders");
        var query = new GqlObjectQuery
        {
            DbTable = orders,
            TableName = orders.DbName,
            SchemaName = orders.TableSchema,
            GraphQlName = orders.GraphQlName,
            ScalarColumns = { new GqlObjectColumn("id") },
            AggregateColumns =
            {
                new GqlAggregateColumn(
                    new List<(LinkDirection, TableLinkDto)> { (LinkDirection.OneToMany, orders.MultiLinks["lines"]) },
                    "id", "n", AggregateOperationType.Count),
            },
            Sort = { "id_asc" },
            Limit = limit,
            Offset = offset,
        };
        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, SqliteDialect.Instance, sqls, parameters);
        var aggregate = sqls[query.AggregateColumns.Single().SqlKey!];

        await using var cmd = new SqliteCommand(aggregate.Sql, _keepAlive);
        foreach (var p in aggregate.Parameters)
            cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
        var keys = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            keys.Add(reader.GetInt64(0));
        keys.Sort();
        return keys;
    }

    private async Task<List<(int Id, int Lines)>> PageAsync(IDbModel model, string args)
    {
        var schema = DbSchema.FromModel(model);
        var execution = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = $"{{ orders({args}) {{ data {{ id n: _agg(operation: Count, value: {{ lines: {{ column: id }} }}) }} }} }}";
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema, NullQueryTransformerService.Instance),
            });
        });

        execution.Errors.Should().BeNullOrEmpty();
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(execution));
        return doc.RootElement.GetProperty("data").GetProperty("orders").GetProperty("data")
            .EnumerateArray()
            .Select(r => (r.GetProperty("id").GetInt32(), (int)double.Parse(r.GetProperty("n").ToString())))
            .ToList();
    }

    [Fact]
    public async Task ExplicitPage_AggregateStatement_GroupsOnlyThePagedParents()
    {
        var keys = await AggregateGroupKeysAsync(_model, limit: 3, offset: 2);

        keys.Should().Equal(new long[] { 3, 4, 5 },
            "the aggregate must group the parents on the page (ids 3..5), not every one of the 12 orders");
    }

    [Fact]
    public async Task NoLimit_UnderMaxQueryRows_AggregateStatement_GroupsOnlyTheCeilingParents()
    {
        var keys = await AggregateGroupKeysAsync(_ceilingModel, limit: null, offset: null);

        keys.Should().Equal(new long[] { 1, 2, 3, 4 },
            "max-query-rows binds the aggregate's parent rows exactly as it binds the page");
    }

    [Fact]
    public async Task PagedValues_EqualTheUnpagedValues()
    {
        var all = await PageAsync(_model, "sort: [id_asc]");
        all.Should().HaveCount(OrderCount);
        all.Should().OnlyContain(r => r.Lines == r.Id, "order i has i lines");

        var page = await PageAsync(_model, "sort: [id_asc], limit: 3, offset: 2");
        page.Should().Equal(all.Skip(2).Take(3), "paging the parent must not change any parent's aggregate");

        var desc = await PageAsync(_model, "sort: [id_desc], limit: 2");
        desc.Should().Equal((12, 12), (11, 11));

        var capped = await PageAsync(_ceilingModel, "sort: [id_asc]");
        capped.Should().Equal(all.Take(Ceiling));
    }
}
