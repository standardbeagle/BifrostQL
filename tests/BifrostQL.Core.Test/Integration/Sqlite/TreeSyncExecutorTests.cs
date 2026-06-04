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
/// Integration tests for <see cref="TreeSyncExecutor"/> against a real SQLite
/// database: a nested insert of a parent with polymorphic children must persist
/// the parent, propagate its generated PK into each child's id column, and stamp
/// the discriminator constant.
/// </summary>
public sealed class TreeSyncExecutorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _factory;

    public TreeSyncExecutorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-treesync-{Guid.NewGuid():N}.db");
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

    private async Task CreateSchemaAsync()
    {
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE companies (company_id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT)", null, 30, 1000);
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE notes (note_id INTEGER PRIMARY KEY AUTOINCREMENT, entity_type TEXT, entity_id INTEGER, content TEXT)", null, 30, 1000);
    }

    [Fact]
    public async Task NestedInsert_PolymorphicChildren_AutoLinkAndStampDiscriminator()
    {
        await CreateSchemaAsync();
        var model = BuildModel();
        var engine = new TreeSyncEngine(model);
        var companies = model.GetTableFromDbName("companies");

        var submitted = new Dictionary<string, object?>
        {
            ["name"] = "Acme",
            ["notes"] = new List<Dictionary<string, object?>>
            {
                new() { ["content"] = "First" },
                new() { ["content"] = "Second" },
            },
        };

        var ops = engine.ComputeOperations(companies, submitted, existing: null);
        var executor = new TreeSyncExecutor(_factory.Dialect);

        var rootId = await executor.ExecuteAsync(ops, _factory);

        // Root company persisted and its id returned.
        rootId.Should().NotBeNull();
        var companyId = Convert.ToInt64(rootId);

        // Both notes auto-linked to the company with the discriminator stamped.
        var notes = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT entity_type, entity_id, content FROM notes ORDER BY note_id", null, 30, 1000);
        notes.Rows.Should().HaveCount(2);
        notes.Rows.Should().OnlyContain(r => (string)r[0]! == "company");
        notes.Rows.Should().OnlyContain(r => Convert.ToInt64(r[1]) == companyId);
        notes.Rows.Select(r => (string)r[2]!).Should().BeEquivalentTo(new[] { "First", "Second" });
    }

    [Fact]
    public async Task NestedInsert_IsAtomic_RollsBackOnChildFailure()
    {
        await CreateSchemaAsync();
        var model = BuildModel();
        var engine = new TreeSyncEngine(model);
        var companies = model.GetTableFromDbName("companies");

        // Force a child failure: a note with a column that does not exist.
        var submitted = new Dictionary<string, object?>
        {
            ["name"] = "Acme",
            ["notes"] = new List<Dictionary<string, object?>>
            {
                new() { ["content"] = "ok" },
            },
        };
        var ops = engine.ComputeOperations(companies, submitted, existing: null).ToList();
        // Corrupt the child op so its INSERT fails after the parent succeeds.
        ops.First(o => o.Table.DbName == "notes").Data["nonexistent_column"] = "boom";

        var executor = new TreeSyncExecutor(_factory.Dialect);

        var act = async () => await executor.ExecuteAsync(ops, _factory);
        await act.Should().ThrowAsync<BifrostExecutionError>();

        // Parent insert must have rolled back — no orphaned company.
        var companiesRows = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT COUNT(*) FROM companies", null, 30, 1000);
        Convert.ToInt64(companiesRows.Rows[0][0]).Should().Be(0);
    }
}
