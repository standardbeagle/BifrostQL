using System.Buffers;
using System.IO.Pipelines;
using BifrostQL.Server.Pgwire;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// A server transport whose peer sends but never reads — the zero-window shape. The server end
    /// is the production Kestrel glue (<see cref="DuplexPipeStream"/>) over two in-memory pipes; the
    /// server-to-peer pipe pauses its writer after <see cref="PauseThreshold"/> unread bytes, exactly
    /// as Kestrel's output pipe does once the socket stops draining, so the server's flush parks on
    /// backpressure until something cancels it. Nothing here measures time: the test waits for the
    /// server to park (<see cref="WaitUntilServerIsBlockedAsync"/>) and drives the clock itself.
    /// </summary>
    internal sealed class StalledReaderTransport
    {
        public const int PauseThreshold = 256;

        private readonly Pipe _toServer = new();
        private readonly Pipe _toPeer = new(new PipeOptions(
            pauseWriterThreshold: PauseThreshold, resumeWriterThreshold: PauseThreshold / 2,
            useSynchronizationContext: false));
        private readonly ParkSignallingWriter _serverOutput;

        public StalledReaderTransport()
        {
            _serverOutput = new ParkSignallingWriter(_toPeer.Writer);
            ServerStream = new DuplexPipeStream(new Duplex(_toServer.Reader, _serverOutput));
        }

        /// <summary>The stream the connection handler runs over.</summary>
        public Stream ServerStream { get; }

        /// <summary>Sends bytes from the peer to the server.</summary>
        public async Task SendAsync(byte[] bytes)
        {
            await _toServer.Writer.WriteAsync(bytes);
        }

        /// <summary>
        /// Reads (and consumes) exactly one chunk the server wrote, so a test can drain the replies it
        /// expects — the AUTH +OK — before it stops reading.
        /// </summary>
        public async Task<byte[]> ReadAvailableAsync()
        {
            var result = await _toPeer.Reader.ReadAsync();
            var bytes = result.Buffer.ToArray();
            _toPeer.Reader.AdvanceTo(result.Buffer.End);
            return bytes;
        }

        /// <summary>
        /// Completes once a server flush has actually parked: the pipe returned it pending because
        /// the peer left a full window unread. The signal comes from the writer, not from reading
        /// the peer's end: an earlier version waited until the peer could see a window's worth of
        /// bytes, examining (never consuming) them, and the LDAP server was measured writing on past
        /// that point to 3 KiB while being examined. It was not stuck when the clock moved, so it
        /// came back round its loop, left on the ordinary deadline check, and a write-deadline fact
        /// passed with the write deadline removed. After this completes, the test must not read the
        /// peer's end until the clock has moved.
        /// </summary>
        public Task WaitUntilServerIsBlockedAsync() => _serverOutput.Parked;

        private sealed class Duplex : IDuplexPipe
        {
            public Duplex(PipeReader input, PipeWriter output)
            {
                Input = input;
                Output = output;
            }

            public PipeReader Input { get; }
            public PipeWriter Output { get; }
        }

        /// <summary>The server's output pipe, reporting the first flush backpressure parks.</summary>
        private sealed class ParkSignallingWriter : PipeWriter
        {
            private readonly PipeWriter _inner;
            private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public ParkSignallingWriter(PipeWriter inner) => _inner = inner;

            public Task Parked => _parked.Task;

            public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            {
                var flush = _inner.FlushAsync(cancellationToken);
                if (!flush.IsCompleted)
                    _parked.TrySetResult();
                return flush;
            }

            public override void Advance(int bytes) => _inner.Advance(bytes);
            public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
            public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
            public override void CancelPendingFlush() => _inner.CancelPendingFlush();
            public override void Complete(Exception? exception = null) => _inner.Complete(exception);
        }
    }
}
