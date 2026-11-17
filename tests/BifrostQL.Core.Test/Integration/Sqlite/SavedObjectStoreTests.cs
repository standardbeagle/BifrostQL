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
/// save/load/rename/delete identically, enforce the same optimistic-concurrency
/// rule, and isolate one owner's objects from another's identically. Runs every test
/// against both backends via a member-data store factory.
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

    private ISavedObjectStore DbStore() => DbStore(DbSavedObjectStore.DefaultMaxListRows, out _);

    private DbSavedObjectStore DbStore(int maxListRows, out SqliteConnection keepAlive)
    {
        var conn = $"Data Source=bifrost_so_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        keepAlive = new SqliteConnection(conn);
        keepAlive.Open();
        _keepAlive.Add(keepAlive);
        return new DbSavedObjectStore(new SqliteDbConnFactory(conn), maxListRows: maxListRows);
    }

    public static IEnumerable<object[]> Backends => new[]
    {
        new object[] { "file" },
        new object[] { "db" },
    };

    private ISavedObjectStore Make(string backend) => backend == "file" ? FileStore() : DbStore();

    // Two owner tokens of the shape SavedObjectOwner produces, standing in for two callers with
    // distinct projected identities. Every single-owner test below would pass against a store that
    // ignored the owner entirely, so isolation needs a SECOND owner to be evidence of anything.
    private static readonly string Alice = SavedObjectOwner.FromUserContext(
        new Dictionary<string, object?> { ["user_id"] = "alice" })!;
    private static readonly string Bob = SavedObjectOwner.FromUserContext(
        new Dictionary<string, object?> { ["user_id"] = "bob" })!;

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
    public async Task PerOwnerCap_RefusesCreates_NotUpdates_AndIsPerOwner(string backend)
    {
        ISavedObjectStore store;
        if (backend == "file")
        {
            var dir = Path.Combine(Path.GetTempPath(), $"bifrost-so-{Guid.NewGuid():N}");
            _tempDirs.Add(dir);
            store = new FileSavedObjectStore(dir, maxObjectsPerOwner: 2);
        }
        else
        {
            var conn = $"Data Source=bifrost_so_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var keep = new SqliteConnection(conn);
            keep.Open();
            _keepAlive.Add(keep);
            store = new DbSavedObjectStore(new SqliteDbConnFactory(conn), maxObjectsPerOwner: 2);
        }

        await store.PutAsync(Alice, Obj(SavedObjectType.Query, "a", "a", 0));
        await store.PutAsync(Alice, Obj(SavedObjectType.Form, "b", "b", 0));

        var third = () => store.PutAsync(Alice, Obj(SavedObjectType.Query, "c", "c", 0));
        await third.Should().ThrowAsync<SavedObjectQuotaExceededException>();

        (await store.PutAsync(Alice, Obj(SavedObjectType.Query, "a", "renamed", 1))).Version.Should().Be(2,
            "an update adds no object");
        (await store.PutAsync(Bob, Obj(SavedObjectType.Query, "c", "c", 0))).Version.Should().Be(1,
            "the cap is per owner: Alice's objects do not count against Bob");
    }

    [Fact]
    public void Validate_RefusesAnUndefinedType()
    {
        var undefined = Obj((SavedObjectType)999, "x", "x", 0);
        var act = () => SavedObjectJson.Validate(undefined);
        act.Should().Throw<SavedObjectValidationException>();
        var parse = () => SavedObjectJson.ParseType("999");
        parse.Should().Throw<SavedObjectValidationException>();
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task SaveLoadRenameDelete_RoundTrips(string backend)
    {
        var store = Make(backend);

        // Create a query and a form; a create carries version 0 and lands as version 1.
        var query = await store.PutAsync(Alice, Obj(SavedObjectType.Query, "q1", "Sales by region", 0, """{"groupBy":["region"]}"""));
        var form = await store.PutAsync(Alice, Obj(SavedObjectType.Form, "f1", "Customer form", 0, """{"table":"customers"}"""));
        query.Version.Should().Be(1);
        form.Version.Should().Be(1);

        // Load back.
        var loaded = await store.GetAsync(Alice, SavedObjectType.Query, "q1");
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Sales by region");
        loaded.Definition.GetProperty("groupBy")[0].GetString().Should().Be("region");

        // List filters by type.
        (await store.ListAsync(Alice, SavedObjectType.Query)).Should().ContainSingle(o => o.Id == "q1");
        (await store.ListAsync(Alice, SavedObjectType.Form)).Should().ContainSingle(o => o.Id == "f1");
        (await store.ListAsync(Alice, null)).Should().HaveCount(2);

        // Rename = update at the current version; version increments.
        var renamed = await store.PutAsync(Alice, Obj(SavedObjectType.Query, "q1", "Regional sales", 1, """{"groupBy":["region"]}"""));
        renamed.Version.Should().Be(2);
        (await store.GetAsync(Alice, SavedObjectType.Query, "q1"))!.Name.Should().Be("Regional sales");

        // Delete.
        await store.DeleteAsync(Alice, SavedObjectType.Query, "q1");
        (await store.GetAsync(Alice, SavedObjectType.Query, "q1")).Should().BeNull();
        (await store.ListAsync(Alice, null)).Should().ContainSingle(o => o.Id == "f1");
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task StaleVersionWrite_IsRejected(string backend)
    {
        var store = Make(backend);
        await store.PutAsync(Alice, Obj(SavedObjectType.Query, "q1", "v1", 0));   // -> version 1
        await store.PutAsync(Alice, Obj(SavedObjectType.Query, "q1", "v2", 1));   // -> version 2

        // A writer still holding version 1 must be rejected — lost-update guard.
        var stale = () => store.PutAsync(Alice, Obj(SavedObjectType.Query, "q1", "conflicting", 1));
        (await stale.Should().ThrowAsync<SavedObjectVersionConflictException>())
            .Which.ActualVersion.Should().Be(2);

        // The rejected write left the stored object untouched.
        (await store.GetAsync(Alice, SavedObjectType.Query, "q1"))!.Name.Should().Be("v2");
    }

    [Fact]
    public async Task DbStore_ConcurrentCreates_ExactlyOneWins_OthersConflict()
    {
        var store = DbStore();

        // Fire several creates for the same (type, id) at once. The pre-insert
        // existence check is check-then-act, so losers hit the primary key — they must
        // surface as a version conflict (409), never a raw provider exception (500).
        var attempts = Enumerable.Range(0, 8).Select(i =>
            Task.Run(async () =>
            {
                try
                {
                    await store.PutAsync(Alice, Obj(SavedObjectType.Query, "race", $"attempt-{i}", 0));
                    return (Succeeded: true, Conflicted: false);
                }
                catch (SavedObjectVersionConflictException)
                {
                    return (Succeeded: false, Conflicted: true);
                }
            })).ToArray();

        var results = await Task.WhenAll(attempts);

        results.Count(r => r.Succeeded).Should().Be(1, "exactly one create can win the race");
        results.Count(r => r.Conflicted).Should().Be(results.Length - 1, "every loser gets a clean version conflict");
        (await store.GetAsync(Alice, SavedObjectType.Query, "race"))!.Version.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task CreateOverExisting_WithVersionZero_IsRejected(string backend)
    {
        var store = Make(backend);
        await store.PutAsync(Alice, Obj(SavedObjectType.Form, "f1", "first", 0));

        // A second create (version 0) for the same id is a conflict, not a silent overwrite.
        var recreate = () => store.PutAsync(Alice, Obj(SavedObjectType.Form, "f1", "second", 0));
        await recreate.Should().ThrowAsync<SavedObjectVersionConflictException>();
        (await store.GetAsync(Alice, SavedObjectType.Form, "f1"))!.Name.Should().Be("first");
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task OneOwnersObjects_AreInvisibleToAnother(string backend)
    {
        var store = Make(backend);
        await store.PutAsync(Alice, Obj(SavedObjectType.Query, "q1", "alice-original", 0));

        // Bob's reads see nothing: not in either list shape, and absent by id -- the SAME answer
        // he gets for an id nobody holds, so a read is never an existence oracle.
        (await store.ListAsync(Bob, SavedObjectType.Query)).Should().BeEmpty();
        (await store.ListAsync(Bob, null)).Should().BeEmpty();
        (await store.GetAsync(Bob, SavedObjectType.Query, "q1")).Should().BeNull();
        (await store.GetAsync(Bob, SavedObjectType.Query, "never-existed")).Should().BeNull();

        // ...and alice still has hers, so this cannot pass on a store that lost the object.
        (await store.ListAsync(Alice, SavedObjectType.Query)).Should().ContainSingle(o => o.Id == "q1");
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task WritingAnotherOwnersId_CreatesOwnObject_AndLeavesTheirsIntact(string backend)
    {
        var store = Make(backend);
        await store.PutAsync(Alice, Obj(SavedObjectType.Query, "q1", "alice-original", 0));

        // Version 0 on an id ALICE holds. In bob's partition nothing is there, so it is a create:
        // a conflict here would leak that the id is taken, and an update would clobber her object.
        var bobs = await store.PutAsync(Bob, Obj(SavedObjectType.Query, "q1", "bob-clobber", 0));

        bobs.Version.Should().Be(1);
        (await store.GetAsync(Alice, SavedObjectType.Query, "q1"))!.Name.Should().Be("alice-original");
        (await store.GetAsync(Bob, SavedObjectType.Query, "q1"))!.Name.Should().Be("bob-clobber");
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task DeletingAnotherOwnersId_IsANoOp(string backend)
    {
        var store = Make(backend);
        await store.PutAsync(Alice, Obj(SavedObjectType.Query, "q1", "alice-original", 0));

        await store.DeleteAsync(Bob, SavedObjectType.Query, "q1");

        (await store.GetAsync(Alice, SavedObjectType.Query, "q1"))!.Name.Should().Be("alice-original");
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task VersionsAreTrackedPerOwner_NotGlobally(string backend)
    {
        var store = Make(backend);
        await store.PutAsync(Alice, Obj(SavedObjectType.Query, "q1", "a1", 0));
        await store.PutAsync(Alice, Obj(SavedObjectType.Query, "q1", "a2", 1));   // alice at version 2

        // Bob's first write of the same id is still a create at version 0. A shared version counter
        // would reject it -- and would tell bob exactly how many times alice has edited hers.
        var bobs = await store.PutAsync(Bob, Obj(SavedObjectType.Query, "q1", "b1", 0));

        bobs.Version.Should().Be(1);
        (await store.GetAsync(Alice, SavedObjectType.Query, "q1"))!.Version.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AnOwnerTokenTheStoreDidNotIssue_IsRefused(string backend)
    {
        var store = Make(backend);

        // Owners are VALIDATED, never sanitized into shape. A traversal-flavoured token must be
        // rejected outright: rewriting it to something safe is how two distinct owners end up
        // sharing one partition.
        var write = () => store.PutAsync("../../etc", Obj(SavedObjectType.Query, "q1", "x", 0));
        await write.Should().ThrowAsync<ArgumentException>();

        var read = () => store.ListAsync("", null);
        await read.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void OwnerTokens_AreDistinctPerIdentity_AndPerTenant()
    {
        // The partition function must be injective, or isolation is decorative.
        string Owner(string user, string? tenant = null)
        {
            var ctx = new Dictionary<string, object?> { ["user_id"] = user };
            if (tenant != null) ctx["tenant_id"] = tenant;
            return SavedObjectOwner.FromUserContext(ctx)!;
        }

        Owner("alice").Should().NotBe(Owner("bob"));
        Owner("alice", "t1").Should().NotBe(Owner("alice", "t2"));
        Owner("alice", "t1").Should().NotBe(Owner("alice"));
        Owner("alice").Should().Be(Owner("alice"), "the same identity must reach the same partition");

        // A composition that concatenated its parts would collide these two.
        Owner("ab", "c").Should().NotBe(Owner("a", "bc"));

        // The anonymous partition can never be produced by an identity.
        Owner("alice").Should().NotBe(SavedObjectOwner.Anonymous);

        // No identity to partition by is a REFUSAL, never a fallback to a shared owner.
        SavedObjectOwner.FromUserContext(new Dictionary<string, object?>()).Should().BeNull();
        SavedObjectOwner.FromUserContext(
            new Dictionary<string, object?> { ["user_id"] = "  " }).Should().BeNull();
        SavedObjectOwner.FromUserContext(null).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Put_IdOverTheKeyCap_IsRejected(string backend)
    {
        var store = Make(backend);
        var atCap = new string('q', SavedObjectJson.MaxIdLength);

        // The cap itself is allowed; one character more is refused on both backends.
        await store.PutAsync(Alice, Obj(SavedObjectType.Query, atCap, "at cap", 0));
        var act = () => store.PutAsync(Alice, Obj(SavedObjectType.Query, atCap + "q", "over cap", 0));

        await act.Should().ThrowAsync<SavedObjectValidationException>().WithMessage("*'id'*128*");
    }

    [Fact]
    public async Task DbStore_KeyColumns_AreBoundedText()
    {
        // SQL Server refuses NVARCHAR(MAX) and MySQL refuses TEXT as a key column, so the
        // provisioned key columns must use the bounded kind on every dialect.
        var store = DbStore(DbSavedObjectStore.DefaultMaxListRows, out var conn);
        await store.PutAsync(Alice, Obj(SavedObjectType.Query, "q1", "one", 0));

        var types = new Dictionary<string, string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({DbSavedObjectStore.DefaultTableName})";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                types[reader.GetString(1)] = reader.GetString(2);
        }

        types.Should().Contain("owner", "VARCHAR(128)")
            .And.Contain("type", "VARCHAR(128)")
            .And.Contain("id", "VARCHAR(128)")
            .And.Contain("definition", "TEXT");
    }

    [Fact]
    public void KeyText_RendersBoundedOnEveryDialect()
    {
        BifrostQL.SqlServer.SqlServerDialect.Instance.RenderColumnType(BifrostQL.Core.QueryModel.SqlColumnKind.KeyText)
            .Should().Be("NVARCHAR(128)");
        BifrostQL.MySql.MySqlDialect.Instance.RenderColumnType(BifrostQL.Core.QueryModel.SqlColumnKind.KeyText)
            .Should().Be("VARCHAR(128)");
        BifrostQL.Ngsql.PostgresDialect.Instance.RenderColumnType(BifrostQL.Core.QueryModel.SqlColumnKind.KeyText)
            .Should().Be("VARCHAR(128)");
    }

    [Fact]
    public async Task DbStore_ListOverTheCap_IsRefusedNotCutShort()
    {
        var store = DbStore(maxListRows: 2, out _);
        await store.PutAsync(Alice, Obj(SavedObjectType.Query, "q1", "one", 0));
        await store.PutAsync(Alice, Obj(SavedObjectType.Query, "q2", "two", 0));

        // Exactly at the cap: the full list.
        (await store.ListAsync(Alice, null)).Select(o => o.Id).Should().BeEquivalentTo("q1", "q2");

        await store.PutAsync(Alice, Obj(SavedObjectType.Query, "q3", "three", 0));
        var act = () => store.ListAsync(Alice, null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*more than 2*");
    }
}
