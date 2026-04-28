using System.Text;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Resolvers
{
    public class BifrostRequestTests
    {
        [Fact]
        public void DefaultBifrostRequest_HasEmptyQuery()
        {
            var request = new BifrostRequest();

            request.Query.Should().Be("");
            request.OperationName.Should().BeNull();
            request.Variables.Should().BeNull();
            request.Extensions.Should().BeNull();
            request.UserContext.Should().NotBeNull().And.BeEmpty();
            request.RequestServices.Should().BeNull();
            request.CancellationToken.Should().Be(CancellationToken.None);
        }

        [Fact]
        public void BifrostRequest_WithAllProperties_RoundTrips()
        {
            var variables = new Dictionary<string, object?> { { "id", 42 } };
            var extensions = new Dictionary<string, object?> { { "hash", "abc" } };
            var userContext = new Dictionary<string, object?> { { "tenant_id", "t1" } };
            using var cts = new CancellationTokenSource();

            var request = new BifrostRequest
            {
                Query = "{ users { id name } }",
                OperationName = "GetUsers",
                Variables = variables,
                Extensions = extensions,
                UserContext = userContext,
                CancellationToken = cts.Token,
            };

            request.Query.Should().Be("{ users { id name } }");
            request.OperationName.Should().Be("GetUsers");
            request.Variables.Should().ContainKey("id").WhoseValue.Should().Be(42);
            request.Extensions.Should().ContainKey("hash").WhoseValue.Should().Be("abc");
            request.UserContext.Should().ContainKey("tenant_id").WhoseValue.Should().Be("t1");
            request.CancellationToken.Should().Be(cts.Token);
        }
    }

    public class BifrostResultTests
    {
        [Fact]
        public void DefaultBifrostResult_IsSuccess()
        {
            var result = new BifrostResult();

            result.Data.Should().BeNull();
            result.Errors.Should().BeEmpty();
            result.IsSuccess.Should().BeTrue();
        }

        [Fact]
        public void BifrostResult_WithData_IsSuccess()
        {
            var data = new Dictionary<string, object?> { { "users", new[] { "alice" } } };
            var result = new BifrostResult { Data = data };

            result.Data.Should().BeSameAs(data);
            result.IsSuccess.Should().BeTrue();
        }

        [Fact]
        public void BifrostResult_WithErrors_IsNotSuccess()
        {
            var errors = new[] { new BifrostResultError { Message = "boom" } };
            var result = new BifrostResult { Errors = errors };

            result.IsSuccess.Should().BeFalse();
            result.Errors.Should().HaveCount(1);
            result.Errors[0].Message.Should().Be("boom");
        }
    }

    public class BifrostResultErrorTests
    {
        [Fact]
        public void DefaultError_HasEmptyMessage()
        {
            var error = new BifrostResultError();

            error.Message.Should().Be("");
            error.Path.Should().BeNull();
            error.Extensions.Should().BeNull();
        }

        [Fact]
        public void Error_WithAllProperties_RoundTrips()
        {
            var path = new List<object> { "users", 0, "name" };
            var extensions = new Dictionary<string, object?> { { "code", "NOT_FOUND" } };

            var error = new BifrostResultError
            {
                Message = "User not found",
                Path = path,
                Extensions = extensions,
            };

            error.Message.Should().Be("User not found");
            error.Path.Should().BeEquivalentTo(path);
            error.Extensions.Should().ContainKey("code").WhoseValue.Should().Be("NOT_FOUND");
        }
    }

    /// <summary>
    /// Tests that verify the IProtocolFrontend contract is implementable
    /// with minimal code. Uses a trivial echo frontend to validate the interface.
    /// </summary>
    public class ProtocolFrontendContractTests
    {
        /// <summary>
        /// Minimal IProtocolFrontend implementation for testing.
        /// Reads the body as a UTF-8 string and echoes it back as the BifrostResult data.
        /// This demonstrates the &lt;200 lines success metric.
        /// </summary>
        private sealed class EchoFrontend : IProtocolFrontend
        {
            public string ProtocolName => "echo";
            public string ContentType => "text/plain";
            public string ResponseContentType => "text/plain";

            public async ValueTask<BifrostRequest?> ParseAsync(Stream body, CancellationToken cancellationToken)
            {
                using var reader = new StreamReader(body, Encoding.UTF8, leaveOpen: true);
                var text = await reader.ReadToEndAsync(cancellationToken);
                if (string.IsNullOrEmpty(text))
                    return null;
                return new BifrostRequest { Query = text };
            }

            public async ValueTask SerializeAsync(Stream output, BifrostResult result, CancellationToken cancellationToken)
            {
                var text = result.IsSuccess
                    ? $"OK: {result.Data}"
                    : $"ERR: {result.Errors[0].Message}";
                var bytes = Encoding.UTF8.GetBytes(text);
                await output.WriteAsync(bytes, cancellationToken);
            }
        }

        [Fact]
        public async Task EchoFrontend_ParseAsync_ReturnsRequest()
        {
            var frontend = new EchoFrontend();
            var body = new MemoryStream(Encoding.UTF8.GetBytes("hello world"));

            var request = await frontend.ParseAsync(body, CancellationToken.None);

            request.Should().NotBeNull();
            request!.Query.Should().Be("hello world");
        }

        [Fact]
        public async Task EchoFrontend_ParseAsync_EmptyBody_ReturnsNull()
        {
            var frontend = new EchoFrontend();
            var body = new MemoryStream(Array.Empty<byte>());

            var request = await frontend.ParseAsync(body, CancellationToken.None);

            request.Should().BeNull();
        }

        [Fact]
        public async Task EchoFrontend_SerializeAsync_SuccessResult()
        {
            var frontend = new EchoFrontend();
            var result = new BifrostResult { Data = "test-data" };
            var output = new MemoryStream();

            await frontend.SerializeAsync(output, result, CancellationToken.None);

            output.Position = 0;
            var text = new StreamReader(output).ReadToEnd();
            text.Should().Be("OK: test-data");
        }

        [Fact]
        public async Task EchoFrontend_SerializeAsync_ErrorResult()
        {
            var frontend = new EchoFrontend();
            var result = new BifrostResult
            {
                Errors = new[] { new BifrostResultError { Message = "something broke" } }
            };
            var output = new MemoryStream();

            await frontend.SerializeAsync(output, result, CancellationToken.None);

            output.Position = 0;
            var text = new StreamReader(output).ReadToEnd();
            text.Should().Be("ERR: something broke");
        }

        [Fact]
        public void EchoFrontend_Properties_AreCorrect()
        {
            var frontend = new EchoFrontend();

            frontend.ProtocolName.Should().Be("echo");
            frontend.ContentType.Should().Be("text/plain");
            frontend.ResponseContentType.Should().Be("text/plain");
        }

    }
}
