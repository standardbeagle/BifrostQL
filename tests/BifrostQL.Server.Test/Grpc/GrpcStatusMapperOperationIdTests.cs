using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Grpc;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// The gRPC funnel maps every operation-id refusal by its code, not to INTERNAL. Only
    /// <c>OPERATION_ID_REQUIRED</c> is reachable end to end (a gRPC write cannot carry an id;
    /// the conformance kit covers it), so each code gets a fact here on the funnel itself. The
    /// thrown message deliberately names a table: the wire text must be the per-code constant,
    /// never the exception's own message (invariant 3).
    /// </summary>
    public sealed class GrpcStatusMapperOperationIdTests
    {
        [Theory]
        [InlineData("OPERATION_ID_REQUIRED", StatusCode.FailedPrecondition)]
        [InlineData("OPERATION_ID_UNSUPPORTED", StatusCode.FailedPrecondition)]
        [InlineData("OPERATION_ID_ANONYMOUS", StatusCode.FailedPrecondition)]
        [InlineData("OPERATION_ID_REUSED", StatusCode.AlreadyExists)]
        public void Operation_id_refusal_maps_to_its_named_status(string code, StatusCode expected)
        {
            var refusal = new BifrostExecutionError("refused on secret_table.secret_column") { ErrorCode = code };

            var status = GrpcStatusMapper.Map(refusal, new FakeServerCallContext(), NullLogger.Instance);

            status.StatusCode.Should().Be(expected);
            status.Detail.Should().StartWith(code + ": ");
            status.Detail.Should().NotContain("secret_table").And.NotContain("secret_column");
        }

        [Fact]
        public void Another_coded_error_still_maps_to_internal()
        {
            // Control: the mapping is by the operation-id codes, not by any ErrorCode.
            var other = new BifrostExecutionError("boom on secret_table") { ErrorCode = "SOME_OTHER_CODE" };

            var status = GrpcStatusMapper.Map(other, new FakeServerCallContext(), NullLogger.Instance);

            status.StatusCode.Should().Be(StatusCode.Internal);
            status.Detail.Should().Be("The gRPC request could not be completed.");
        }

        private sealed class FakeServerCallContext : ServerCallContext
        {
            protected override string MethodCore => "/test/Method";
            protected override string HostCore => "localhost";
            protected override string PeerCore => "ipv4:127.0.0.1:1";
            protected override DateTime DeadlineCore => DateTime.MaxValue;
            protected override Metadata RequestHeadersCore { get; } = new();
            protected override CancellationToken CancellationTokenCore => CancellationToken.None;
            protected override Metadata ResponseTrailersCore { get; } = new();
            protected override Status StatusCore { get; set; }
            protected override WriteOptions? WriteOptionsCore { get; set; }
            protected override AuthContext AuthContextCore { get; } = new(null, new Dictionary<string, List<AuthProperty>>());

            protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
                => throw new NotSupportedException();

            protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
        }
    }
}
