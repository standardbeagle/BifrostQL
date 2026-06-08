using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Storage;

namespace BifrostQL.Core.Test.Storage;

public sealed class FileFolderComputedColumnProviderTests : IDisposable
{
    private readonly string _tempDir;

    public FileFolderComputedColumnProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void FileFolderMetadata_AddsComputedColumnDefinition()
    {
        var table = BuildModel().GetTableFromDbName("Pages");

        var computed = ComputedColumnConfigCollector.Find(table, "assets");

        Assert.NotNull(computed);
        Assert.Equal("JSON", computed.GraphQlType);
        Assert.Equal(FileFolderComputedColumnCollector.LocalProviderName, computed.ExpressionOrProvider);
        Assert.Contains("Id", computed.Dependencies);
        Assert.Contains("Title", computed.Dependencies);
    }

    [Fact]
    public async Task LocalProvider_ListsTemplatedFolder()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "assets", "42"));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "assets", "42", "hero.txt"), "hello");

        var model = BuildModel();
        var table = model.GetTableFromDbName("Pages");
        var computed = ComputedColumnConfigCollector.Find(table, "assets")!;
        var provider = new LocalFileFolderComputedColumnProvider();

        var result = await provider.ComputeAsync(new ComputedColumnContext
        {
            Model = model,
            Table = table,
            Column = computed,
            Row = new Dictionary<string, object?> { ["Id"] = 42 },
            UserContext = new Dictionary<string, object?>(),
        });

        var entries = Assert.IsAssignableFrom<IReadOnlyList<FileFolderEntry>>(result);
        Assert.Contains(entries, e => e.Name == "hero.txt" && e.Key == "assets/42/hero.txt");
    }

    private IDbModel BuildModel()
        => DbModelTestFixture.Create()
            .WithTable("Pages", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Title", "varchar")
                .WithMetadata(MetadataKeys.FileStorage.Folder,
                    $"assets:JSON:local:folder=assets/{{Id}},depends=Id,Title,bucket={_tempDir}"))
            .Build();
}
