using Amazon.S3;
using Amazon.S3.Model;

namespace BifrostQL.Aws;

/// <summary>
/// A forward-only read over a GET response body that owns the response and the client that
/// produced it, so the caller of <see cref="S3StorageProvider.OpenReadAsync"/> disposes one
/// object and releases the connection and the client together.
/// </summary>
internal sealed class S3ObjectReadStream : Stream
{
    private readonly GetObjectResponse _response;
    private readonly IAmazonS3 _client;
    private readonly Stream _body;

    public S3ObjectReadStream(GetObjectResponse response, IAmazonS3 client)
    {
        _response = response;
        _client = client;
        _body = response.ResponseStream;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _body.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _body.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _body.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _response.Dispose();
            _client.Dispose();
        }
        base.Dispose(disposing);
    }
}
