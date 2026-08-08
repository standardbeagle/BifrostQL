using BifrostQL.Core.Model;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Verifies the fix for the HIGH finding: <see cref="TreeSyncStateLoader"/> read
/// the root row and every child collection by raw PK/FK SQL, bypassing every
/// filter transformer. Before the fix, a caller could submit another tenant's
/// primary key and the loader would happily return that tenant's row (an
/// existence-probe / cross-tenant-read bug), and out-of-scope children would be
/// diffed as if the caller could see them.
///
/// The fix ANDs each loaded table's combined tenant/soft-delete/policy filter
/// onto the read, when the loader is constructed with
/// <c>filterTransformers</c>/<c>model</c>/<c>userContext</c>. Those three
/// constructor parameters are optional (default null) so pre-existing callers
/// keep compiling — see the class remarks for the required follow-up to wire
/// them at the <c>DbTableMutateResolver</c> call site.
/// </summary>
public sealed class TreeSyncStateLoaderSecurityFilterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _factory;

    public TreeSyncStateLoaderSecurityFilterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-treesync-security-{Guid.NewGuid():N}.db");
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
                .WithColumn("tenant_id", "int")
                .WithColumn("name", "nvarchar")
                .WithMetadata(TenantFilterTransformer.MetadataKey, "tenant_id"))
            .WithTable("notes", t => t
                .WithPrimaryKey("note_id")
                .WithColumn("entity_type", "nvarchar")
                .WithColumn("entity_id", "int")
                .WithColumn("content", "nvarchar")
                .WithColumn("tenant_id", "int")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicTypeCol, "entity_type")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicIdCol, "entity_id")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicMap, "company=companies")
                .WithMetadata(TenantFilterTransformer.MetadataKey, "tenant_id"))
            .Build();
        new PolymorphicRelationshipStrategy().DiscoverRelationships(model);
        return model;
    }

    private async Task SeedAsync()
    {
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE companies (company_id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id INTEGER, name TEXT)", null, 30, 1000);
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE notes (note_id INTEGER PRIMARY KEY AUTOINCREMENT, entity_type TEXT, entity_id INTEGER, content TEXT, tenant_id INTEGER)", null, 30, 1000);
        // Company 1 belongs to tenant 1; company 2 belongs to tenant 2.
        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO companies (company_id, tenant_id, name) VALUES (1,1,'Acme'),(2,2,'Globex')", null, 30, 1000);
        // Company 1 has one tenant-1 note and (inconsistently) one tenant-2-tagged
        // note attached — the loader must exclude the latter for a tenant-1 caller.
        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO notes (note_id, entity_type, entity_id, content, tenant_id) VALUES " +
            "(10,'company',1,'own tenant note',1),(11,'company',1,'other tenant note',2)",
            null, 30, 1000);
    }

    /// <summary>
    /// A service provider carrying only the tenant filter transformer — the shape the
    /// loader now REQUIRES, since <see cref="TreeSyncStateLoader"/> resolves its read
    /// chain (row filter, column guards, crypto projection) from services.
    /// </summary>
    private static IServiceProvider TenantFilterOnly()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new TenantFilterTransformer() },
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task LoadAsync_RootBelongingToAnotherTenant_ReturnsNull()
    {
        await SeedAsync();
        var model = BuildModel();
        var companies = model.GetTableFromDbName("companies");

        var loader = new TreeSyncStateLoader(
            _factory.Dialect,
            model,
            new Dictionary<string, object?> { ["tenant_id"] = 1 },
            TenantFilterOnly());

        // A tenant-1 caller submits company 2's PK (tenant 2's row). Before the
        // fix this would load and return tenant 2's row unfiltered — usable as an
        // existence probe. The security-filtered load must treat it as not found.
        var tree = new Dictionary<string, object?> { ["company_id"] = 2L, ["name"] = "Globex" };
        var existing = await loader.LoadAsync(companies, tree, _factory);

        existing.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_OwnTenantRoot_LoadsRowAndOnlyOwnTenantChildren()
    {
        await SeedAsync();
        var model = BuildModel();
        var companies = model.GetTableFromDbName("companies");

        var loader = new TreeSyncStateLoader(
            _factory.Dialect,
            model,
            new Dictionary<string, object?> { ["tenant_id"] = 1 },
            TenantFilterOnly());

        var tree = new Dictionary<string, object?>
        {
            ["company_id"] = 1L,
            ["name"] = "Acme",
            ["notes"] = new List<Dictionary<string, object?>>(),
        };
        var existing = await loader.LoadAsync(companies, tree, _factory);

        existing.Should().NotBeNull();
        var notes = (List<Dictionary<string, object?>>)existing!["notes"]!;
        // Only the tenant-1-tagged note is visible; the tenant-2-tagged note on
        // the same company row must not be diffed (it would otherwise be
        // orphan-deleted or silently no-op'd at execution time).
        notes.Should().ContainSingle();
        ((string)notes[0]["content"]!).Should().Be("own tenant note");
    }

    [Fact]
    public async Task LoadAsync_WithoutRegisteredTransformers_LoadsUnfiltered()
    {
        // A host that registers no filter transformers has no row security to apply,
        // so the load is unfiltered — the same way the rest of the read path degrades.
        // Note this is now the ONLY unfiltered shape: model and user context are
        // required constructor arguments, so a caller can no longer accidentally
        // construct a loader that ignores a registered transformer set (which is
        // exactly what DbTableMutateResolver did).
        await SeedAsync();
        var model = BuildModel();
        var companies = model.GetTableFromDbName("companies");

        var loader = new TreeSyncStateLoader(
            _factory.Dialect, model, new Dictionary<string, object?>());

        var tree = new Dictionary<string, object?> { ["company_id"] = 2L, ["name"] = "Globex" };
        var existing = await loader.LoadAsync(companies, tree, _factory);

        existing.Should().NotBeNull();
    }
}
