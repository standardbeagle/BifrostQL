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
/// A <c>computed-expr</c> column on a table read through a relationship join must qualify
/// its column references with that table's alias, as plain and raw-SQL computed columns
/// already do. A many-to-many read joins the junction table too; this junction has its own
/// surrogate <c>id</c>, so the <c>labels</c> computed column's bare <c>"id"</c> is ambiguous
/// unless it is written <c>"b"."id"</c>.
/// </summary>
public sealed class ComputedExpressionJoinTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_computed_expr_join_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS task_labels");
        await Exec("DROP TABLE IF EXISTS labels");
        await Exec("DROP TABLE IF EXISTS tasks");
        await Exec("DROP TABLE IF EXISTS projects");
        await Exec("CREATE TABLE projects (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await Exec("""
            CREATE TABLE tasks (
                id INTEGER PRIMARY KEY,
                project_id INTEGER NOT NULL REFERENCES projects(id),
                name TEXT NOT NULL)
            """);
        await Exec("INSERT INTO projects (id, name) VALUES (1, 'apollo')");
        await Exec("INSERT INTO tasks (id, project_id, name) VALUES (10, 1, 'launch'), (11, 1, 'land')");
        await Exec("CREATE TABLE labels (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        // A junction with its own surrogate key: a payload column, so it is declared below
        // rather than auto-detected. Its ids differ from the label ids on purpose.
        await Exec("""
            CREATE TABLE task_labels (
                id INTEGER PRIMARY KEY,
                task_id INTEGER NOT NULL REFERENCES tasks(id),
                label_id INTEGER NOT NULL REFERENCES labels(id))
            """);
        await Exec("INSERT INTO labels (id, name) VALUES (1, 'urgent'), (2, 'blocked')");
        await Exec("INSERT INTO task_labels (id, task_id, label_id) VALUES (500, 10, 1), (501, 10, 2)");

        var loader = new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(new[]
        {
            "*.tasks { computed-expr: shout:String:UPPER(name); many-to-many: labels:task_labels }",
            "*.labels { computed-expr: labelKey:Int:id }",
        }));
        _model = await loader.LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<JsonElement> QueryAsync(string query)
    {
        var schema = DbSchema.FromModel(_model);
        var result = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
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

        result.Errors.Should().BeNullOrEmpty("the joined computed column must render valid, table-qualified SQL");
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task Expression_column_on_a_joined_table_qualifies_its_columns_with_the_table_alias()
    {
        var data = await QueryAsync(
            "{ tasks(filter: { id: { _eq: 10 } }) { data { id labels(sort: [id_asc]) { data { id labelKey } } } } }");

        var labels = data.GetProperty("tasks").GetProperty("data")[0].GetProperty("labels").GetProperty("data");
        labels.EnumerateArray().Select(l => l.GetProperty("labelKey").GetInt32())
            .Should().Equal(new[] { 1, 2 }, "each label's own id, not the junction row's");
    }

    [Fact]
    public async Task Expression_column_on_a_foreign_key_collection_reads_the_child_row()
    {
        var data = await QueryAsync(
            "{ projects { data { id name tasks(sort: [id_asc]) { data { id shout } } } } }");

        var project = data.GetProperty("projects").GetProperty("data")[0];
        project.GetProperty("name").GetString().Should().Be("apollo");
        var tasks = project.GetProperty("tasks").GetProperty("data");
        tasks.EnumerateArray().Select(t => t.GetProperty("shout").GetString())
            .Should().Equal(new[] { "LAUNCH", "LAND" }, "each task's own name, not the project's");
    }

    [Fact]
    public async Task Expression_column_on_the_root_table_renders_as_before()
    {
        var data = await QueryAsync("{ tasks(sort: [id_asc]) { data { id shout } } }");

        data.GetProperty("tasks").GetProperty("data").EnumerateArray()
            .Select(t => t.GetProperty("shout").GetString())
            .Should().Equal("LAUNCH", "LAND");
    }
}
