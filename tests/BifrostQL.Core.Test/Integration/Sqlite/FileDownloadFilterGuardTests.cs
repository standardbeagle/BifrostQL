using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
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
/// <see cref="FileDownloadResolver"/> builds its own key-predicated SQL. It ran
/// <see cref="IColumnReadGuard"/> for the file column but never
/// <see cref="IColumnFilterGuard"/> for the key columns it puts in that WHERE — the
/// fourth independent copy of the read chain, and the fourth to have drifted from it.
///
/// <see cref="FileResolverSecurityTests"/> is tenant-ROW-only, so it cannot manifest
/// a missing column guard: it registers only <c>TenantFilterTransformer</c>, which
/// implements neither guard interface.
///
/// Severity here is low on its own (the predicate is the record's own primary key),
/// so these tests pin the seam rather than a leak: a registered filter guard must be
/// consulted on this surface exactly as on every other read surface, and must not
/// over-reject when it permits.
/// </summary>
public sealed class FileDownloadFilterGuardTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private string _bucketDir = null!;

    private static readonly string[] Rules = { "*.documents { }" };

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_file_guard_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
        _connFactory = new SqliteDbConnFactory(_connectionString);

        _bucketDir = Path.Combine(Path.GetTempPath(), $"bifrost-file-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_bucketDir);

        await Exec(
            """
            CREATE TABLE documents (
                id INTEGER PRIMARY KEY,
                file_data TEXT NULL
            )
            """);

        await File.WriteAllTextAsync(Path.Combine(_bucketDir, "doc.txt"), "contents");
        var metadata = new FileMetadata
        {
            FileKey = "doc.txt",
            ContentType = "text/plain",
            Size = 8,
            BucketName = _bucketDir,
            ProviderType = "local",
        };
        await Exec($"INSERT INTO documents(id, file_data) VALUES (1, '{metadata.ToJson().Replace("'", "''")}')");
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
        SqliteConnection.ClearAllPools();
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
        var model = await new DbModelLoader(_connFactory, new MetadataLoader(Rules)).LoadAsync();
        model.GetTableFromDbName("documents").ColumnLookup["file_data"]
            .Metadata[MetadataKeys.Storage.Config] = $"bucket:{_bucketDir};provider:local";
        return model;
    }

    /// <summary>
    /// A filter guard that refuses one named column in a predicate position, and
    /// records what it was asked about — so a test can prove the surface consulted it
    /// at all, not merely that it happened to return the right answer.
    /// </summary>
    private sealed class RecordingFilterGuard : IFilterTransformer, IColumnFilterGuard
    {
        private readonly string? _denied;
        public RecordingFilterGuard(string? denied) => _denied = denied;

        public List<string> Seen { get; } = new();

        public int Priority => 40;
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => false;
        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;

        public void AssertColumnsFilterable(
            IDbTable table, IEnumerable<string> filteredColumns, QueryTransformContext context)
        {
            foreach (var name in filteredColumns)
            {
                Seen.Add(name);
                if (string.Equals(name, _denied, StringComparison.OrdinalIgnoreCase))
                    throw new BifrostExecutionError("A requested column may not be used in a filter.")
                    { ErrorCode = BifrostExecutionError.AccessDeniedCode };
            }
        }
    }

    private async Task<object?> DownloadAsync(RecordingFilterGuard guard)
    {
        var model = await LoadModelAsync();
        var schema = DbSchema.FromModel(model);

        var services = new ServiceCollection();
        services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { guard },
        });
        await using var provider = services.BuildServiceProvider();

        var context = new FakeFieldContext
        {
            Arguments = new Dictionary<string, object?>
            {
                ["table"] = "documents",
                ["column"] = "file_data",
                ["recordId"] = "1",
            },
            UserContext = new Dictionary<string, object?>(),
            RequestServices = provider,
            InputExtensions = new Dictionary<string, object?>
            {
                ["connFactory"] = _connFactory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema, BifrostQL.Core.Modules.NullQueryTransformerService.Instance),
            },
        };

        return await new FileDownloadResolver(new FileStorageService()).ResolveAsync(context);
    }

    [Fact]
    public async Task Download_KeyColumnDeniedByFilterGuard_IsRejected()
    {
        var guard = new RecordingFilterGuard(denied: "id");

        var act = () => DownloadAsync(guard);

        await act.Should().ThrowAsync<BifrostExecutionError>();
        guard.Seen.Should().Contain("id", "the key columns this resolver filters by must reach the filter guard");
    }

    [Fact]
    public async Task Download_FilterGuardPermits_StillReturnsFile()
    {
        var guard = new RecordingFilterGuard(denied: null);

        var result = await DownloadAsync(guard);

        result.Should().NotBeNull();
        guard.Seen.Should().Contain("id");
        ((FileDownloadResult)result!).FileKey.Should().Be("doc.txt");
    }

    private sealed class FakeFieldContext : IBifrostFieldContext
    {
        public string FieldName => "_downloadFile";
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
