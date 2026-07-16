using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Storage;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
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

    #region FileObjectSeam: identity-gated file objects (S3 slice 1)

    private const string EndpointPath = "/graphql";

    /// <summary>
    /// Wires the seam over the real intent executors, exactly as a protocol
    /// adapter would get them from DI. <paramref name="storageProvider"/> replaces
    /// the built-in "local" provider so a test can prove whether storage was
    /// touched at all.
    /// </summary>
    private async Task<(FileObjectSeam Seam, PathCache<Inputs> Cache)> BuildSeamAsync(
        bool enableWrites = true,
        IStorageProvider? storageProvider = null,
        IMutationTransformer[]? extraMutationTransformers = null)
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(_connectionString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
            model.GetTableFromDbName("documents").ColumnLookup["file_data"]
                .Metadata[MetadataKeys.Storage.Config] = $"bucket:{_bucketDir};provider:local";
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["dbSchema"] = DbSchema.FromModel(model),
                ["connFactory"] = factory,
            });
        });

        var reads = new QueryIntentExecutor(
            pathCache,
            new QueryTransformerService(new FilterTransformersWrap
            {
                Transformers = new IFilterTransformer[] { new TenantFilterTransformer() },
            }));

        var writes = new MutationIntentExecutor(
            pathCache,
            new MutationTransformersWrap
            {
                Transformers = new IMutationTransformer[] { new TenantMutationTransformer() }
                    .Concat(extraMutationTransformers ?? Array.Empty<IMutationTransformer>()).ToArray(),
            });

        var providerFactory = new StorageProviderFactory();
        if (storageProvider != null)
            providerFactory.RegisterProvider(storageProvider);

        var seam = new FileObjectSeam(
            reads, writes,
            new FileStorageService(providerFactory),
            new FileObjectSeamOptions { Endpoint = EndpointPath, EnableWrites = enableWrites });

        await Task.CompletedTask;
        return (seam, pathCache);
    }

    /// <summary>A provider that fails the test if storage is touched at all.</summary>
    private sealed class ExplodingStorageProvider : IStorageProvider
    {
        public string ProviderType => "local";
        private static Exception Boom([System.Runtime.CompilerServices.CallerMemberName] string op = "") =>
            new InvalidOperationException($"storage was accessed ({op}) — authorization must fail first");

        public Task<string> UploadAsync(StorageBucketConfig c, string k, byte[] b, string? t = null, CancellationToken ct = default) => throw Boom();
        public Task<byte[]> DownloadAsync(StorageBucketConfig c, string k, CancellationToken ct = default) => throw Boom();
        public Task DeleteAsync(StorageBucketConfig c, string k, CancellationToken ct = default) => throw Boom();
        public Task<bool> ExistsAsync(StorageBucketConfig c, string k, CancellationToken ct = default) => throw Boom();
        public Task<string> GetPresignedUrlAsync(StorageBucketConfig c, string k, int e = 15, bool u = false) => throw Boom();
    }

    /// <summary>Vetoes every write, standing in for any transformer that denies a mutation.</summary>
    private sealed class VetoMutationTransformer : IMutationTransformer
    {
        public int Priority => 500;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context) => true;

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data,
            MutationTransformContext context) =>
            ValueTask.FromResult(new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data,
                Errors = new[] { "vetoed by policy" },
            });
    }

    private static string KeyFor(long id) => $"file_data/{id}";

    // ---- reads --------------------------------------------------------------

    [Fact]
    public async Task Seam_Resolve_CrossTenantRow_FailsBeforeAnyStorageAccess()
    {
        var (seam, _) = await BuildSeamAsync(storageProvider: new ExplodingStorageProvider());
        var metadata = new FileMetadata { FileKey = "tenant2.txt", ContentType = "text/plain", Size = 15 };
        await Exec($"UPDATE documents SET file_data = '{metadata.ToJson().Replace("'", "''")}' WHERE id = 2");

        // Tenant 1 addresses tenant 2's object. The exploding provider proves the
        // denial happens before storage is consulted — not merely that the
        // response ends up empty.
        var resolved = await seam.ResolveAsync("documents", KeyFor(2), Tenant(1));

        resolved.Should().BeNull("a row the caller cannot see must not yield a file object");
    }

    [Fact]
    public async Task Seam_Resolve_OwnRow_ExposesTheObjectContract()
    {
        var (seam, _) = await BuildSeamAsync();
        await File.WriteAllTextAsync(Path.Combine(_bucketDir, "tenant1.txt"), "tenant-1-file");
        var metadata = new FileMetadata
        {
            FileKey = "tenant1.txt",
            ContentType = "text/plain",
            Size = 13,
            ETag = "abc123",
            CustomMetadata = new Dictionary<string, string> { ["x-owner"] = "alice" },
        };
        await Exec($"UPDATE documents SET file_data = '{metadata.ToJson().Replace("'", "''")}' WHERE id = 1");

        var resolved = await seam.ResolveAsync("documents", KeyFor(1), Tenant(1));

        resolved.Should().NotBeNull();
        resolved!.ContentType.Should().Be("text/plain");
        resolved.ContentLength.Should().Be(13);
        resolved.ETag.Should().Be("abc123");
        resolved.CustomMetadata.Should().Contain("x-owner", "alice");
    }

    [Fact]
    public async Task Seam_GetContent_RequiresAResolvedObject_AndReturnsTheBlob()
    {
        var (seam, _) = await BuildSeamAsync();
        await File.WriteAllTextAsync(Path.Combine(_bucketDir, "tenant1.txt"), "tenant-1-file");
        var metadata = new FileMetadata { FileKey = "tenant1.txt", ContentType = "text/plain", Size = 13 };
        await Exec($"UPDATE documents SET file_data = '{metadata.ToJson().Replace("'", "''")}' WHERE id = 1");

        var resolved = await seam.ResolveAsync("documents", KeyFor(1), Tenant(1));
        var content = await seam.GetContentAsync(resolved!);

        System.Text.Encoding.UTF8.GetString(content).Should().Be("tenant-1-file");
    }

    [Fact]
    public async Task Seam_Resolve_RowWithNoFile_ReturnsNull()
    {
        var (seam, _) = await BuildSeamAsync(storageProvider: new ExplodingStorageProvider());

        var resolved = await seam.ResolveAsync("documents", KeyFor(1), Tenant(1));

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task Seam_Resolve_NonFileColumn_IsNotAddressable()
    {
        var (seam, _) = await BuildSeamAsync(storageProvider: new ExplodingStorageProvider());

        // 'tenant_id' is a real column but is not configured for file storage;
        // the seam must not turn it into a downloadable object.
        var act = async () => await seam.ResolveAsync("documents", "tenant_id/1", Tenant(1));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- writes: the gate ---------------------------------------------------

    [Fact]
    public async Task Seam_Put_IsDisabledByDefault()
    {
        var (seam, _) = await BuildSeamAsync(enableWrites: false, storageProvider: new ExplodingStorageProvider());

        var act = async () => await seam.PutAsync(
            "documents", KeyFor(1), new byte[] { 1, 2, 3 }, "application/octet-stream", null, Tenant(1));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not enabled*");
        CountFilesInBucket().Should().Be(0);
    }

    [Fact]
    public async Task Seam_Put_DisabledGateRejectsBeforeParsingTheKey()
    {
        var (seam, _) = await BuildSeamAsync(enableWrites: false, storageProvider: new ExplodingStorageProvider());

        // A disabled surface must not even be probeable for behaviour: a garbage
        // bucket/key must produce the SAME "not enabled" rejection, never a
        // key-parsing or unknown-bucket error that reveals the schema.
        var act = async () => await seam.PutAsync(
            "no-such-bucket", "nonsense", new byte[] { 1 }, null, null, Tenant(1));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not enabled*");
    }

    [Fact]
    public async Task Seam_Delete_IsDisabledByDefault()
    {
        var (seam, _) = await BuildSeamAsync(enableWrites: false, storageProvider: new ExplodingStorageProvider());

        var act = async () => await seam.DeleteAsync("documents", KeyFor(1), Tenant(1));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not enabled*");
    }

    // ---- writes: authorization before storage -------------------------------

    [Fact]
    public async Task Seam_Put_CrossTenantRow_FailsBeforeAnyStorageAccess()
    {
        var (seam, _) = await BuildSeamAsync(storageProvider: new ExplodingStorageProvider());

        var act = async () => await seam.PutAsync(
            "documents", KeyFor(2), new byte[] { 1, 2, 3, 4 }, "application/octet-stream", null, Tenant(1));

        await act.Should().ThrowAsync<BifrostExecutionError>();
        GetFileDataColumn(2).Should().BeNull("a denied put must not touch another tenant's row");
    }

    [Fact]
    public async Task Seam_Put_NonexistentRow_FailsBeforeAnyStorageAccess()
    {
        var (seam, _) = await BuildSeamAsync(storageProvider: new ExplodingStorageProvider());

        var act = async () => await seam.PutAsync(
            "documents", KeyFor(999), new byte[] { 1 }, "application/octet-stream", null, Tenant(1));

        await act.Should().ThrowAsync<BifrostExecutionError>();
    }

    [Fact]
    public async Task Seam_Delete_CrossTenantRow_FailsBeforeAnyStorageAccess()
    {
        var (seam, _) = await BuildSeamAsync(storageProvider: new ExplodingStorageProvider());
        var metadata = new FileMetadata { FileKey = "tenant2.txt" };
        await Exec($"UPDATE documents SET file_data = '{metadata.ToJson().Replace("'", "''")}' WHERE id = 2");

        var act = async () => await seam.DeleteAsync("documents", KeyFor(2), Tenant(1));

        await act.Should().ThrowAsync<BifrostExecutionError>();
        GetFileDataColumn(2).Should().NotBeNullOrEmpty("a denied delete must not clear another tenant's pointer");
    }

    // ---- writes: happy path + contract --------------------------------------

    [Fact]
    public async Task Seam_Put_OwnRow_StoresBlobAtTheDeterministicKey_AndPersistsTheContract()
    {
        var (seam, _) = await BuildSeamAsync();
        var content = System.Text.Encoding.UTF8.GetBytes("hello");

        var put = await seam.PutAsync(
            "documents", KeyFor(1), content, "text/plain",
            new Dictionary<string, string> { ["x-owner"] = "alice" }, Tenant(1));

        put.ContentLength.Should().Be(5);
        put.ContentType.Should().Be("text/plain");
        put.ETag.Should().Be(System.Convert.ToHexString(System.Security.Cryptography.MD5.HashData(content)).ToLowerInvariant(),
            "the S3 ETag of a single-part object is the MD5 of its content");

        // The blob lands at the deterministic key derived from the row identity,
        // so a subsequent PUT to the same row overwrites rather than orphaning.
        File.Exists(Path.Combine(_bucketDir, KeyFor(1))).Should().BeTrue();

        var resolved = await seam.ResolveAsync("documents", KeyFor(1), Tenant(1));
        resolved!.ETag.Should().Be(put.ETag, "the ETag must be persisted on the row, not recomputed per read");
        resolved.CustomMetadata.Should().Contain("x-owner", "alice");
    }

    [Fact]
    public async Task Seam_Put_Twice_OverwritesInPlace_LeavingNoOrphan()
    {
        var (seam, _) = await BuildSeamAsync();

        await seam.PutAsync("documents", KeyFor(1), new byte[] { 1 }, "text/plain", null, Tenant(1));
        await seam.PutAsync("documents", KeyFor(1), new byte[] { 2, 2 }, "text/plain", null, Tenant(1));

        CountFilesInBucket().Should().Be(1, "a deterministic key makes a re-put an in-place overwrite");
        var resolved = await seam.ResolveAsync("documents", KeyFor(1), Tenant(1));
        resolved!.ContentLength.Should().Be(2);
    }

    [Fact]
    public async Task Seam_Delete_OwnRow_ClearsPointerAndRemovesBlob()
    {
        var (seam, _) = await BuildSeamAsync();
        await seam.PutAsync("documents", KeyFor(1), new byte[] { 1, 2, 3 }, "text/plain", null, Tenant(1));

        var deleted = await seam.DeleteAsync("documents", KeyFor(1), Tenant(1));

        deleted.Should().BeTrue();
        GetFileDataColumn(1).Should().BeNull();
        CountFilesInBucket().Should().Be(0);
    }

    [Fact]
    public async Task Seam_Delete_ObjectThatDoesNotExist_IsIdempotent()
    {
        var (seam, _) = await BuildSeamAsync();

        var deleted = await seam.DeleteAsync("documents", KeyFor(1), Tenant(1));

        deleted.Should().BeFalse("deleting an absent object is a no-op, not an error");
    }

    // ---- writes: the failure paths ------------------------------------------

    [Fact]
    public async Task Seam_Put_RollsBackTheBlob_WhenTheRowMutationIsVetoed()
    {
        // The throwing-substitute path: a transformer veto AFTER the blob is
        // already uploaded must not leave an orphan behind.
        var (seam, _) = await BuildSeamAsync(
            extraMutationTransformers: new IMutationTransformer[] { new VetoMutationTransformer() });

        var act = async () => await seam.PutAsync(
            "documents", KeyFor(1), new byte[] { 1, 2, 3 }, "text/plain", null, Tenant(1));

        await act.Should().ThrowAsync<BifrostExecutionError>();
        CountFilesInBucket().Should().Be(0, "a failed row mutation must roll the uploaded blob back");
        GetFileDataColumn(1).Should().BeNull();
    }

    [Fact]
    public async Task Seam_Put_SurfacesBothFailures_WhenTheRollbackAlsoFails()
    {
        // Compensation is best-effort but never silent: if the rollback delete
        // fails too, the caller must learn that an orphan was left behind.
        var (seam, _) = await BuildSeamAsync(
            storageProvider: new UploadOkDeleteFailsProvider(_bucketDir),
            extraMutationTransformers: new IMutationTransformer[] { new VetoMutationTransformer() });

        var act = async () => await seam.PutAsync(
            "documents", KeyFor(1), new byte[] { 1, 2, 3 }, "text/plain", null, Tenant(1));

        (await act.Should().ThrowAsync<BifrostExecutionError>())
            .WithMessage("*orphan*");
    }

    /// <summary>Uploads succeed, deletes always fail — exercises the failed-rollback path.</summary>
    private sealed class UploadOkDeleteFailsProvider : IStorageProvider
    {
        private readonly LocalStorageProvider _inner = new();
        private readonly string _bucketDir;
        public UploadOkDeleteFailsProvider(string bucketDir) => _bucketDir = bucketDir;
        public string ProviderType => "local";

        public Task<string> UploadAsync(StorageBucketConfig c, string k, byte[] b, string? t = null, CancellationToken ct = default)
            => _inner.UploadAsync(c, k, b, t, ct);
        public Task<byte[]> DownloadAsync(StorageBucketConfig c, string k, CancellationToken ct = default)
            => _inner.DownloadAsync(c, k, ct);
        public Task DeleteAsync(StorageBucketConfig c, string k, CancellationToken ct = default)
            => throw new IOException("storage delete failed");
        public Task<bool> ExistsAsync(StorageBucketConfig c, string k, CancellationToken ct = default)
            => _inner.ExistsAsync(c, k, ct);
        public Task<string> GetPresignedUrlAsync(StorageBucketConfig c, string k, int e = 15, bool u = false)
            => _inner.GetPresignedUrlAsync(c, k, e, u);
    }

    [Fact]
    public async Task Seam_Resolve_CorruptPointer_FailsLoudly()
    {
        var (seam, _) = await BuildSeamAsync(storageProvider: new ExplodingStorageProvider());
        await Exec("UPDATE documents SET file_data = 'not-json' WHERE id = 1");

        var act = async () => await seam.ResolveAsync("documents", KeyFor(1), Tenant(1));

        await act.Should().ThrowAsync<BifrostExecutionError>(
            "a corrupt pointer must not masquerade as 'no such object'");
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
