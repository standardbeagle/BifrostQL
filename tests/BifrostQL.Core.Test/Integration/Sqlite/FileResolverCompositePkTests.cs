using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Storage;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Pins the composite-primary-key contract of the file resolvers
/// (<see cref="FileDownloadResolver"/> / <see cref="FileDeleteResolver"/>).
///
/// The <c>recordId: String!</c> argument carries a composite key as its
/// components joined with '-' (the same rendering
/// <see cref="FileObjectSeam"/> uses: <c>string.Join("-", primaryKey)</c>).
/// Each key column MUST bind its OWN decoded value in declared key order —
/// never the whole scalar broadcast across every key column, which produced
/// the mis-scoped <c>id = X AND tenant_id = X</c> predicate.
///
/// The decoy row is addressed exactly by that broadcast: with TEXT keys and
/// <c>recordId = "5-9"</c>, the buggy predicate is <c>id = '5-9' AND
/// tenant_id = '5-9'</c>, which matches the DECOY row, not the intended
/// (id='5', tenant_id='9') TARGET row — a real cross-row mis-resolution, not
/// merely a parse failure.
/// </summary>
public sealed class FileResolverCompositePkTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _factory;

    public FileResolverCompositePkTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-filecompositepk-{Guid.NewGuid():N}.db");
        _factory = new SqliteDbConnFactory($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    // Composite TEXT primary key (id, tenant_id) with a file-bearing column.
    private static IDbModel BuildCompositeModel() => DbModelTestFixture.Create()
        .WithTable("doc", t => t
            .WithPrimaryKey("id", "text")
            .WithColumn("tenant_id", "text", isPrimaryKey: true)
            .WithColumn("attachment", "text", isNullable: true)
            .WithColumnMetadata("attachment", MetadataKeys.FileStorage.File, "true")
            .WithColumnMetadata("attachment", MetadataKeys.Storage.Config, "provider:recording;bucket:bucket"))
        .Build();

    // Single TEXT primary key. A key value that itself contains '-' must NOT be
    // split: the whole recordId is the one key column's value.
    private static IDbModel BuildSingleKeyModel() => DbModelTestFixture.Create()
        .WithTable("note", t => t
            .WithPrimaryKey("id", "text")
            .WithColumn("attachment", "text", isNullable: true)
            .WithColumnMetadata("attachment", MetadataKeys.FileStorage.File, "true")
            .WithColumnMetadata("attachment", MetadataKeys.Storage.Config, "provider:recording;bucket:bucket"))
        .Build();

    private static string Meta(string fileKey) => new FileMetadata
    {
        FileKey = fileKey,
        ProviderType = "recording",
        BucketName = "bucket",
        Size = 10,
    }.ToJson();

    private FileResolverTestContext Context(IDbModel model, string table, string recordId) => new(_factory, model,
        new Dictionary<string, string?> { ["table"] = table, ["column"] = "attachment", ["recordId"] = recordId });

    private async Task SeedCompositeAsync()
    {
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE doc (id TEXT, tenant_id TEXT, attachment TEXT, PRIMARY KEY (id, tenant_id))", null, 30, 1000);
        // TARGET: addressed by recordId "5-9" -> id='5', tenant_id='9'.
        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO doc (id, tenant_id, attachment) VALUES ('5', '9', @p)",
            new Dictionary<string, object?> { ["p"] = Meta("target.bin") }, 30, 1000);
        // DECOY: the row the broadcast bug (id='5-9' AND tenant_id='5-9') hits.
        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO doc (id, tenant_id, attachment) VALUES ('5-9', '5-9', @p)",
            new Dictionary<string, object?> { ["p"] = Meta("decoy.bin") }, 30, 1000);
    }

    private async Task<string?> ReadDocAsync(string id, string tenantId)
    {
        var result = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT attachment FROM doc WHERE id = @id AND tenant_id = @t",
            new Dictionary<string, object?> { ["id"] = id, ["t"] = tenantId }, 30, 1000);
        return result.Rows.Count == 0 ? null : result.Rows[0][0] as string;
    }

    private static (FileDeleteResolver Resolver, RecordingStorageProvider Recorder) DeleteResolver()
    {
        var recorder = new RecordingStorageProvider("recording");
        var factory = new StorageProviderFactory();
        factory.RegisterProvider(recorder);
        return (new FileDeleteResolver(new FileStorageService(factory)), recorder);
    }

    private static (FileDownloadResolver Resolver, RecordingStorageProvider Recorder) DownloadResolver()
    {
        var recorder = new RecordingStorageProvider("recording");
        var factory = new StorageProviderFactory();
        factory.RegisterProvider(recorder);
        return (new FileDownloadResolver(new FileStorageService(factory)), recorder);
    }

    [Fact]
    public async Task Delete_CompositePk_AddressesTargetRow_NotCoincidentBroadcastRow()
    {
        await SeedCompositeAsync();
        var (resolver, recorder) = DeleteResolver();

        var result = await resolver.ResolveAsync(Context(BuildCompositeModel(), "doc", "5-9"));

        result.Should().Be(true);
        // Only the TARGET row's file was deleted.
        recorder.DeletedKeys.Should().ContainSingle().Which.Should().Be("target.bin");
        // TARGET (5,9) pointer cleared; DECOY (5-9,5-9) untouched.
        (await ReadDocAsync("5", "9")).Should().BeNull();
        (await ReadDocAsync("5-9", "5-9")).Should().Be(Meta("decoy.bin"));
    }

    [Fact]
    public async Task Download_CompositePk_AddressesTargetRow_NotCoincidentBroadcastRow()
    {
        await SeedCompositeAsync();
        var (resolver, _) = DownloadResolver();

        var result = await resolver.ResolveAsync(Context(BuildCompositeModel(), "doc", "5-9"));

        var download = result.Should().BeOfType<FileDownloadResult>().Subject;
        download.FileKey.Should().Be("target.bin");
    }

    [Fact]
    public async Task Delete_CompositePk_WrongArityRecordId_FailsFast()
    {
        await SeedCompositeAsync();
        var (resolver, _) = DeleteResolver();

        // A bare scalar cannot address a 2-column key: it must not silently
        // broadcast to id = '5' AND tenant_id = '5'.
        var act = async () => await resolver.ResolveAsync(Context(BuildCompositeModel(), "doc", "5"));

        await act.Should().ThrowAsync<BifrostExecutionError>();
    }

    [Fact]
    public async Task Delete_SingleKey_HyphenatedRecordId_IsNotSplit()
    {
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE note (id TEXT PRIMARY KEY, attachment TEXT)", null, 30, 1000);
        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO note (id, attachment) VALUES ('a-b', @p)",
            new Dictionary<string, object?> { ["p"] = Meta("single.bin") }, 30, 1000);
        var (resolver, recorder) = DeleteResolver();

        var result = await resolver.ResolveAsync(Context(BuildSingleKeyModel(), "note", "a-b"));

        result.Should().Be(true);
        recorder.DeletedKeys.Should().ContainSingle().Which.Should().Be("single.bin");
        var remaining = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT attachment FROM note WHERE id = 'a-b'", null, 30, 1000);
        (remaining.Rows[0][0] as string).Should().BeNull();
    }
}
