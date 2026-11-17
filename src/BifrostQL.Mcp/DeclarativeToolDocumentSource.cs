namespace BifrostQL.Mcp
{
    /// <summary>
    /// Where a declarative MCP tool document comes from. <see cref="OpenReadAsync"/> runs once, from
    /// <see cref="DeclarativeToolDocumentProvider"/> at host start, so an implementation reads its
    /// backing store asynchronously and never blocks a thread on I/O.
    /// </summary>
    public interface IDeclarativeToolDocumentSource
    {
        string Description { get; }
        ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken);
    }

    public sealed class FileDeclarativeToolDocumentSource(string path) : IDeclarativeToolDocumentSource
    {
        private readonly string _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("Tool document path is required.", nameof(path))
            : path;

        public string Description => _path;

        // FileOptions.Asynchronous makes the reads the loader awaits true async I/O instead of
        // synchronous reads on a thread-pool thread.
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous));
    }

    public sealed class StreamDeclarativeToolDocumentSource : IDeclarativeToolDocumentSource
    {
        private readonly byte[] _content;

        public StreamDeclarativeToolDocumentSource(Stream stream, string description = "stream")
        {
            ArgumentNullException.ThrowIfNull(stream);
            Description = description;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            _content = buffer.ToArray();
        }

        public string Description { get; }

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream(_content, writable: false));
    }
}
