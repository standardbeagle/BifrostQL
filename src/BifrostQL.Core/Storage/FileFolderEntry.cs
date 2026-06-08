namespace BifrostQL.Core.Storage;

public sealed class FileFolderEntry
{
    public required string Name { get; init; }
    public required string Key { get; init; }
    public required bool IsFolder { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public string? ContentType { get; init; }
    public string? Url { get; init; }
}

public interface IStorageFolderProvider
{
    Task<IReadOnlyList<FileFolderEntry>> ListFolderAsync(
        StorageBucketConfig bucketConfig,
        string folderKey,
        bool recursive = false,
        CancellationToken cancellationToken = default);
}
