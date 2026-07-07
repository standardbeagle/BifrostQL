using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Storage;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Verifies the fix for the CRITICAL findings that <see cref="FileDownloadResolver"/>,
/// <see cref="FileUploadResolver"/> and <see cref="FileDeleteResolver"/> built raw SQL
/// gated only by primary key, bypassing tenant-filter/soft-delete entirely, and that
/// <see cref="FileStorageService"/> trusted row-persisted bucket/provider values instead
/// of the column's configured storage target.
///
/// These tests drive the real resolvers end-to-end against a self-contained
/// shared-cache in-memory SQLite database, with <see cref="TenantFilterTransformer"/>/
/// <see cref="TenantMutationTransformer"/> wired into real transformer pipelines, and a
/// real <see cref="LocalStorageProvider"/> writing to a temp directory, asserting
/// against actual DB row state and actual files on disk.
/// </summary>
public sealed class FileResolverSecurityTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private string _bucketDir = null!;

    private static readonly string[] Rules =
    {
        "*.documents { tenant-filter: tenant_id; soft-delete: deleted_at }",
    };

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_file_security_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
        _connFactory = new SqliteDbConnFactory(_connectionString);

        await Exec(
            """
            CREATE TABLE documents (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                deleted_at TEXT NULL,
                file_data TEXT NULL
            )
            """);
        await Exec(
            """
            INSERT INTO documents(id, tenant_id, deleted_at, file_data) VALUES
                (1, 1, NULL, NULL),
                (2, 2, NULL, NULL)
            """);

        _bucketDir = Path.Combine(Path.GetTempPath(), "bifrost-file-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_bucketDir);
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
        if (Directory.Exists(_bucketDir))
            Directory.Delete(_bucketDir, recursive: true);
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<IDbModel> LoadModelAsync()
    {
        var loader = new DbModelLoader(_connFactory, new MetadataLoader(Rules));
        var model = await loader.LoadAsync();

        var column = model.GetTableFromDbName("documents").ColumnLookup["file_data"];
        column.Metadata[MetadataKeys.Storage.Config] = $"bucket:{_bucketDir};provider:local";

        return model;
    }

    private static IServiceProvider BuildServices(
        IEnumerable<IFilterTransformer>? filterTransformers = null,
        IEnumerable<IMutationTransformer>? mutationTransformers = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap
        {
            Transformers = (filterTransformers ?? Array.Empty<IFilterTransformer>()).ToArray(),
        });
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = (mutationTransformers ?? Array.Empty<IMutationTransformer>()).ToArray(),
        });
        return services.BuildServiceProvider();
    }

    private FakeFieldContext BuildContext(
        IDbModel model,
        IServiceProvider services,
        IDictionary<string, object?> userContext,
        IDictionary<string, object?> arguments)
    {
        var schema = DbSchema.FromModel(model);
        return new FakeFieldContext
        {
            Arguments = arguments,
            UserContext = userContext,
            RequestServices = services,
            InputExtensions = new Dictionary<string, object?>
            {
                ["connFactory"] = _connFactory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema),
            },
        };
    }

    private static IDictionary<string, object?> Tenant(int tenantId) =>
        new Dictionary<string, object?> { ["tenant_id"] = tenantId };

    private string? GetFileDataColumn(long id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = new SqliteCommand("SELECT file_data FROM documents WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        var result = cmd.ExecuteScalar();
        return result == null || result is DBNull ? null : (string)result;
    }

    private int CountFilesInBucket() =>
        Directory.Exists(_bucketDir) ? Directory.EnumerateFiles(_bucketDir, "*", SearchOption.AllDirectories).Count() : 0;

    #region Download: cross-tenant / soft-delete denial (finding #1)

    [Fact]
    public async Task Download_CrossTenantRow_ReturnsNull_NotOtherTenantsFile()
    {
        var model = await LoadModelAsync();
        await File.WriteAllTextAsync(Path.Combine(_bucketDir, "tenant2.txt"), "tenant-2-secret");
        var metadata = new FileMetadata { FileKey = "tenant2.txt", ContentType = "text/plain", Size = 15, BucketName = _bucketDir, ProviderType = "local" };
        await Exec($"UPDATE documents SET file_data = '{metadata.ToJson().Replace("'", "''")}' WHERE id = 2");

        var services = BuildServices(filterTransformers: new IFilterTransformer[] { new TenantFilterTransformer() });
        var resolver = new FileDownloadResolver(new FileStorageService());

        // Tenant 1 attempts to read tenant 2's row by primary key.
        var context = BuildContext(model, services, Tenant(1), new Dictionary<string, object?>
        {
            ["table"] = "documents",
            ["column"] = "file_data",
            ["recordId"] = "2",
        });

        var result = await resolver.ResolveAsync(context);

        result.Should().BeNull("the tenant filter must scope the row lookup, not just the primary key");
    }

    [Fact]
    public async Task Download_SameTenantRow_Succeeds()
    {
        var model = await LoadModelAsync();
        await File.WriteAllTextAsync(Path.Combine(_bucketDir, "tenant1.txt"), "tenant-1-file");
        var metadata = new FileMetadata { FileKey = "tenant1.txt", ContentType = "text/plain", Size = 13, BucketName = _bucketDir, ProviderType = "local" };
        await Exec($"UPDATE documents SET file_data = '{metadata.ToJson().Replace("'", "''")}' WHERE id = 1");

        var services = BuildServices(filterTransformers: new IFilterTransformer[] { new TenantFilterTransformer() });
        var resolver = new FileDownloadResolver(new FileStorageService());

        var context = BuildContext(model, services, Tenant(1), new Dictionary<string, object?>
        {
            ["table"] = "documents",
            ["column"] = "file_data",
            ["recordId"] = "1",
        });

        var result = (FileDownloadResult?)await resolver.ResolveAsync(context);

        result.Should().NotBeNull();
        result!.FileKey.Should().Be("tenant1.txt");
    }

    #endregion

    #region Download: stored BucketName/ProviderType is never trusted (finding #4)

    [Fact]
    public async Task DownloadFileAsync_IgnoresRowPersistedBucketName_UsesConfiguredBucket()
    {
        var model = await LoadModelAsync();
        var table = model.GetTableFromDbName("documents");
        var column = table.ColumnLookup["file_data"];

        // The real file lives under the column's *configured* bucket.
        await File.WriteAllTextAsync(Path.Combine(_bucketDir, "real.txt"), "real-content");

        // An attacker-controlled row value points BucketName at a directory the
        // column is not configured to use; a decoy file with the same key lives
        // there with different content.
        var attackerDir = Path.Combine(Path.GetTempPath(), "bifrost-file-security-attacker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attackerDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(attackerDir, "real.txt"), "attacker-content");

            var attackerMetadata = new FileMetadata
            {
                FileKey = "real.txt",
                BucketName = attackerDir,
                ProviderType = "local",
            };

            var service = new FileStorageService();
            var content = await service.DownloadFileAsync(table, column, model, attackerMetadata);

            System.Text.Encoding.UTF8.GetString(content).Should().Be("real-content",
                "the storage target must always come from the column's configuration, never from row-persisted metadata");
        }
        finally
        {
            Directory.Delete(attackerDir, recursive: true);
        }
    }

    #endregion

    #region Upload: row-writability verified before any storage write (findings #1, #2)

    [Fact]
    public async Task Upload_ToOtherTenantsRow_FailsWithoutOrphanBlob()
    {
        var model = await LoadModelAsync();
        var services = BuildServices(mutationTransformers: new IMutationTransformer[] { new TenantMutationTransformer() });
        var resolver = new FileUploadResolver(new FileStorageService());

        // Tenant 1 attempts to overwrite tenant 2's file column.
        var context = BuildContext(model, services, Tenant(1), new Dictionary<string, object?>
        {
            ["table"] = "documents",
            ["column"] = "file_data",
            ["recordId"] = "2",
            ["file"] = new byte[] { 1, 2, 3, 4 },
            ["filename"] = "evil.bin",
            ["contentType"] = "application/octet-stream",
        });

        var act = async () => await resolver.ResolveAsync(context);

        await act.Should().ThrowAsync<BifrostExecutionError>();
        CountFilesInBucket().Should().Be(0, "the row must be verified writable before any blob is uploaded");
        GetFileDataColumn(2).Should().BeNull();
    }

    [Fact]
    public async Task Upload_ToNonexistentRow_FailsWithoutOrphanBlob()
    {
        var model = await LoadModelAsync();
        var services = BuildServices(mutationTransformers: new IMutationTransformer[] { new TenantMutationTransformer() });
        var resolver = new FileUploadResolver(new FileStorageService());

        var context = BuildContext(model, services, Tenant(1), new Dictionary<string, object?>
        {
            ["table"] = "documents",
            ["column"] = "file_data",
            ["recordId"] = "999",
            ["file"] = new byte[] { 1, 2, 3, 4 },
            ["filename"] = "ghost.bin",
            ["contentType"] = "application/octet-stream",
        });

        var act = async () => await resolver.ResolveAsync(context);

        await act.Should().ThrowAsync<BifrostExecutionError>();
        CountFilesInBucket().Should().Be(0);
    }

    [Fact]
    public async Task Upload_ToOwnTenantsRow_Succeeds_AndPersistsMetadata()
    {
        var model = await LoadModelAsync();
        var services = BuildServices(mutationTransformers: new IMutationTransformer[] { new TenantMutationTransformer() });
        var resolver = new FileUploadResolver(new FileStorageService());

        var context = BuildContext(model, services, Tenant(1), new Dictionary<string, object?>
        {
            ["table"] = "documents",
            ["column"] = "file_data",
            ["recordId"] = "1",
            ["file"] = new byte[] { 1, 2, 3, 4 },
            ["filename"] = "mine.bin",
            ["contentType"] = "application/octet-stream",
        });

        var result = (FileUploadResult?)await resolver.ResolveAsync(context);

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        CountFilesInBucket().Should().Be(1);
        GetFileDataColumn(1).Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Delete: row-writability verified, column cleared before blob deleted (findings #1, #7)

    [Fact]
    public async Task Delete_OtherTenantsRow_DoesNotClearColumn_OrDeleteBlob()
    {
        var model = await LoadModelAsync();
        await File.WriteAllTextAsync(Path.Combine(_bucketDir, "tenant2.txt"), "tenant-2-file");
        var metadata = new FileMetadata { FileKey = "tenant2.txt", BucketName = _bucketDir, ProviderType = "local" };
        await Exec($"UPDATE documents SET file_data = '{metadata.ToJson().Replace("'", "''")}' WHERE id = 2");

        var services = BuildServices(mutationTransformers: new IMutationTransformer[] { new TenantMutationTransformer() });
        var resolver = new FileDeleteResolver(new FileStorageService());

        var context = BuildContext(model, services, Tenant(1), new Dictionary<string, object?>
        {
            ["table"] = "documents",
            ["column"] = "file_data",
            ["recordId"] = "2",
        });

        var act = async () => await resolver.ResolveAsync(context);

        await act.Should().ThrowAsync<BifrostExecutionError>();
        GetFileDataColumn(2).Should().NotBeNullOrEmpty("a denied delete must not clear another tenant's row");
        File.Exists(Path.Combine(_bucketDir, "tenant2.txt")).Should().BeTrue("a denied delete must not remove another tenant's file");
    }

    [Fact]
    public async Task Delete_OwnTenantsRow_ClearsColumn_AndDeletesBlob()
    {
        var model = await LoadModelAsync();
        await File.WriteAllTextAsync(Path.Combine(_bucketDir, "tenant1.txt"), "tenant-1-file");
        var metadata = new FileMetadata { FileKey = "tenant1.txt", BucketName = _bucketDir, ProviderType = "local" };
        await Exec($"UPDATE documents SET file_data = '{metadata.ToJson().Replace("'", "''")}' WHERE id = 1");

        var services = BuildServices(mutationTransformers: new IMutationTransformer[] { new TenantMutationTransformer() });
        var resolver = new FileDeleteResolver(new FileStorageService());

        var context = BuildContext(model, services, Tenant(1), new Dictionary<string, object?>
        {
            ["table"] = "documents",
            ["column"] = "file_data",
            ["recordId"] = "1",
        });

        var result = await resolver.ResolveAsync(context);

        result.Should().Be(true);
        GetFileDataColumn(1).Should().BeNull();
        File.Exists(Path.Combine(_bucketDir, "tenant1.txt")).Should().BeFalse();
    }

    #endregion

    #region FileStorageService: content-type fail-closed (finding #5) and per-column max (finding #6)

    [Fact]
    public async Task UploadFileAsync_AllowListConfigured_ContentTypeOmitted_Throws()
    {
        var column = new ColumnDto
        {
            ColumnName = "avatar",
            GraphQlName = "avatar",
            NormalizedName = "avatar",
            DataType = "nvarchar",
            Metadata = new Dictionary<string, object?> { ["storage"] = $"bucket:{_bucketDir};mimetypes:image/png" },
        };
        var table = new DbTable
        {
            DbName = "users",
            GraphQlName = "users",
            NormalizedName = "users",
            TableSchema = "main",
            TableType = "TABLE",
            ColumnLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase) { [column.ColumnName] = column },
            GraphQlLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase) { [column.GraphQlName] = column },
            Metadata = new Dictionary<string, object?>(),
        };
        var model = new DbModel { Tables = new[] { table }, Metadata = new Dictionary<string, object?>() };
        var service = new FileStorageService();

        var act = async () => await service.UploadFileAsync(table, column, model, "1", new byte[] { 1, 2, 3 }, contentType: null);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "an absent client content-type hint must not bypass a configured MIME allow-list");
    }

    [Fact]
    public async Task UploadFileAsync_PerColumnMaxSize_EnforcedEvenUnderBucketMax()
    {
        var column = new ColumnDto
        {
            ColumnName = "avatar",
            GraphQlName = "avatar",
            NormalizedName = "avatar",
            DataType = "nvarchar",
            Metadata = new Dictionary<string, object?>
            {
                ["storage"] = $"bucket:{_bucketDir};maxSize:1000000",
                ["file"] = "maxSize:5",
            },
        };
        var table = new DbTable
        {
            DbName = "users",
            GraphQlName = "users",
            NormalizedName = "users",
            TableSchema = "main",
            TableType = "TABLE",
            ColumnLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase) { [column.ColumnName] = column },
            GraphQlLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase) { [column.GraphQlName] = column },
            Metadata = new Dictionary<string, object?>(),
        };
        var model = new DbModel { Tables = new[] { table }, Metadata = new Dictionary<string, object?>() };
        var service = new FileStorageService();

        // 10 bytes: comfortably under the bucket's 1,000,000-byte cap but over
        // the column's tighter 5-byte cap.
        var act = async () => await service.UploadFileAsync(table, column, model, "1", new byte[10]);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "the more restrictive of the bucket-level and column-level max size must be enforced");
    }

    #endregion

    private sealed class FakeFieldContext : IBifrostFieldContext
    {
        public string FieldName => "_file";
        public string? FieldAlias => null;
        public object? Source => null;
        public IReadOnlyList<object> Path => Array.Empty<object>();
        public IDictionary<string, object?> UserContext { get; init; } = new Dictionary<string, object?>();
        public IServiceProvider? RequestServices { get; init; }
        public bool HasSubFields => false;
        public object Document => null!;
        public object Variables => null!;
        public IDictionary<string, object?> InputExtensions { get; init; } = new Dictionary<string, object?>();
        public CancellationToken CancellationToken => CancellationToken.None;
        public IDictionary<string, object?> Arguments { get; init; } = new Dictionary<string, object?>();

        public bool HasArgument(string name) => Arguments.ContainsKey(name);
        public T? GetArgument<T>(string name) => Arguments.TryGetValue(name, out var v) ? (T?)v : default;
    }
}
