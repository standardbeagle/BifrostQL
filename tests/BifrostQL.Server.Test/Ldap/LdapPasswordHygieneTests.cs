using System.Runtime.InteropServices;
using System.Text;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// The bind path zeroed the decoded password, but the cleartext had already been copied into
    /// buffers it never touched: the message body the decoder read into, the array that body
    /// outgrew on the way, and the connection-lifetime read buffer underneath. Each fact reads a
    /// BindRequest through the production reader and then inspects every array the reader was
    /// handed to fill.
    /// </summary>
    public sealed class LdapPasswordHygieneTests
    {
        private const string Password = "Pa55word-Marker!";
        private static readonly byte[] PasswordBytes = Encoding.UTF8.GetBytes(Password);

        private static LdapMessageReader Reader() => new(1 << 20, 32, 1024, 1024);

        [Fact]
        public async Task Message_body_is_wiped_after_decode_including_the_outgrown_array()
        {
            // A password longer than ReceivedByteBuffer.InitialCapacity, behind a short DN, makes the
            // body grow once: the outgrown array holds the password's first 16 KiB (its marker
            // prefix) and the final array holds all of it. A long DN would put the password past the
            // growth point and leave the outgrown array with nothing to find.
            var longPassword = Password + new string('p', 20_000);
            var message = LdapWire.Message(1, LdapWire.BindRequest(name: "uid=alice", password: longPassword));
            var transport = new CapturingStream(message, chunk: 4096);

            var request = await Reader().ReadRequestAsync(transport, CancellationToken.None);

            request!.Operation.Should().BeOfType<LdapBindRequest>()
                .Which.SimplePassword.Should().Equal(Encoding.UTF8.GetBytes(longPassword),
                    "the decoded copy is the one the bind path verifies");
            transport.ReceivingArrays.Should().HaveCount(2, "the body grew once, so two arrays received it");
            transport.ReceivingArrays.Should().OnlyContain(a => !Contains(a, PasswordBytes),
                "neither the outgrown array nor the final body may still hold the password");
        }

        [Fact]
        public async Task Connection_read_buffer_is_wiped_as_it_is_consumed()
        {
            var message = LdapWire.Message(1, LdapWire.BindRequest(name: "uid=alice", password: Password));
            var transport = new CapturingStream(message, chunk: message.Length);
            var wire = new LdapBufferedStream(transport);

            var request = await Reader().ReadRequestAsync(wire, CancellationToken.None);

            request!.Operation.Should().BeOfType<LdapBindRequest>();
            transport.ReceivingArrays.Should().ContainSingle("one burst fills the connection buffer");
            Contains(transport.ReceivingArrays[0], PasswordBytes).Should().BeFalse(
                "the connection-lifetime read buffer must not keep the password once it is consumed");
        }

        private static bool Contains(byte[] haystack, byte[] needle) =>
            haystack.AsSpan().IndexOf(needle) >= 0;

        /// <summary>Serves fixed bytes in chunks and records every array it was asked to fill.</summary>
        private sealed class CapturingStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _chunk;
            private int _position;

            public CapturingStream(byte[] data, int chunk)
            {
                _data = data;
                _chunk = chunk;
            }

            public List<byte[]> ReceivingArrays { get; } = new();

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (MemoryMarshal.TryGetArray<byte>(buffer, out var segment)
                    && segment.Array is { Length: > 16 } array
                    && !ReceivingArrays.Any(a => ReferenceEquals(a, array)))
                    ReceivingArrays.Add(array);
                var take = Math.Min(Math.Min(buffer.Length, _chunk), _data.Length - _position);
                _data.AsSpan(_position, take).CopyTo(buffer.Span);
                _position += take;
                return ValueTask.FromResult(take);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override bool CanRead => true;
            public override bool CanWrite => false;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
