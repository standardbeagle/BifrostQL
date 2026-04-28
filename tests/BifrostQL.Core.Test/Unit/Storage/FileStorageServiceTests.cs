using BifrostQL.Core.Model;
using BifrostQL.Core.Storage;

namespace BifrostQL.Core.Test.Storage;

public class FileStorageServiceTests
{
    private static ColumnDto CreateColumn(string name, string dataType, Dictionary<string, object?>? metadata = null)
    {
        return new ColumnDto
        {
            ColumnName = name,
            GraphQlName = name.ToLowerInvariant(),
            NormalizedName = name,
            DataType = dataType,
            Metadata = metadata ?? new Dictionary<string, object?>()
        };
    }

    private static DbTable CreateTable(string name, params ColumnDto[] columns)
    {
        var columnList = columns.ToList();
        return new DbTable
        {
            DbName = name,
            GraphQlName = name.ToLowerInvariant(),
            NormalizedName = name,
            TableSchema = "dbo",
            TableType = "TABLE",
            ColumnLookup = columnList.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase),
            GraphQlLookup = columnList.ToDictionary(c => c.GraphQlName, StringComparer.OrdinalIgnoreCase),
            Metadata = new Dictionary<string, object?>()
        };
    }

    #region IsFileStorageColumn

    [Fact]
    public void IsFileStorageColumn_WithFileMetadata_ReturnsTrue()
    {
        var service = new FileStorageService();
        var column = CreateColumn("avatar", "nvarchar", new Dictionary<string, object?>
        {
            ["file"] = "type:image"
        });
        var table = CreateTable("users", column);
        var model = new DbModel { Tables = new[] { table }, Metadata = new Dictionary<string, object?>() };

        var result = service.IsFileStorageColumn(table, column, model);

        Assert.True(result);
    }

    [Fact]
    public void IsFileStorageColumn_WithStorageMetadata_ReturnsTrue()
    {
        var service = new FileStorageService();
        var column = CreateColumn("document", "nvarchar", new Dictionary<string, object?>
        {
            ["storage"] = "bucket:files"
        });
        var table = CreateTable("users", column);
        var model = new DbModel { Tables = new[] { table }, Metadata = new Dictionary<string, object?>() };

        var result = service.IsFileStorageColumn(table, column, model);

        Assert.True(result);
    }

    [Fact]
    public void IsFileStorageColumn_WithBinaryType_ReturnsFalse()
    {
        var service = new FileStorageService();
        var column = CreateColumn("data", "varbinary");
        var table = CreateTable("users", column);
        var model = new DbModel { Tables = new[] { table }, Metadata = new Dictionary<string, object?>() };

        var result = service.IsFileStorageColumn(table, column, model);

        Assert.False(result);
    }

    [Fact]
    public void IsFileStorageColumn_WithRegularColumn_ReturnsFalse()
    {
        var service = new FileStorageService();
        var column = CreateColumn("name", "nvarchar");
        var table = CreateTable("users", column);
        var model = new DbModel { Tables = new[] { table }, Metadata = new Dictionary<string, object?>() };

        var result = service.IsFileStorageColumn(table, column, model);

        Assert.False(result);
    }

    #endregion

    #region GetBucketConfig

    [Fact]
    public void GetBucketConfig_WithColumnConfig_ReturnsColumnConfig()
    {
        var service = new FileStorageService();
        var column = CreateColumn("avatar", "nvarchar", new Dictionary<string, object?>
        {
            ["storage"] = "bucket:column-bucket"
        });
        var table = CreateTable("users", column);
        table.Metadata["storage"] = "bucket:table-bucket";
        var model = new DbModel 
        { 
            Tables = new[] { table }, 
            Metadata = new Dictionary<string, object?> { ["storage"] = "bucket:db-bucket" }
        };

        var config = service.GetBucketConfig(table, column, model);

        Assert.NotNull(config);
        Assert.Equal("column-bucket", config.BucketName);
    }

    [Fact]
    public void GetBucketConfig_WithTableConfig_ReturnsTableConfig()
    {
        var service = new FileStorageService();
        var column = CreateColumn("avatar", "nvarchar");
        var table = CreateTable("users", column);
        table.Metadata["storage"] = "bucket:table-bucket";
        var model = new DbModel 
        { 
            Tables = new[] { table }, 
            Metadata = new Dictionary<string, object?> { ["storage"] = "bucket:db-bucket" }
        };

        var config = service.GetBucketConfig(table, column, model);

        Assert.NotNull(config);
        Assert.Equal("table-bucket", config.BucketName);
    }

    [Fact]
    public void GetBucketConfig_WithDatabaseDefault_ReturnsDefaultConfig()
    {
        var dbConfig = new StorageBucketConfig { BucketName = "default-bucket", ProviderType = "local" };
        var service = new FileStorageService(databaseDefaultConfig: dbConfig);
        var column = CreateColumn("avatar", "nvarchar");
        var table = CreateTable("users", column);
        var model = new DbModel { Tables = new[] { table }, Metadata = new Dictionary<string, object?>() };

        var config = service.GetBucketConfig(table, column, model);

        Assert.NotNull(config);
        Assert.Equal("default-bucket", config.BucketName);
    }

    [Fact]
    public void GetBucketConfig_WithNoConfig_ReturnsNull()
    {
        var service = new FileStorageService();
        var column = CreateColumn("avatar", "nvarchar");
        var table = CreateTable("users", column);
        var model = new DbModel { Tables = new[] { table }, Metadata = new Dictionary<string, object?>() };

        var config = service.GetBucketConfig(table, column, model);

        Assert.Null(config);
    }

    #endregion

    #region GetFileColumnConfig

    [Fact]
    public void GetFileColumnConfig_WithValidMetadata_ReturnsConfig()
    {
        var service = new FileStorageService();
        var column = CreateColumn("avatar", "nvarchar", new Dictionary<string, object?>
        {
            ["file"] = "type:image;maxSize:5242880"
        });

        var config = service.GetFileColumnConfig(column);

        Assert.NotNull(config);
        Assert.Equal("image", config.FileType);
        Assert.Equal(5242880, config.MaxFileSize);
    }

    [Fact]
    public void GetFileColumnConfig_WithNoMetadata_ReturnsNull()
    {
        var service = new FileStorageService();
        var column = CreateColumn("avatar", "nvarchar");

        var config = service.GetFileColumnConfig(column);

        Assert.Null(config);
    }

    #endregion

    #region UploadFileAsync

    [Fact]
    public async Task UploadFileAsync_WithNoConfig_ThrowsInvalidOperationException()
    {
        var service = new FileStorageService();
        var column = CreateColumn("avatar", "nvarchar");
        var table = CreateTable("users", column);
        var model = new DbModel { Tables = new[] { table }, Metadata = new Dictionary<string, object?>() };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadFileAsync(table, column, model, "123", new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task UploadFileAsync_WithFileExceedingMaxSize_ThrowsInvalidOperationException()
    {
        var service = new FileStorageService();
        var column = CreateColumn("avatar", "nvarchar", new Dictionary<string, object?>
        {
            ["storage"] = "bucket:test;maxSize:10"
        });
        var table = CreateTable("users", column);
        var model = new DbModel { Tables = new[] { table }, Metadata = new Dictionary<string, object?>() };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadFileAsync(table, column, model, "123", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }));
    }

    [Fact]
    public async Task UploadFileAsync_WithInvalidMimeType_ThrowsInvalidOperationException()
    {
        var service = new FileStorageService();
        var column = CreateColumn("avatar", "nvarchar", new Dictionary<string, object?>
        {
            ["storage"] = "bucket:test;mimetypes:image/png"
        });
        var table = CreateTable("users", column);
        var model = new DbModel { Tables = new[] { table }, Metadata = new Dictionary<string, object?>() };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadFileAsync(table, column, model, "123", new byte[] { 1, 2, 3 }, contentType: "application/pdf"));
    }

    #endregion
}
