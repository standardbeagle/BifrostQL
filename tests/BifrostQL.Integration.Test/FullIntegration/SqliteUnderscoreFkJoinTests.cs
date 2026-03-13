using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using RootExecutionNode = GraphQL.Execution.RootExecutionNode;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// Integration tests for underscore-separated FK columns (workspace_id, member_id, etc.)
/// through the full runtime pipeline: schema load → GraphQL schema → query → SQL → result assembly.
/// Uses paged query format and correct RootExecutionNode extraction.
/// </summary>
[Collection("SqliteUnderscoreFkJoin")]
public class SqliteUnderscoreFkJoinTests : FullIntegrationTestBase, IAsyncLifetime
{
    private SqliteConnection? _keepAliveConnection;

    public async Task InitializeAsync()
    {
        var connectionString = "Data Source=bifrost_fk_test;Mode=Memory;Cache=Shared";
        _keepAliveConnection = new SqliteConnection(connectionString);
        await _keepAliveConnection.OpenAsync();

        var factory = new SqliteDbConnFactory(connectionString);
        await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync);
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();
        if (_keepAliveConnection != null)
            await _keepAliveConnection.DisposeAsync();
    }

    private static async Task CreateSchemaAsync(System.Data.Common.DbConnection conn)
    {
        var statements = new[]
        {
            "DROP TABLE IF EXISTS tasks",
            "DROP TABLE IF EXISTS projects",
            "DROP TABLE IF EXISTS members",
            "DROP TABLE IF EXISTS workspaces",
            @"CREATE TABLE workspaces (
                workspace_id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL
            )",
            @"CREATE TABLE members (
                member_id INTEGER PRIMARY KEY AUTOINCREMENT,
                workspace_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (workspace_id) REFERENCES workspaces(workspace_id)
            )",
            @"CREATE TABLE projects (
                project_id INTEGER PRIMARY KEY AUTOINCREMENT,
                workspace_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (workspace_id) REFERENCES workspaces(workspace_id)
            )",
            @"CREATE TABLE tasks (
                task_id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (project_id) REFERENCES projects(project_id)
            )"
        };

        foreach (var sql in statements)
        {
            var cmd = new SqliteCommand(sql, (SqliteConnection)conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedDataAsync(System.Data.Common.DbConnection conn)
    {
        var statements = new[]
        {
            "INSERT INTO workspaces (name) VALUES ('Acme Corp'), ('Beta Inc')",
            "INSERT INTO members (workspace_id, name) VALUES (1, 'Alice'), (1, 'Bob'), (2, 'Charlie')",
            "INSERT INTO projects (workspace_id, name) VALUES (1, 'Website'), (1, 'Mobile App'), (2, 'Dashboard')",
            "INSERT INTO tasks (project_id, name) VALUES (1, 'Design'), (1, 'Develop'), (2, 'Prototype'), (3, 'Setup')"
        };

        foreach (var sql in statements)
        {
            var cmd = new SqliteCommand(sql, (SqliteConnection)conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    #region Helpers

    private static List<Dictionary<string, JsonElement>> ExtractPagedData(ExecutionResult result, string tableName)
    {
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();
        var root = (Dictionary<string, object?>)((RootExecutionNode)result.Data!).ToValue()!;
        root.Should().ContainKey(tableName);
        var json = JsonSerializer.Serialize(root[tableName]);
        var paged = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        paged.Should().ContainKey("data");
        var dataJson = paged["data"].GetRawText();
        return JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(dataJson)!;
    }

    private static string Str(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null! : e.ToString();
    private static long Long(JsonElement e) => e.GetInt64();

    #endregion

    #region Schema loads correctly

    [Fact]
    public void Schema_HasUnderscoreFkLinks()
    {
        var members = Model.GetTableFromDbName("members");
        var projects = Model.GetTableFromDbName("projects");
        var workspaces = Model.GetTableFromDbName("workspaces");

        members.SingleLinks.Should().ContainKey("workspaces", "members should have single-link to workspaces");
        projects.SingleLinks.Should().ContainKey("workspaces", "projects should have single-link to workspaces");
        workspaces.MultiLinks.Should().ContainKey("members", "workspaces should have multi-link to members");
        workspaces.MultiLinks.Should().ContainKey("projects", "workspaces should have multi-link to projects");
    }

    #endregion

    #region Scalar queries (paged format)

    [Fact]
    public async Task Query_AllMembers_ReturnsPaged()
    {
        var query = "query { members { data { member_id workspace_id name } } }";
        var rows = ExtractPagedData(await ExecuteQueryAsync(query), "members");

        rows.Should().HaveCount(3);
        Str(rows[0]["name"]).Should().Be("Alice");
    }

    [Fact]
    public async Task Query_AllWorkspaces_ReturnsPaged()
    {
        var query = "query { workspaces { data { workspace_id name } } }";
        var rows = ExtractPagedData(await ExecuteQueryAsync(query), "workspaces");

        rows.Should().HaveCount(2);
    }

    #endregion

    #region Single-link joins (ManyToOne)

    [Fact]
    public async Task Query_MembersWithWorkspace_SingleLink()
    {
        var query = @"query {
            members {
                data {
                    member_id
                    name
                    workspaces {
                        workspace_id
                        name
                    }
                }
            }
        }";

        var rows = ExtractPagedData(await ExecuteQueryAsync(query), "members");

        rows.Should().HaveCount(3);

        // Alice is in workspace 1 (Acme Corp)
        var alice = rows.First(r => Str(r["name"]) == "Alice");
        var ws = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(alice["workspaces"].GetRawText())!;
        Str(ws["name"]).Should().Be("Acme Corp");

        // Charlie is in workspace 2 (Beta Inc)
        var charlie = rows.First(r => Str(r["name"]) == "Charlie");
        var ws2 = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(charlie["workspaces"].GetRawText())!;
        Str(ws2["name"]).Should().Be("Beta Inc");
    }

    [Fact]
    public async Task Query_ProjectsWithWorkspace_SingleLink()
    {
        var query = @"query {
            projects {
                data {
                    project_id
                    name
                    workspaces {
                        workspace_id
                        name
                    }
                }
            }
        }";

        var rows = ExtractPagedData(await ExecuteQueryAsync(query), "projects");

        rows.Should().HaveCount(3);

        var website = rows.First(r => Str(r["name"]) == "Website");
        var ws = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(website["workspaces"].GetRawText())!;
        Str(ws["name"]).Should().Be("Acme Corp");
    }

    [Fact]
    public async Task Query_TasksWithProject_SingleLink()
    {
        var query = @"query {
            tasks {
                data {
                    task_id
                    name
                    projects {
                        project_id
                        name
                    }
                }
            }
        }";

        var rows = ExtractPagedData(await ExecuteQueryAsync(query), "tasks");

        rows.Should().HaveCount(4);

        var design = rows.First(r => Str(r["name"]) == "Design");
        var proj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(design["projects"].GetRawText())!;
        Str(proj["name"]).Should().Be("Website");
    }

    #endregion

    #region Multi-link joins (OneToMany)

    [Fact]
    public async Task Query_WorkspacesWithMembers_MultiLink()
    {
        var query = @"query {
            workspaces {
                data {
                    workspace_id
                    name
                    members {
                        member_id
                        name
                    }
                }
            }
        }";

        var rows = ExtractPagedData(await ExecuteQueryAsync(query), "workspaces");

        rows.Should().HaveCount(2);

        // Acme Corp has 2 members
        var acme = rows.First(r => Str(r["name"]) == "Acme Corp");
        var members = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(acme["members"].GetRawText())!;
        members.Should().HaveCount(2);
    }

    [Fact]
    public async Task Query_WorkspacesWithProjects_MultiLink()
    {
        var query = @"query {
            workspaces {
                data {
                    workspace_id
                    name
                    projects {
                        project_id
                        name
                    }
                }
            }
        }";

        var rows = ExtractPagedData(await ExecuteQueryAsync(query), "workspaces");

        var acme = rows.First(r => Str(r["name"]) == "Acme Corp");
        var projects = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(acme["projects"].GetRawText())!;
        projects.Should().HaveCount(2);
    }

    #endregion

    #region Nested joins (two levels deep)

    [Fact]
    public async Task Query_TasksWithProjectAndWorkspace_NestedSingleLinks()
    {
        var query = @"query {
            tasks {
                data {
                    task_id
                    name
                    projects {
                        project_id
                        name
                        workspaces {
                            workspace_id
                            name
                        }
                    }
                }
            }
        }";

        var rows = ExtractPagedData(await ExecuteQueryAsync(query), "tasks");

        var design = rows.First(r => Str(r["name"]) == "Design");
        var proj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(design["projects"].GetRawText())!;
        Str(proj["name"]).Should().Be("Website");

        var ws = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(proj["workspaces"].GetRawText())!;
        Str(ws["name"]).Should().Be("Acme Corp");
    }

    [Fact]
    public async Task Query_WorkspacesWithMembersAndProjects_NestedMultiLinks()
    {
        var query = @"query {
            workspaces(filter: { workspace_id: { _eq: 1 } }) {
                data {
                    name
                    members { member_id name }
                    projects {
                        name
                        tasks { task_id name }
                    }
                }
            }
        }";

        var rows = ExtractPagedData(await ExecuteQueryAsync(query), "workspaces");

        rows.Should().HaveCount(1);
        Str(rows[0]["name"]).Should().Be("Acme Corp");

        var members = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(rows[0]["members"].GetRawText())!;
        members.Should().HaveCount(2);

        var projects = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(rows[0]["projects"].GetRawText())!;
        projects.Should().HaveCount(2);

        var website = projects.First(p => Str(p["name"]) == "Website");
        var tasks = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(website["tasks"].GetRawText())!;
        tasks.Should().HaveCount(2);
    }

    #endregion

    #region Frontend query pattern (with GraphQL aliases)

    [Fact]
    public async Task Query_FrontendPattern_SingleLinkWithAliases()
    {
        // This is the exact pattern the edit-db frontend generates:
        // workspace_id workspaces { id: workspace_id label: name }
        var query = @"query {
            members {
                data {
                    member_id
                    workspace_id
                    name
                    workspaces { id: workspace_id label: name }
                }
            }
        }";

        var rows = ExtractPagedData(await ExecuteQueryAsync(query), "members");

        rows.Should().HaveCount(3);

        var alice = rows.First(r => Str(r["name"]) == "Alice");
        // workspace_id should be available as a scalar
        alice["workspace_id"].GetInt64().Should().Be(1);

        // workspaces should be the joined workspace with aliased fields
        var ws = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(alice["workspaces"].GetRawText())!;
        ws.Should().ContainKey("id");
        ws.Should().ContainKey("label");
        Str(ws["label"]).Should().Be("Acme Corp");
    }

    [Fact]
    public async Task Query_FrontendPattern_DuplicateAliasForSameColumn()
    {
        // When labelColumn == PK, frontend generates both aliases pointing to same DB column:
        // workspaces { id: workspace_id label: workspace_id }
        // This was the root cause of "Unable to find queryField: workspace_id on table: :labels"
        var query = @"query {
            members {
                data {
                    member_id
                    workspace_id
                    name
                    workspaces { id: workspace_id label: workspace_id }
                }
            }
        }";

        var rows = ExtractPagedData(await ExecuteQueryAsync(query), "members");

        rows.Should().HaveCount(3);

        var alice = rows.First(r => Str(r["name"]) == "Alice");
        var ws = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(alice["workspaces"].GetRawText())!;
        ws.Should().ContainKey("id");
        ws.Should().ContainKey("label");
        // Both should equal workspace_id value = 1
        ws["id"].GetInt64().Should().Be(1);
        ws["label"].GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task Query_FrontendPattern_WithSort()
    {
        // Frontend always includes sort
        var query = @"query($sort: [membersSortEnum!], $limit: Int, $offset: Int) {
            members(sort: $sort, limit: $limit, offset: $offset) {
                total offset limit
                data {
                    member_id
                    workspace_id
                    name
                    workspaces { id: workspace_id label: name }
                }
            }
        }";

        var variables = new Dictionary<string, object?>
        {
            { "sort", new[] { "member_id_asc" } },
            { "limit", 10 },
            { "offset", 0 }
        };

        var rows = ExtractPagedData(await ExecuteQueryAsync(query, variables), "members");
        rows.Should().HaveCount(3);
    }

    #endregion
}
