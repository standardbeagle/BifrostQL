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
/// Integration tests for <see cref="FileDeleteResolver"/> against a real SQLite
/// database. Covers the two safety invariants added to the delete path:
/// (a) unparseable metadata fails fast rather than clearing the DB pointer, and
/// (b) a storage-delete failure leaves the DB pointer intact (delete happens
/// before the DB is cleared).
/// </summary>
public sealed class FileDeleteResolverTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _factory;

    public FileDeleteResolverTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-filedelete-{Guid.NewGuid():N}.db");
        _factory = new SqliteDbConnFactory($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    // The storage target is resolved from the column's configured bucket
    // (never from row-persisted BucketName/ProviderType), so the column must
    // carry a storage config naming the provider under test.
    private static IDbModel BuildModel(string provider = "local") => DbModelTestFixture.Create()
        .WithTable("widget", t => t
            .WithPrimaryKey("id")
            .WithColumn("photo", "text", isNullable: true)
            .WithColumnMetadata("photo", MetadataKeys.FileStorage.File, "true")
            .WithColumnMetadata("photo", MetadataKeys.Storage.Config, $"provider:{provider};bucket:bucket"))
        .Build();

    private async Task SeedAsync(string photoValue)
    {
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE widget (id INTEGER PRIMARY KEY, photo TEXT)", null, 30, 1000);
        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO widget (id, photo) VALUES (1, @p)",
            new Dictionary<string, object?> { ["p"] = photoValue }, 30, 1000);
    }

    private async Task<string?> ReadPhotoAsync()
    {
        var result = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT photo FROM widget WHERE id = 1", null, 30, 1000);
        return result.Rows[0][0] as string;
    }

    private FileResolverTestContext Context(IDbModel model) => new(_factory, model,
        new Dictionary<string, string?> { ["table"] = "widget", ["column"] = "photo", ["recordId"] = "1" });

    [Fact]
    public async Task CorruptMetadata_ThrowsAndPreservesDatabasePointer()
    {
        const string corrupt = "{not valid json";
        await SeedAsync(corrupt);
        var resolver = new FileDeleteResolver();

        var act = async () => await resolver.ResolveAsync(Context(BuildModel()));

        (await act.Should().ThrowAsync<BifrostExecutionError>())
            .Which.Message.Should().Contain("could not be parsed");
        // The corrupt pointer must survive: clearing it would orphan whatever it referenced.
        (await ReadPhotoAsync()).Should().Be(corrupt);
    }

    [Fact]
    public async Task StorageDeleteFails_DatabasePointerNotCleared()
    {
        var metadata = new FileMetadata
        {
            FileKey = "widget/photo/1_file.bin",
            ProviderType = "failing",
            BucketName = "bucket",
            Size = 10,
        }.ToJson();
        await SeedAsync(metadata);

        var factory = new StorageProviderFactory();
        factory.RegisterProvider(new FailingStorageProvider("failing"));
        var resolver = new FileDeleteResolver(new FileStorageService(factory));

        var act = async () => await resolver.ResolveAsync(Context(BuildModel("failing")));

        await act.Should().ThrowAsync<Exception>();
        // Storage delete threw before the UPDATE ran, so the pointer is intact.
        (await ReadPhotoAsync()).Should().Be(metadata);
    }

    [Fact]
    public async Task StorageDeleteSucceeds_DeletesFileThenClearsPointer()
    {
        var metadata = new FileMetadata
        {
            FileKey = "widget/photo/1_file.bin",
            ProviderType = "recording",
            BucketName = "bucket",
            Size = 10,
        }.ToJson();
        await SeedAsync(metadata);

        var recorder = new RecordingStorageProvider("recording");
        var factory = new StorageProviderFactory();
        factory.RegisterProvider(recorder);
        var resolver = new FileDeleteResolver(new FileStorageService(factory));

        var result = await resolver.ResolveAsync(Context(BuildModel("recording")));

        result.Should().Be(true);
        recorder.DeletedKeys.Should().ContainSingle().Which.Should().Be("widget/photo/1_file.bin");
        (await ReadPhotoAsync()).Should().BeNull();
    }
}
