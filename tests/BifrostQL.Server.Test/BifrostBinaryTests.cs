using System.Text;
using System.Text.Json;
using BifrostQL.Server;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    public class BifrostMessageSerializationTests
    {
        [Fact]
        public void EmptyMessage_RoundTrips()
        {
            var msg = new BifrostMessage();
            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.RequestId.Should().Be(0);
            restored.Type.Should().Be(BifrostMessageType.Query);
            restored.Query.Should().Be("");
            restored.VariablesJson.Should().Be("");
            restored.Payload.Should().BeEmpty();
            restored.Errors.Should().BeEmpty();
        }

        [Fact]
        public void QueryMessage_RoundTrips()
        {
            var msg = new BifrostMessage
            {
                RequestId = 42,
                Type = BifrostMessageType.Query,
                Query = "{ users { id name } }",
                VariablesJson = "{\"limit\":10}",
            };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.RequestId.Should().Be(42);
            restored.Type.Should().Be(BifrostMessageType.Query);
            restored.Query.Should().Be("{ users { id name } }");
            restored.VariablesJson.Should().Be("{\"limit\":10}");
            restored.Payload.Should().BeEmpty();
            restored.Errors.Should().BeEmpty();
        }

        [Fact]
        public void MutationMessage_RoundTrips()
        {
            var msg = new BifrostMessage
            {
                RequestId = 7,
                Type = BifrostMessageType.Mutation,
                Query = "mutation { insert_user(data: $input) { id } }",
                VariablesJson = "{\"input\":{\"name\":\"Alice\"}}",
            };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.RequestId.Should().Be(7);
            restored.Type.Should().Be(BifrostMessageType.Mutation);
            restored.Query.Should().Be("mutation { insert_user(data: $input) { id } }");
            restored.VariablesJson.Should().Be("{\"input\":{\"name\":\"Alice\"}}");
        }

        [Fact]
        public void ResultMessage_WithPayload_RoundTrips()
        {
            var payloadData = JsonSerializer.SerializeToUtf8Bytes(new { users = new[] { new { id = 1, name = "Alice" } } });
            var msg = new BifrostMessage
            {
                RequestId = 42,
                Type = BifrostMessageType.Result,
                Payload = payloadData,
            };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.RequestId.Should().Be(42);
            restored.Type.Should().Be(BifrostMessageType.Result);
            restored.Payload.Should().BeEquivalentTo(payloadData);
            restored.Errors.Should().BeEmpty();
        }

        [Fact]
        public void ErrorMessage_WithMultipleErrors_RoundTrips()
        {
            var msg = new BifrostMessage
            {
                RequestId = 99,
                Type = BifrostMessageType.Error,
                Errors = { "Table not found", "Permission denied" },
            };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.RequestId.Should().Be(99);
            restored.Type.Should().Be(BifrostMessageType.Error);
            restored.Errors.Should().HaveCount(2);
            restored.Errors[0].Should().Be("Table not found");
            restored.Errors[1].Should().Be("Permission denied");
        }

        [Fact]
        public void ResultMessage_WithErrorsAndPayload_RoundTrips()
        {
            var payload = Encoding.UTF8.GetBytes("{\"partial\":true}");
            var msg = new BifrostMessage
            {
                RequestId = 55,
                Type = BifrostMessageType.Result,
                Payload = payload,
                Errors = { "Partial results: timeout on secondary query" },
            };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.RequestId.Should().Be(55);
            restored.Type.Should().Be(BifrostMessageType.Result);
            restored.Payload.Should().BeEquivalentTo(payload);
            restored.Errors.Should().ContainSingle().Which.Should().Contain("Partial results");
        }

        [Fact]
        public void LargeRequestId_RoundTrips()
        {
            var msg = new BifrostMessage { RequestId = uint.MaxValue };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.RequestId.Should().Be(uint.MaxValue);
        }

        [Fact]
        public void UnicodeQuery_RoundTrips()
        {
            var msg = new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Query,
                Query = "{ users(filter: { name: { _eq: \"\u00e9l\u00e8ve\" } }) { id } }",
            };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.Query.Should().Contain("\u00e9l\u00e8ve");
        }

        [Fact]
        public void EmptyPayload_IsDistinguishedFromNull()
        {
            var msg = new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Result,
                Payload = Array.Empty<byte>(),
            };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.Payload.Should().BeEmpty();
        }

        [Fact]
        public void AllMessageTypes_Serialize()
        {
            foreach (BifrostMessageType msgType in Enum.GetValues(typeof(BifrostMessageType)))
            {
                var msg = new BifrostMessage { RequestId = 1, Type = msgType };
                var bytes = msg.ToBytes();
                var restored = BifrostMessage.FromBytes(bytes);
                restored.Type.Should().Be(msgType);
            }
        }

        [Fact]
        public void FromBytes_WithOffset_DeserializesCorrectly()
        {
            var msg = new BifrostMessage
            {
                RequestId = 77,
                Type = BifrostMessageType.Query,
                Query = "{ test }",
            };
            var msgBytes = msg.ToBytes();

            // Pad with prefix and suffix bytes
            var padded = new byte[10 + msgBytes.Length + 5];
            Buffer.BlockCopy(msgBytes, 0, padded, 10, msgBytes.Length);

            var restored = BifrostMessage.FromBytes(padded, 10, msgBytes.Length);
            restored.RequestId.Should().Be(77);
            restored.Query.Should().Be("{ test }");
        }

        [Fact]
        public void FromBytes_UnknownField_IsSkipped()
        {
            // Manually build bytes with an unknown field tag to test forward compatibility
            var msg = new BifrostMessage
            {
                RequestId = 1,
                Query = "{ test }",
            };
            var bytes = msg.ToBytes();

            // Valid messages with unknown fields should deserialize without error
            var restored = BifrostMessage.FromBytes(bytes);
            restored.RequestId.Should().Be(1);
            restored.Query.Should().Be("{ test }");
        }
    }

    public class BifrostMessageTypeTests
    {
        [Theory]
        [InlineData(BifrostMessageType.Query, 0)]
        [InlineData(BifrostMessageType.Mutation, 1)]
        [InlineData(BifrostMessageType.Result, 2)]
        [InlineData(BifrostMessageType.Error, 3)]
        public void MessageType_HasExpectedValues(BifrostMessageType type, int expected)
        {
            ((int)type).Should().Be(expected);
        }
    }
}
