using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Storage;
using NSubstitute;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Shared test doubles for the file resolver integration tests. A minimal
/// <see cref="IBifrostFieldContext"/> that surfaces the connection factory and
/// model through <c>InputExtensions</c> (the keys <see cref="BifrostContextAdapter"/>
/// reads), plus recording/failing storage providers used to drive the
/// storage-delete-before-DB-clear ordering assertions.
/// </summary>
internal sealed class FileResolverTestContext : IBifrostFieldContext
{
    private readonly Dictionary<string, string?> _args;

    public FileResolverTestContext(IDbConnFactory connFactory, IDbModel model, Dictionary<string, string?> args)
    {
        _args = args;
        InputExtensions = new Dictionary<string, object?>
        {
            ["connFactory"] = connFactory,
            ["model"] = model,
            ["tableReaderFactory"] = Substitute.For<ISqlExecutionManager>(),
        };
    }

    public IDictionary<string, object?> UserContext { get; } = new Dictionary<string, object?>();
    public IServiceProvider? RequestServices => null;
    public IDictionary<string, object?> InputExtensions { get; }
    public CancellationToken CancellationToken => CancellationToken.None;

    public string FieldName => "file";
    public string? FieldAlias => null;
    public object? Source => null;
    public IReadOnlyList<object> Path => Array.Empty<object>();
    public bool HasSubFields => true;
    public object Document => null!;
    public object Variables => null!;

    public bool HasArgument(string name) => _args.ContainsKey(name);

    public T? GetArgument<T>(string name)
    {
        if (_args.TryGetValue(name, out var value) && value is T typed)
            return typed;
        return default;
    }
}

/// <summary>Records delete calls and always succeeds.</summary>
internal sealed class RecordingStorageProvider : IStorageProvider
{
    private readonly string _providerType;
    public List<string> DeletedKeys { get; } = new();

    public RecordingStorageProvider(string providerType) => _providerType = providerType;

    public string ProviderType => _providerType;

    public Task DeleteAsync(StorageBucketConfig bucketConfig, string fileKey, CancellationToken cancellationToken = default)
    {
        DeletedKeys.Add(fileKey);
        return Task.CompletedTask;
    }

    public Task<string> UploadAsync(StorageBucketConfig bucketConfig, string fileKey, byte[] content, string? contentType = null, CancellationToken cancellationToken = default)
        => Task.FromResult($"mem://{fileKey}");

    public Task<byte[]> DownloadAsync(StorageBucketConfig bucketConfig, string fileKey, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<bool> ExistsAsync(StorageBucketConfig bucketConfig, string fileKey, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<string> GetPresignedUrlAsync(StorageBucketConfig bucketConfig, string fileKey, int expirationMinutes = 15, bool forUpload = false)
        => Task.FromResult($"mem://{fileKey}?url");
}

/// <summary>Throws on delete to simulate a storage backend failure.</summary>
internal sealed class FailingStorageProvider : IStorageProvider
{
    private readonly string _providerType;

    public FailingStorageProvider(string providerType) => _providerType = providerType;

    public string ProviderType => _providerType;

    public Task DeleteAsync(StorageBucketConfig bucketConfig, string fileKey, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("storage delete failed");

    public Task<string> UploadAsync(StorageBucketConfig bucketConfig, string fileKey, byte[] content, string? contentType = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("storage upload failed");

    public Task<byte[]> DownloadAsync(StorageBucketConfig bucketConfig, string fileKey, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("storage download failed");

    public Task<bool> ExistsAsync(StorageBucketConfig bucketConfig, string fileKey, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<string> GetPresignedUrlAsync(StorageBucketConfig bucketConfig, string fileKey, int expirationMinutes = 15, bool forUpload = false)
        => throw new InvalidOperationException("storage url failed");
}
