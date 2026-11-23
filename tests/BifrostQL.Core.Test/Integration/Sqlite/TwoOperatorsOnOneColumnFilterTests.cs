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
using System.Text.Json;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// A column's filter input takes several operators at once, <c>day: { _gte: a, _lte: b }</c>, and
/// each one constrains the rows. Observed 2026-11-22 (harvest m1b, the Day view's week read): the
/// parser wrapped the two operators in an AND node, the renderer took a column whose next node is
/// not an operator for a relationship, and the read failed with "unknown single-link relationship
/// 'day'". It holds wherever a column filter can appear: at the top, beside another column, inside
/// <c>or</c>, and inside a relationship sub-filter.
/// </summary>
public sealed class TwoOperatorsOnOneColumnFilterTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_two_operators_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS entries");
        await Exec("DROP TABLE IF EXISTS projects");
        await Exec("CREATE TABLE projects (id INTEGER PRIMARY KEY, name TEXT NOT NULL, rank INTEGER NOT NULL)");
        await Exec("""
            CREATE TABLE entries (
                id INTEGER PRIMARY KEY,
                project_id INTEGER NOT NULL REFERENCES projects(id),
                person TEXT NOT NULL,
                day TEXT NOT NULL)
            """);
        await Exec("INSERT INTO projects (id, name, rank) VALUES (1, 'one', 1), (2, 'two', 2), (3, 'three', 3), (4, 'four', 4)");
        await Exec("""
            INSERT INTO entries (id, project_id, person, day) VALUES
              (1, 1, 'ann', '2026-11-15'),
              (2, 2, 'ann', '2026-11-16'),
              (3, 3, 'bob', '2026-11-18'),
              (4, 4, 'ann', '2026-11-22'),
              (5, 1, 'ann', '2026-11-23')
            """);
        _model = await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int[]> EntryIds(string filter)
    {
        var schema = DbSchema.FromModel(_model);
        var result = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = $"{{ entries(filter: {filter}, sort: [id_asc]) {{ data {{ id }} }} }}";
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(
                    _model, schema, new QueryTransformerService(new FilterTransformersWrap
                    {
                        Transformers = Array.Empty<IFilterTransformer>(),
                    })),
            });
        });

        result.Errors.Should().BeNullOrEmpty();
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        return doc.RootElement.GetProperty("data").GetProperty("entries").GetProperty("data")
            .EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ToArray();
    }

    [Fact]
    public async Task Both_operators_on_one_column_bound_the_rows()
    {
        (await EntryIds("""{ day: { _gte: "2026-11-16", _lte: "2026-11-22" } }"""))
            .Should().Equal(2, 3, 4);
    }

    [Fact]
    public async Task Two_operators_beside_another_column_hold_all_three_constraints()
    {
        (await EntryIds("""{ person: { _eq: "ann" }, day: { _gte: "2026-11-16", _lte: "2026-11-22" } }"""))
            .Should().Equal(2, 4);
    }

    [Fact]
    public async Task Two_operators_inside_or_bound_their_branch()
    {
        (await EntryIds("""{ or: [{ day: { _gt: "2026-11-15", _lt: "2026-11-18" } }, { id: { _eq: 5 } }] }"""))
            .Should().Equal(2, 5);
    }

    [Fact]
    public async Task Two_operators_inside_a_relationship_filter_bound_the_related_rows()
    {
        (await EntryIds("{ projects: { rank: { _gte: 2, _lte: 3 } } }"))
            .Should().Equal(2, 3);
    }
}
