using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Multi-level reconcile coverage for two engine/loader fixes:
///   - the state loader must descend past depth 1 (grand-links populated), so an
///     already-existing depth-2 row is UPDATEd, not re-INSERTed into a PK violation;
///   - the executor must resolve each child's deferred FK to its OWN parent
///     instance, so two sibling parents of the same table (each owning their own
///     children) do not collapse — attaching the first parent's children to the
///     second parent's PK.
/// </summary>
public sealed class TreeSyncMultiLevelTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _factory;

    public TreeSyncMultiLevelTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-treesync-ml-{Guid.NewGuid():N}.db");
        _factory = new SqliteDbConnFactory($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    // organizations -> teams -> members (single-column FKs), explicit links so the
    // link names in the submitted tree are deterministic.
    private static IDbModel BuildModel() =>
        DbModelTestFixture.Create()
            .WithTable("organizations", t => t
                .WithPrimaryKey("org_id")
                .WithColumn("name", "nvarchar"))
            .WithTable("teams", t => t
                .WithPrimaryKey("team_id")
                .WithColumn("org_id", "int")
                .WithColumn("name", "nvarchar"))
            .WithTable("members", t => t
                .WithPrimaryKey("member_id")
                .WithColumn("team_id", "int")
                .WithColumn("name", "nvarchar"))
            .WithSingleLink("teams", "org_id", "organizations", "org_id", "organization")
            .WithMultiLink("organizations", "org_id", "teams", "org_id", "teams")
            .WithSingleLink("members", "team_id", "teams", "team_id", "team")
            .WithMultiLink("teams", "team_id", "members", "team_id", "members")
            .Build();

    private async Task CreateSchemaAsync()
    {
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE organizations (org_id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT)", null, 30, 1000);
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE teams (team_id INTEGER PRIMARY KEY AUTOINCREMENT, org_id INTEGER, name TEXT)", null, 30, 1000);
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE members (member_id INTEGER PRIMARY KEY AUTOINCREMENT, team_id INTEGER, name TEXT)", null, 30, 1000);
    }

    [Fact]
    public async Task Reconcile_ExistingGrandchild_IsUpdated_NotReInserted()
    {
        await CreateSchemaAsync();
        // Seed a full 3-level tree.
        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO organizations (org_id, name) VALUES (1,'Org')", null, 30, 1000);
        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO teams (team_id, org_id, name) VALUES (10,1,'Team')", null, 30, 1000);
        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO members (member_id, team_id, name) VALUES (100,10,'old name')", null, 30, 1000);

        var model = BuildModel();
        var organizations = model.GetTableFromDbName("organizations");

        // Submit the same tree with the grandchild's PK present and its name changed.
        var tree = new Dictionary<string, object?>
        {
            ["org_id"] = 1L,
            ["name"] = "Org",
            ["teams"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["team_id"] = 10L,
                    ["name"] = "Team",
                    ["members"] = new List<Dictionary<string, object?>>
                    {
                        new() { ["member_id"] = 100L, ["name"] = "new name" },
                    },
                },
            },
        };

        var loader = new TreeSyncStateLoader(_factory.Dialect);
        var existing = await loader.LoadAsync(organizations, tree, _factory);
        var ops = new TreeSyncEngine(model).ComputeOperations(organizations, tree, existing);

        // The grandchild must be reconciled as an UPDATE (loader saw it), never an
        // INSERT (which would violate the member_id PK).
        ops.Should().NotContain(o =>
            o.Table.DbName == "members" && o.OperationType == TreeSyncOperationType.Insert);

        await new TreeSyncExecutor(_factory.Dialect).ExecuteAsync(ops, _factory);

        var members = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT member_id, name FROM members", null, 30, 1000);
        members.Rows.Should().ContainSingle();
        Convert.ToInt64(members.Rows[0][0]).Should().Be(100);
        ((string)members.Rows[0][1]!).Should().Be("new name");
    }

    [Fact]
    public async Task Reconcile_TwoSiblingParentsSameTable_EachChildLinksToOwnParent()
    {
        await CreateSchemaAsync();

        var model = BuildModel();
        var organizations = model.GetTableFromDbName("organizations");

        // One new org with TWO new teams, each owning its own new member.
        var tree = new Dictionary<string, object?>
        {
            ["name"] = "Org",
            ["teams"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["name"] = "TeamA",
                    ["members"] = new List<Dictionary<string, object?>> { new() { ["name"] = "MemberA" } },
                },
                new()
                {
                    ["name"] = "TeamB",
                    ["members"] = new List<Dictionary<string, object?>> { new() { ["name"] = "MemberB" } },
                },
            },
        };

        var ops = new TreeSyncEngine(model).ComputeOperations(organizations, tree, existing: null);
        await new TreeSyncExecutor(_factory.Dialect).ExecuteAsync(ops, _factory);

        // Each member must link to the team it was nested under — not both to the
        // last-inserted team.
        var joined = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT m.name, t.name FROM members m JOIN teams t ON m.team_id = t.team_id ORDER BY m.name",
            null, 30, 1000);
        joined.Rows.Should().HaveCount(2);
        ((string)joined.Rows[0][0]!).Should().Be("MemberA");
        ((string)joined.Rows[0][1]!).Should().Be("TeamA");
        ((string)joined.Rows[1][0]!).Should().Be("MemberB");
        ((string)joined.Rows[1][1]!).Should().Be("TeamB");
    }
}
