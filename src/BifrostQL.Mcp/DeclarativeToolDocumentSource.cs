namespace BifrostQL.Mcp
{
    public interface IDeclarativeToolDocumentSource
    {
        string Description { get; }
        Stream OpenRead();
    }

    public sealed class FileDeclarativeToolDocumentSource(string path) : IDeclarativeToolDocumentSource
    {
        private readonly string _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("Tool document path is required.", nameof(path))
            : path;

        public string Description => _path;

        public Stream OpenRead() => File.OpenRead(_path);
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

        public Stream OpenRead() => new MemoryStream(_content, writable: false);
    }
}
