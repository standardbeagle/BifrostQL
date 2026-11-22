using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// Nested reads over keys PostgreSQL reads as text. The read projection casts a <c>uuid</c>
/// (and a <c>date</c>) column to text, so a parent row carries its key as a string; the
/// rows a link reads must carry the same key the same way, or no child matches its parent
/// and every to-one link answers null and every collection answers empty. Each fact reads a
/// link shape through the production registrar over HTTP: to-one, to-many, many-to-many,
/// nested two deep, a composite key with a uuid column, a row aggregate, and a date key.
/// </summary>
public sealed class PostgresUuidJoinTests : IAsyncLifetime
{
    private const string Alpha = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Beta = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string Design = "dddddddd-0000-0000-0000-000000000001";
    private const string Build = "dddddddd-0000-0000-0000-000000000002";
    private const string Review = "dddddddd-0000-0000-0000-000000000003";

    private const string Ddl =
        $"""
        CREATE TABLE projects (id UUID PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE tasks (
            id UUID PRIMARY KEY,
            project_id UUID NOT NULL REFERENCES projects (id),
            name TEXT NOT NULL);
        CREATE TABLE workdays (day DATE PRIMARY KEY, label TEXT NOT NULL);
        CREATE TABLE time_entries (
            id UUID PRIMARY KEY,
            project_id UUID NOT NULL REFERENCES projects (id),
            task_id UUID NULL REFERENCES tasks (id),
            day DATE NULL REFERENCES workdays (day),
            note TEXT NOT NULL,
            hours INT NOT NULL);
        CREATE TABLE tags (id UUID PRIMARY KEY, label TEXT NOT NULL);
        CREATE TABLE time_entry_tags (
            time_entry_id UUID NOT NULL REFERENCES time_entries (id),
            tag_id UUID NOT NULL REFERENCES tags (id),
            PRIMARY KEY (time_entry_id, tag_id));
        CREATE TABLE phases (
            project_id UUID NOT NULL REFERENCES projects (id),
            seq INT NOT NULL,
            name TEXT NOT NULL,
            PRIMARY KEY (project_id, seq));
        CREATE TABLE milestones (
            id UUID PRIMARY KEY,
            project_id UUID NOT NULL,
            phase_seq INT NOT NULL,
            title TEXT NOT NULL,
            FOREIGN KEY (project_id, phase_seq) REFERENCES phases (project_id, seq));

        INSERT INTO projects (id, name) VALUES ('{Alpha}', 'alpha'), ('{Beta}', 'beta');
        INSERT INTO tasks (id, project_id, name) VALUES
            ('{Design}', '{Alpha}', 'design'), ('{Build}', '{Alpha}', 'build'), ('{Review}', '{Beta}', 'review');
        INSERT INTO workdays (day, label) VALUES ('2026-11-02', 'monday'), ('2026-11-03', 'tuesday');
        INSERT INTO time_entries (id, project_id, task_id, day, note, hours) VALUES
            ('eeeeeeee-0000-0000-0000-000000000001', '{Alpha}', '{Design}', '2026-11-02', 'e1', 2),
            ('eeeeeeee-0000-0000-0000-000000000002', '{Alpha}', '{Build}', '2026-11-03', 'e2', 3),
            ('eeeeeeee-0000-0000-0000-000000000003', '{Beta}', '{Review}', '2026-11-02', 'e3', 5),
            ('eeeeeeee-0000-0000-0000-000000000004', '{Beta}', NULL, NULL, 'e4', 7);
        INSERT INTO tags (id, label) VALUES
            ('cccccccc-0000-0000-0000-000000000001', 'billable'), ('cccccccc-0000-0000-0000-000000000002', 'remote');
        INSERT INTO time_entry_tags (time_entry_id, tag_id) VALUES
            ('eeeeeeee-0000-0000-0000-000000000001', 'cccccccc-0000-0000-0000-000000000001'),
            ('eeeeeeee-0000-0000-0000-000000000001', 'cccccccc-0000-0000-0000-000000000002'),
            ('eeeeeeee-0000-0000-0000-000000000003', 'cccccccc-0000-0000-0000-000000000002');
        INSERT INTO phases (project_id, seq, name) VALUES
            ('{Alpha}', 1, 'alpha-one'), ('{Alpha}', 2, 'alpha-two'), ('{Beta}', 1, 'beta-one');
        INSERT INTO milestones (id, project_id, phase_seq, title) VALUES
            ('ffffffff-0000-0000-0000-000000000001', '{Alpha}', 2, 'm-alpha-two'),
            ('ffffffff-0000-0000-0000-000000000002', '{Beta}', 1, 'm-beta-one');
        """;

    private static readonly string[] Metadata =
    {
        "public.time_entries { many-to-many: tags:time_entry_tags }",
    };

    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync("bifrost_uuid_join", Ddl, Metadata);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task ToOneLinks_OverUuidKeys_ResolveTheirParents()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var data = await DataAsync("{ time_entries(sort: [note_asc]) { data { note projects { name } tasks { name } } } }");

        data.Should().Be(
            """{"time_entries":{"data":[""" +
            """{"note":"e1","projects":{"name":"alpha"},"tasks":{"name":"design"}},""" +
            """{"note":"e2","projects":{"name":"alpha"},"tasks":{"name":"build"}},""" +
            """{"note":"e3","projects":{"name":"beta"},"tasks":{"name":"review"}},""" +
            """{"note":"e4","projects":{"name":"beta"},"tasks":null}]}}""");
    }

    [SkippableFact]
    public async Task ToManyLinks_OverUuidKeys_CollectTheirChildren()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var data = await DataAsync("{ projects(sort: [name_asc]) { data { name time_entries(sort: [note_asc]) { total data { note } } } } }");

        data.Should().Be(
            """{"projects":{"data":[""" +
            """{"name":"alpha","time_entries":{"total":2,"data":[{"note":"e1"},{"note":"e2"}]}},""" +
            """{"name":"beta","time_entries":{"total":2,"data":[{"note":"e3"},{"note":"e4"}]}}]}}""");
    }

    [SkippableFact]
    public async Task ManyToManyLinks_OverUuidKeys_CollectThroughTheJunction()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var data = await DataAsync("{ time_entries(sort: [note_asc]) { data { note tags(sort: [label_asc]) { data { label } } } } }");

        data.Should().Be(
            """{"time_entries":{"data":[""" +
            """{"note":"e1","tags":{"data":[{"label":"billable"},{"label":"remote"}]}},""" +
            """{"note":"e2","tags":{"data":[]}},""" +
            """{"note":"e3","tags":{"data":[{"label":"remote"}]}},""" +
            """{"note":"e4","tags":{"data":[]}}]}}""");
    }

    [SkippableFact]
    public async Task LinksNestedTwoDeep_OverUuidKeys_ResolveAtEveryLevel()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var data = await DataAsync(
            "{ projects(sort: [name_asc]) { data { name tasks(sort: [name_asc]) { data { name projects { name } time_entries { data { note } } } } } } }");

        data.Should().Be(
            """{"projects":{"data":[""" +
            """{"name":"alpha","tasks":{"data":[""" +
            """{"name":"build","projects":{"name":"alpha"},"time_entries":{"data":[{"note":"e2"}]}},""" +
            """{"name":"design","projects":{"name":"alpha"},"time_entries":{"data":[{"note":"e1"}]}}]}},""" +
            """{"name":"beta","tasks":{"data":[""" +
            """{"name":"review","projects":{"name":"beta"},"time_entries":{"data":[{"note":"e3"}]}}]}}]}}""");
    }

    [SkippableFact]
    public async Task CompositeKeyLinks_WithAUuidColumn_ResolveBothWays()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var toOne = await DataAsync("{ milestones(sort: [title_asc]) { data { title phases { name } } } }");
        var toMany = await DataAsync("{ phases(sort: [name_asc]) { data { name milestones { data { title } } } } }");

        toOne.Should().Be(
            """{"milestones":{"data":[""" +
            """{"title":"m-alpha-two","phases":{"name":"alpha-two"}},""" +
            """{"title":"m-beta-one","phases":{"name":"beta-one"}}]}}""");
        toMany.Should().Be(
            """{"phases":{"data":[""" +
            """{"name":"alpha-one","milestones":{"data":[]}},""" +
            """{"name":"alpha-two","milestones":{"data":[{"title":"m-alpha-two"}]}},""" +
            """{"name":"beta-one","milestones":{"data":[{"title":"m-beta-one"}]}}]}}""");
    }

    [SkippableFact]
    public async Task RowAggregates_OverUuidKeys_CorrelateToTheirRows()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var data = await DataAsync(
            "{ projects(sort: [name_asc]) { data { name _agg(value: { time_entries: { column: hours } } operation: Sum) } } }");

        data.Should().Be(
            """{"projects":{"data":[{"name":"alpha","_agg":5},{"name":"beta","_agg":12}]}}""");
    }

    [SkippableFact]
    public async Task Links_OverDateKeys_ResolveBothWays()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var toOne = await DataAsync("{ time_entries(sort: [note_asc]) { data { note workdays { label } } } }");
        var toMany = await DataAsync("{ workdays(sort: [day_asc]) { data { label time_entries(sort: [note_asc]) { data { note } } } } }");

        toOne.Should().Be(
            """{"time_entries":{"data":[""" +
            """{"note":"e1","workdays":{"label":"monday"}},""" +
            """{"note":"e2","workdays":{"label":"tuesday"}},""" +
            """{"note":"e3","workdays":{"label":"monday"}},""" +
            """{"note":"e4","workdays":null}]}}""");
        toMany.Should().Be(
            """{"workdays":{"data":[""" +
            """{"label":"monday","time_entries":{"data":[{"note":"e1"},{"note":"e3"}]}},""" +
            """{"label":"tuesday","time_entries":{"data":[{"note":"e2"}]}}]}}""");
    }

    private async Task<string> DataAsync(string query)
    {
        using var reply = await _host!.PostAsync(query);
        PostgresGraphQlHost.AssertNoErrors(reply);
        return reply.RootElement.GetProperty("data").GetRawText();
    }
}
