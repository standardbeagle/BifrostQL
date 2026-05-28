using System.Text;
using BifrostQL.Server;
using FluentAssertions;
using Google.Protobuf;
using Xunit;
using WireType = Google.Protobuf.WireFormat.WireType;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Adversarial and invariant coverage for the hand-rolled protobuf wire format:
    /// field-order independence, unknown-field skipping, repeated-scalar semantics,
    /// truncation handling, large varints, determinism, and cross-checks that the
    /// emitted bytes match the documented .proto field numbers / wire types.
    /// </summary>
    public class BinaryWireFormatTests
    {
        [Fact]
        public void AllFieldsSet_RoundTrips()
        {
            var msg = new BifrostMessage
            {
                RequestId = 123456,
                Type = BifrostMessageType.Chunk,
                Query = "{ users { id } }",
                VariablesJson = "{\"a\":1}",
                Payload = new byte[] { 9, 8, 7, 0, 255 },
                Errors = { "e1", "e2" },
                ChunkSequence = 3,
                ChunkTotal = 7,
                ChunkOffset = 5_000_000_000UL,
                TotalBytes = 9_000_000_000UL,
                ChunkChecksum = 0xCAFEBABE,
                LastSequence = 42,
            };

            var restored = BifrostMessage.FromBytes(msg.ToBytes());

            restored.RequestId.Should().Be(123456);
            restored.Type.Should().Be(BifrostMessageType.Chunk);
            restored.Query.Should().Be("{ users { id } }");
            restored.VariablesJson.Should().Be("{\"a\":1}");
            restored.Payload.Should().BeEquivalentTo(new byte[] { 9, 8, 7, 0, 255 });
            restored.Errors.Should().Equal("e1", "e2");
            restored.ChunkSequence.Should().Be(3);
            restored.ChunkTotal.Should().Be(7);
            restored.ChunkOffset.Should().Be(5_000_000_000UL);
            restored.TotalBytes.Should().Be(9_000_000_000UL);
            restored.ChunkChecksum.Should().Be(0xCAFEBABE);
            restored.LastSequence.Should().Be(42);
        }

        [Fact]
        public void FromBytes_FieldsInReverseOrder_Parses()
        {
            // A conforming protobuf encoder may emit fields in any order. Hand-write
            // them back-to-front and confirm the parser is order-independent.
            var bytes = HandCraft(o =>
            {
                o.WriteTag(12, WireType.Varint); o.WriteUInt32(99);    // LastSequence
                o.WriteTag(3, WireType.LengthDelimited); o.WriteString("{ q }"); // Query
                o.WriteTag(2, WireType.Varint); o.WriteInt32((int)BifrostMessageType.Mutation);
                o.WriteTag(1, WireType.Varint); o.WriteUInt32(7);      // RequestId
            });

            var msg = BifrostMessage.FromBytes(bytes);

            msg.RequestId.Should().Be(7);
            msg.Type.Should().Be(BifrostMessageType.Mutation);
            msg.Query.Should().Be("{ q }");
            msg.LastSequence.Should().Be(99);
        }

        [Fact]
        public void FromBytes_UnknownVarintField_IsSkipped()
        {
            var bytes = HandCraft(o =>
            {
                o.WriteTag(1, WireType.Varint); o.WriteUInt32(5);          // RequestId (known)
                o.WriteTag(13, WireType.Varint); o.WriteInt64(987654321);  // unknown field 13
                o.WriteTag(3, WireType.LengthDelimited); o.WriteString("ok");
            });

            var msg = BifrostMessage.FromBytes(bytes);

            msg.RequestId.Should().Be(5);
            msg.Query.Should().Be("ok");
        }

        [Fact]
        public void FromBytes_UnknownLengthDelimitedField_IsSkipped()
        {
            var bytes = HandCraft(o =>
            {
                o.WriteTag(1, WireType.Varint); o.WriteUInt32(8);
                o.WriteTag(14, WireType.LengthDelimited); o.WriteString("future-field-payload");
                o.WriteTag(3, WireType.LengthDelimited); o.WriteString("still-here");
            });

            var msg = BifrostMessage.FromBytes(bytes);

            msg.RequestId.Should().Be(8);
            msg.Query.Should().Be("still-here");
        }

        [Fact]
        public void FromBytes_RepeatedScalarField_LastValueWins()
        {
            // Protobuf semantics: a duplicated scalar field takes the last value.
            var bytes = HandCraft(o =>
            {
                o.WriteTag(1, WireType.Varint); o.WriteUInt32(5);
                o.WriteTag(1, WireType.Varint); o.WriteUInt32(9);
            });

            BifrostMessage.FromBytes(bytes).RequestId.Should().Be(9);
        }

        [Fact]
        public void FromBytes_TruncatedLengthDelimitedField_Throws()
        {
            var full = new BifrostMessage { RequestId = 1, Query = "a longer query string" }.ToBytes();
            var truncated = new byte[full.Length - 3];
            Buffer.BlockCopy(full, 0, truncated, 0, truncated.Length);

            var act = () => BifrostMessage.FromBytes(truncated);

            act.Should().Throw<InvalidProtocolBufferException>();
        }

        [Fact]
        public void UnknownEnumTypeValue_RoundTripsAsRawValue()
        {
            var msg = new BifrostMessage { RequestId = 1, Type = (BifrostMessageType)99 };

            var restored = BifrostMessage.FromBytes(msg.ToBytes());

            ((int)restored.Type).Should().Be(99);
        }

        [Fact]
        public void ToBytes_IsDeterministic()
        {
            var msg = new BifrostMessage
            {
                RequestId = 77,
                Type = BifrostMessageType.Result,
                Payload = Encoding.UTF8.GetBytes("{\"x\":1}"),
                Errors = { "a", "b" },
                ChunkOffset = 4_000_000_000UL,
            };

            msg.ToBytes().Should().Equal(msg.ToBytes());
        }

        [Fact]
        public void Payload_AllByteValues_RoundTrip()
        {
            var payload = new byte[256];
            for (var i = 0; i < 256; i++) payload[i] = (byte)i;
            var msg = new BifrostMessage { RequestId = 1, Type = BifrostMessageType.Result, Payload = payload };

            BifrostMessage.FromBytes(msg.ToBytes()).Payload.Should().Equal(payload);
        }

        [Fact]
        public void Errors_PreserveOrderIncludingEmptyAndUnicode()
        {
            var msg = new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Error,
                Errors = { "", "élève", "third" },
            };

            BifrostMessage.FromBytes(msg.ToBytes()).Errors
                .Should().Equal("", "élève", "third");
        }

        [Fact]
        public void WireFormat_FieldNumbersAndWireTypes_MatchProtoSpec()
        {
            // A fully-populated message must encode each field with the documented
            // field number and wire type so any standard protobuf client can parse it.
            var msg = new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Mutation,
                Query = "q",
                VariablesJson = "{}",
                Payload = new byte[] { 1 },
                Errors = { "x" },
                ChunkSequence = 1,
                ChunkTotal = 1,
                ChunkOffset = 1,
                TotalBytes = 1,
                ChunkChecksum = 1,
                LastSequence = 1,
            };

            var seen = new HashSet<(int field, WireType wt)>();
            var input = new CodedInputStream(msg.ToBytes());
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                seen.Add(((int)(tag >> 3), (WireType)(tag & 0x7)));
                input.SkipLastField();
            }

            seen.Should().Contain(new (int, WireType)[]
            {
                (1, WireType.Varint),           // request_id
                (2, WireType.Varint),           // type
                (3, WireType.LengthDelimited),  // query
                (4, WireType.LengthDelimited),  // variables_json
                (5, WireType.LengthDelimited),  // payload
                (6, WireType.LengthDelimited),  // errors
                (7, WireType.Varint),           // chunk_sequence
                (8, WireType.Varint),           // chunk_total
                (9, WireType.Varint),           // chunk_offset
                (10, WireType.Varint),          // total_bytes
                (11, WireType.Varint),          // chunk_checksum
                (12, WireType.Varint),          // last_sequence
            });
        }

        [Fact]
        public void Fuzz_RandomMessages_RoundTrip()
        {
            var rnd = new Random(0xB1F405);
            var types = (BifrostMessageType[])Enum.GetValues(typeof(BifrostMessageType));

            for (var iter = 0; iter < 500; iter++)
            {
                var payload = new byte[rnd.Next(0, 300)];
                rnd.NextBytes(payload);

                var errorCount = rnd.Next(0, 4);
                var errors = new List<string>();
                for (var e = 0; e < errorCount; e++)
                    errors.Add(RandomString(rnd, rnd.Next(0, 20)));

                var msg = new BifrostMessage
                {
                    RequestId = (uint)rnd.Next(),
                    Type = types[rnd.Next(types.Length)],
                    Query = RandomString(rnd, rnd.Next(0, 40)),
                    VariablesJson = RandomString(rnd, rnd.Next(0, 40)),
                    Payload = payload,
                    Errors = errors,
                    ChunkSequence = (uint)rnd.Next(),
                    ChunkTotal = (uint)rnd.Next(),
                    ChunkOffset = (ulong)rnd.NextInt64(),
                    TotalBytes = (ulong)rnd.NextInt64(),
                    ChunkChecksum = (uint)rnd.Next(),
                    LastSequence = (uint)rnd.Next(),
                };

                var restored = BifrostMessage.FromBytes(msg.ToBytes());

                restored.RequestId.Should().Be(msg.RequestId);
                restored.Type.Should().Be(msg.Type);
                restored.Query.Should().Be(msg.Query);
                restored.VariablesJson.Should().Be(msg.VariablesJson);
                restored.Payload.Should().Equal(msg.Payload);
                restored.Errors.Should().Equal(msg.Errors);
                restored.ChunkSequence.Should().Be(msg.ChunkSequence);
                restored.ChunkTotal.Should().Be(msg.ChunkTotal);
                restored.ChunkOffset.Should().Be(msg.ChunkOffset);
                restored.TotalBytes.Should().Be(msg.TotalBytes);
                restored.ChunkChecksum.Should().Be(msg.ChunkChecksum);
                restored.LastSequence.Should().Be(msg.LastSequence);
            }
        }

        private static string RandomString(Random rnd, int length)
        {
            const string chars = "abcdefABCDEF0123456789 {}\":,éè";
            var sb = new StringBuilder(length);
            for (var i = 0; i < length; i++)
                sb.Append(chars[rnd.Next(chars.Length)]);
            return sb.ToString();
        }

        private static byte[] HandCraft(Action<CodedOutputStream> write)
        {
            using var ms = new MemoryStream();
            var o = new CodedOutputStream(ms, leaveOpen: true);
            write(o);
            o.Flush();
            return ms.ToArray();
        }
    }
}
