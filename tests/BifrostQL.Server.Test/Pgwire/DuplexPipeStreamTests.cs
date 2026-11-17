using System.Buffers;
using System.IO.Pipelines;
using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire;

/// <summary>
/// The pgwire, RESP and LDAP handlers all talk to Kestrel's transport through
/// <see cref="DuplexPipeStream"/>. Its synchronous members used to block on the async pipe
/// (sync-over-async on a connection thread); now they refuse, and the array overloads of
/// the async members reach the pipe without going through them.
/// </summary>
public sealed class DuplexPipeStreamTests
{
    private sealed class Transport(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }

    [Fact]
    public void SynchronousMembers_Refuse_InsteadOfBlockingOnThePipe()
    {
        var toPeer = new Pipe();
        var fromPeer = new Pipe();
        using var stream = new DuplexPipeStream(new Transport(fromPeer.Reader, toPeer.Writer));

        // Write and Flush are the members a blocking adapter completes without complaint;
        // Read would hang on an empty pipe, so it is asserted the same way after them.
        stream.Invoking(s => s.Write(new byte[] { 1, 2, 3 }, 0, 3)).Should().Throw<NotSupportedException>();
        stream.Invoking(s => s.Flush()).Should().Throw<NotSupportedException>();
        stream.Invoking(s => s.Read(new byte[3], 0, 3)).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task ArrayOverloadsOfTheAsyncMembers_ReachThePipe()
    {
        var toPeer = new Pipe();
        var fromPeer = new Pipe();
        await using var stream = new DuplexPipeStream(new Transport(fromPeer.Reader, toPeer.Writer));

        await stream.WriteAsync(new byte[] { 9, 8, 7 }, 0, 3, CancellationToken.None);
        await stream.FlushAsync(CancellationToken.None);
        var sent = await toPeer.Reader.ReadAsync();
        sent.Buffer.ToArray().Should().Equal(9, 8, 7);
        toPeer.Reader.AdvanceTo(sent.Buffer.End);

        await fromPeer.Writer.WriteAsync(new byte[] { 4, 5 });
        var buffer = new byte[4];
        var read = await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
        read.Should().Be(2);
        buffer.Take(2).Should().Equal(4, 5);
    }
}
