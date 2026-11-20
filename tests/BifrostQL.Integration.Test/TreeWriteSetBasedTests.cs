using System.Collections.Concurrent;
using System.Globalization;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// A tree write (<c>save</c>, <c>sync</c>) writes the same-table nodes at one depth with one
/// <c>INSERT … RETURNING</c> and maps the keys back, in row order, to their own children; the
/// transformer chain still runs once per node. <c>batch-max-size</c> bounds a tree's writes as it
/// bounds a batch's actions. Statement-level triggers record each INSERT statement under the
/// caller (owner) that wrote it; the chain stamps the owner and counts its runs.
/// </summary>
public sealed class TreeWriteSetBasedTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE projects (id SERIAL PRIMARY KEY, owner_id INT NOT NULL, name TEXT NOT NULL);
        CREATE TABLE tasks (id SERIAL PRIMARY KEY, project_id INT NOT NULL REFERENCES projects(id), owner_id INT NOT NULL, name TEXT NOT NULL);
        CREATE TABLE steps (id SERIAL PRIMARY KEY, task_id INT NOT NULL REFERENCES tasks(id), owner_id INT NOT NULL, name TEXT NOT NULL);
        CREATE TABLE insert_statements (owner_id INT NOT NULL, table_name TEXT NOT NULL);
        CREATE FUNCTION count_insert() RETURNS trigger LANGUAGE plpgsql AS
            $$ BEGIN INSERT INTO insert_statements SELECT DISTINCT owner_id, TG_TABLE_NAME FROM written; RETURN NULL; END $$;
        CREATE TRIGGER projects_insert_statements AFTER INSERT ON projects
            REFERENCING NEW TABLE AS written FOR EACH STATEMENT EXECUTE FUNCTION count_insert();
        CREATE TRIGGER tasks_insert_statements AFTER INSERT ON tasks
            REFERENCING NEW TABLE AS written FOR EACH STATEMENT EXECUTE FUNCTION count_insert();
        CREATE TRIGGER steps_insert_statements AFTER INSERT ON steps
            REFERENCING NEW TABLE AS written FOR EACH STATEMENT EXECUTE FUNCTION count_insert();
        """;

    private readonly ConcurrentQueue<string> _chainRuns = new();
    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_tree_set_based", Ddl,
            new[]
            {
                "public.projects.owner_id { populate: host }",
                "public.tasks.owner_id { populate: host }",
                "public.steps.owner_id { populate: host }",
            },
            bifrost: options => options.AddMutationTransformers(new IMutationTransformer[] { new StampOwner(_chainRuns) }));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Save_WritesEachLevelInOneInsert_AndLinksChildrenToTheirOwnParents()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync($$"""mutation { projects(save: { name: "p", tasks: [{{Tasks(20, 3)}}] }) }""", "1");

        PostgresGraphQlHost.AssertNoErrors(reply);
        await AssertTreeAsync(owner: 1, tasks: 20, stepsPerTask: 3);
        _chainRuns.Count(u => u == "1").Should().Be(81, "the chain runs once per node");
    }

    [SkippableFact]
    public async Task Sync_OfANewTree_WritesEachLevelInOneInsert()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync($$"""mutation { projects(sync: { name: "p", tasks: [{{Tasks(5, 2)}}] }) }""", "2");

        PostgresGraphQlHost.AssertNoErrors(reply);
        await AssertTreeAsync(owner: 2, tasks: 5, stepsPerTask: 2);
    }

    [SkippableFact]
    public async Task Tree_OfMoreNodesThanBatchMaxSize_IsRefused_AndWritesNothing()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync($$"""mutation { projects(save: { name: "p", tasks: [{{Tasks(100, 0)}}] }) }""", "3");

        PostgresGraphQlHost.FirstError(reply).GetProperty("message").GetString()
            .Should().Contain("exceeds maximum allowed size of 100");
        (await _host.CountAsync("SELECT count(*) FROM projects WHERE owner_id = 3")).Should().Be(0);
        (await _host.CountAsync("SELECT count(*) FROM tasks WHERE owner_id = 3")).Should().Be(0);
        _chainRuns.Should().NotContain("3", "the tree is refused before any node runs its chain");
    }

    private static string Tasks(int tasks, int stepsPerTask) => string.Join(", ", Enumerable.Range(0, tasks).Select(t =>
        stepsPerTask == 0
            ? $$"""{ name: "t{{t}}" }"""
            : $$"""{ name: "t{{t}}", steps: [{{string.Join(", ", Enumerable.Range(0, stepsPerTask).Select(s => $$"""{ name: "t{{t}}-s{{s}}" }"""))}}] }"""));

    private async Task AssertTreeAsync(int owner, int tasks, int stepsPerTask)
    {
        (await _host!.RowsAsync($"SELECT table_name FROM insert_statements WHERE owner_id = {owner} ORDER BY table_name"))
            .Select(r => r["table_name"]).Should().Equal(new[] { "projects", "steps", "tasks" }, "one INSERT per level");
        (await _host.CountAsync(
            $"SELECT count(*) FROM tasks t JOIN projects p ON p.id = t.project_id WHERE t.owner_id = {owner} AND p.owner_id = {owner}"))
            .Should().Be(tasks);
        (await _host.CountAsync(
            $"SELECT count(*) FROM steps s JOIN tasks t ON t.id = s.task_id WHERE s.owner_id = {owner} AND s.name LIKE t.name || '-%'"))
            .Should().Be(tasks * stepsPerTask, "every step is under its own task");
    }

    /// <summary>Stamps the caller as owner on every node, and counts its runs per caller.</summary>
    private sealed class StampOwner(ConcurrentQueue<string> runs) : IMutationTransformer
    {
        public int Priority => 200;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => mutationType == MutationType.Insert && table.DbName is "projects" or "tasks" or "steps";

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            var user = PolicyIdentity.FromUserContext(context.UserContext).Id;
            runs.Enqueue(user);
            data["owner_id"] = int.Parse(user, CultureInfo.InvariantCulture);
            return ValueTask.FromResult(new MutationTransformResult { MutationType = mutationType, Data = data });
        }
    }
}
