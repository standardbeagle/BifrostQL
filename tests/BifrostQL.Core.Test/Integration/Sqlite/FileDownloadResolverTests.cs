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
/// Integration tests for <see cref="FileDownloadResolver"/> against a real SQLite
/// database. Pins the corrupt-metadata behavior so the download path fails fast
/// (mirroring the delete path) instead of silently returning null and masking a
/// corrupt pointer as "no file stored".
/// </summary>
public sealed class FileDownloadResolverTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _factory;

    public FileDownloadResolverTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-filedownload-{Guid.NewGuid():N}.db");
        _factory = new SqliteDbConnFactory($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static IDbModel BuildModel() => DbModelTestFixture.Create()
        .WithTable("widget", t => t
            .WithPrimaryKey("id")
            .WithColumn("photo", "text", isNullable: true)
            .WithColumnMetadata("photo", MetadataKeys.FileStorage.File, "true"))
        .Build();

    private async Task SeedAsync(string? photoValue)
    {
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE widget (id INTEGER PRIMARY KEY, photo TEXT)", null, 30, 1000);
        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO widget (id, photo) VALUES (1, @p)",
            new Dictionary<string, object?> { ["p"] = (object?)photoValue ?? DBNull.Value }, 30, 1000);
    }

    private FileResolverTestContext Context(IDbModel model) => new(_factory, model,
        new Dictionary<string, string?> { ["table"] = "widget", ["column"] = "photo", ["recordId"] = "1" });

    [Fact]
    public async Task CorruptMetadata_ThrowsRatherThanReturningNull()
    {
        await SeedAsync("{not valid json");
        var resolver = new FileDownloadResolver();

        var act = async () => await resolver.ResolveAsync(Context(BuildModel()));

        (await act.Should().ThrowAsync<BifrostExecutionError>())
            .Which.Message.Should().Contain("could not be parsed");
    }

    [Fact]
    public async Task NoFileStored_ReturnsNull()
    {
        await SeedAsync(null);
        var resolver = new FileDownloadResolver();

        var result = await resolver.ResolveAsync(Context(BuildModel()));

        result.Should().BeNull();
    }
}
