using BifrostQL.Core.Model;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Integration tests for the full nested reconcile path (loader + engine +
/// executor) against real SQLite: a submitted tree with a known root key is
/// diffed against existing state and the database is reconciled — update changed
/// rows, insert new children, delete orphans — with polymorphic collections
/// scoped per parent and discriminator.
/// </summary>
public sealed class TreeSyncReconcileTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _factory;

    public TreeSyncReconcileTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-reconcile-{Guid.NewGuid():N}.db");
        _factory = new SqliteDbConnFactory($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static IDbModel BuildModel()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("companies", t => t
                .WithPrimaryKey("company_id")
                .WithColumn("name", "nvarchar"))
            .WithTable("notes", t => t
                .WithPrimaryKey("note_id")
                .WithColumn("entity_type", "nvarchar")
                .WithColumn("entity_id", "int")
                .WithColumn("content", "nvarchar")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicTypeCol, "entity_type")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicIdCol, "entity_id")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicMap, "company=companies"))
            .Build();
        new PolymorphicRelationshipStrategy().DiscoverRelationships(model);
        return model;
    }

    private async Task SeedAsync()
    {
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE companies (company_id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT)", null, 30, 1000);
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE notes (note_id INTEGER PRIMARY KEY AUTOINCREMENT, entity_type TEXT, entity_id INTEGER, content TEXT)", null, 30, 1000);
        // Company 1 with two notes; company 2 with one note (must stay untouched).
        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO companies (company_id, name) VALUES (1,'Acme'),(2,'Globex')", null, 30, 1000);
        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO notes (note_id, entity_type, entity_id, content) VALUES " +
            "(10,'company',1,'keep me'),(11,'company',1,'delete me'),(20,'company',2,'other company')",
            null, 30, 1000);
    }

    private async Task<object?> SyncAsync(Dictionary<string, object?> tree)
    {
        var model = BuildModel();
        var companies = model.GetTableFromDbName("companies");
        var loader = new TreeSyncStateLoader(_factory.Dialect);
        var existing = await loader.LoadAsync(companies, tree, _factory);
        var ops = new TreeSyncEngine(model).ComputeOperations(companies, tree, existing);
        return await new TreeSyncExecutor(_factory.Dialect).ExecuteAsync(ops, _factory);
    }

    [Fact]
    public async Task Reconcile_UpdatesRoot_EditsKeptChild_DeletesOrphan_InsertsNewChild()
    {
        await SeedAsync();

        // Submit company 1: renamed, note 10 edited, note 11 omitted (orphan),
        // and a brand-new note added.
        var tree = new Dictionary<string, object?>
        {
            ["company_id"] = 1L,
            ["name"] = "Acme Renamed",
            ["notes"] = new List<Dictionary<string, object?>>
            {
                new() { ["note_id"] = 10L, ["content"] = "kept and edited" },
                new() { ["content"] = "brand new note" },
            },
        };

        await SyncAsync(tree);

        var company = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT name FROM companies WHERE company_id = 1", null, 30, 1000);
        ((string)company.Rows[0][0]!).Should().Be("Acme Renamed");

        var notes = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT note_id, entity_type, entity_id, content FROM notes WHERE entity_id = 1 AND entity_type='company' ORDER BY note_id",
            null, 30, 1000);
        // note 10 edited, note 11 orphan-deleted, one new note added.
        notes.Rows.Should().HaveCount(2);
        Convert.ToInt64(notes.Rows[0][0]).Should().Be(10);
        ((string)notes.Rows[0][3]!).Should().Be("kept and edited");
        // The new note auto-linked with discriminator + id.
        var newNote = notes.Rows[1];
        ((string)newNote[1]!).Should().Be("company");
        Convert.ToInt64(newNote[2]).Should().Be(1);
        ((string)newNote[3]!).Should().Be("brand new note");
    }

    [Fact]
    public async Task Reconcile_DoesNotTouchOtherParentsPolymorphicRows()
    {
        await SeedAsync();

        // Reconcile company 1 down to a single note; company 2's note must survive.
        var tree = new Dictionary<string, object?>
        {
            ["company_id"] = 1L,
            ["name"] = "Acme",
            ["notes"] = new List<Dictionary<string, object?>>
            {
                new() { ["note_id"] = 10L, ["content"] = "keep me" },
            },
        };

        await SyncAsync(tree);

        var otherNote = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT content FROM notes WHERE note_id = 20", null, 30, 1000);
        otherNote.Rows.Should().ContainSingle();
        ((string)otherNote.Rows[0][0]!).Should().Be("other company");

        // Company 1 ends with exactly one note (orphan 11 removed).
        var c1 = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT COUNT(*) FROM notes WHERE entity_id = 1 AND entity_type='company'", null, 30, 1000);
        Convert.ToInt64(c1.Rows[0][0]).Should().Be(1);
    }
}
