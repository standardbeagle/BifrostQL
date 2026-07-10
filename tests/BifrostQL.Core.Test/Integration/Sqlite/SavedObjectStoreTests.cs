using System.Text.Json;
using BifrostQL.Core.SavedObjects;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Behavioral contract for both <see cref="ISavedObjectStore"/> implementations — the
/// file-backed (desktop) and DB-backed (hosted, Sqlite) stores must round-trip
/// save/load/rename/delete identically and enforce the same optimistic-concurrency
/// rule. Runs every test against both backends via a member-data store factory.
/// </summary>
public sealed class SavedObjectStoreTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly List<SqliteConnection> _keepAlive = new();

    public void Dispose()
    {
        foreach (var c in _keepAlive) c.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var d in _tempDirs)
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
    }

    private ISavedObjectStore FileStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bifrost-so-{Guid.NewGuid():N}");
        _tempDirs.Add(dir);
        return new FileSavedObjectStore(dir);
    }

    private ISavedObjectStore DbStore()
    {
        var conn = $"Data Source=bifrost_so_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keep = new SqliteConnection(conn);
        keep.Open();
        _keepAlive.Add(keep);
        return new DbSavedObjectStore(new SqliteDbConnFactory(conn));
    }

    public static IEnumerable<object[]> Backends => new[]
    {
        new object[] { "file" },
        new object[] { "db" },
    };

    private ISavedObjectStore Make(string backend) => backend == "file" ? FileStore() : DbStore();

    private static SavedObject Obj(SavedObjectType type, string id, string name, int version, string definitionJson = """{"a":1}""")
        => new()
        {
            Id = id,
            Type = type,
            Name = name,
            Version = version,
            Definition = JsonDocument.Parse(definitionJson).RootElement.Clone(),
        };

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task SaveLoadRenameDelete_RoundTrips(string backend)
    {
        var store = Make(backend);

        // Create a query and a form; a create carries version 0 and lands as version 1.
        var query = await store.PutAsync(Obj(SavedObjectType.Query, "q1", "Sales by region", 0, """{"groupBy":["region"]}"""));
        var form = await store.PutAsync(Obj(SavedObjectType.Form, "f1", "Customer form", 0, """{"table":"customers"}"""));
        query.Version.Should().Be(1);
        form.Version.Should().Be(1);

        // Load back.
        var loaded = await store.GetAsync(SavedObjectType.Query, "q1");
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Sales by region");
        loaded.Definition.GetProperty("groupBy")[0].GetString().Should().Be("region");

        // List filters by type.
        (await store.ListAsync(SavedObjectType.Query)).Should().ContainSingle(o => o.Id == "q1");
        (await store.ListAsync(SavedObjectType.Form)).Should().ContainSingle(o => o.Id == "f1");
        (await store.ListAsync(null)).Should().HaveCount(2);

        // Rename = update at the current version; version increments.
        var renamed = await store.PutAsync(Obj(SavedObjectType.Query, "q1", "Regional sales", 1, """{"groupBy":["region"]}"""));
        renamed.Version.Should().Be(2);
        (await store.GetAsync(SavedObjectType.Query, "q1"))!.Name.Should().Be("Regional sales");

        // Delete.
        await store.DeleteAsync(SavedObjectType.Query, "q1");
        (await store.GetAsync(SavedObjectType.Query, "q1")).Should().BeNull();
        (await store.ListAsync(null)).Should().ContainSingle(o => o.Id == "f1");
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task StaleVersionWrite_IsRejected(string backend)
    {
        var store = Make(backend);
        await store.PutAsync(Obj(SavedObjectType.Query, "q1", "v1", 0));   // -> version 1
        await store.PutAsync(Obj(SavedObjectType.Query, "q1", "v2", 1));   // -> version 2

        // A writer still holding version 1 must be rejected — lost-update guard.
        var stale = () => store.PutAsync(Obj(SavedObjectType.Query, "q1", "conflicting", 1));
        (await stale.Should().ThrowAsync<SavedObjectVersionConflictException>())
            .Which.ActualVersion.Should().Be(2);

        // The rejected write left the stored object untouched.
        (await store.GetAsync(SavedObjectType.Query, "q1"))!.Name.Should().Be("v2");
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task CreateOverExisting_WithVersionZero_IsRejected(string backend)
    {
        var store = Make(backend);
        await store.PutAsync(Obj(SavedObjectType.Form, "f1", "first", 0));

        // A second create (version 0) for the same id is a conflict, not a silent overwrite.
        var recreate = () => store.PutAsync(Obj(SavedObjectType.Form, "f1", "second", 0));
        await recreate.Should().ThrowAsync<SavedObjectVersionConflictException>();
        (await store.GetAsync(SavedObjectType.Form, "f1"))!.Name.Should().Be("first");
    }
}
