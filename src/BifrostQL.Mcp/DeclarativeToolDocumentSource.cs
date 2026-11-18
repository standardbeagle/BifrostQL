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

    /// <summary>
    /// A document read from a stream the caller opens on demand. <c>openStream</c> runs when the
    /// provider loads the document at host start, never at registration, so the caller opens its
    /// backing store (an embedded resource, a blob, a socket) with async I/O. The loader disposes
    /// the returned stream.
    /// </summary>
    public sealed class StreamDeclarativeToolDocumentSource : IDeclarativeToolDocumentSource
    {
        private readonly Func<CancellationToken, Task<Stream>> _openStream;

        public StreamDeclarativeToolDocumentSource(Func<CancellationToken, Task<Stream>> openStream, string description = "stream")
        {
            ArgumentNullException.ThrowIfNull(openStream);
            _openStream = openStream;
            Description = description;
        }

        public string Description { get; }

        public async ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
            await _openStream(cancellationToken)
            ?? throw new InvalidOperationException($"The tool document stream factory for '{Description}' returned null.");
    }
}
