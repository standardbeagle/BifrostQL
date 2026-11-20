using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Grpc;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Conditions the single gRPC funnel used to map to the wrong status: an anonymous call to an
    /// unregistered method answered UNIMPLEMENTED (so it told registered tables apart without a
    /// credential), malformed protobuf framing answered INTERNAL with an Error log, an update that
    /// sets nothing answered INTERNAL, and a write conflict answered INTERNAL.
    /// </summary>
    public sealed class GrpcStatusConditionTests : IAsyncLifetime
    {
        private static readonly string[] MetadataRules = { "*.widgets { grpc-write: enabled }" };

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS widgets",
            "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
            "INSERT INTO widgets(id, name) VALUES (1,'first'),(2,'second')",
        };

        private GrpcRealDbHarness _harness = null!;
        private GrpcWireTestClient _client = null!;

        public async Task InitializeAsync()
        {
            _harness = await GrpcRealDbHarness.StartAsync(
                nameof(GrpcStatusConditionTests), MetadataRules, SeedSql, new GrpcWireOptions { EnableWrites = true });
            _client = new GrpcWireTestClient(_harness.Invoker, _harness.Contract);
        }

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        private static Metadata User() => GrpcRealDbHarness.Identity("u1", roles: "member");

        [Fact]
        public async Task Anonymous_call_to_an_unregistered_method_is_unauthenticated_like_a_registered_one()
        {
            var registered = await StatusOf(() => _client.GetAsync("widgets", new Dictionary<string, object?> { ["id"] = 1L }, new Metadata()));
            var unregistered = await StatusOf(() => _client.RawUnaryAsync("GetNope", Array.Empty<byte>(), new Metadata()));

            registered.StatusCode.Should().Be(StatusCode.Unauthenticated);
            unregistered.StatusCode.Should().Be(StatusCode.Unauthenticated,
                "without a credential, an unregistered method must not be distinguishable from a registered one");
            unregistered.Detail.Should().Be(registered.Detail);

            // Control: an authenticated caller still gets the host's own UNIMPLEMENTED.
            var authenticated = await StatusOf(() => _client.RawUnaryAsync("GetNope", Array.Empty<byte>(), User()));
            authenticated.StatusCode.Should().Be(StatusCode.Unimplemented);
            authenticated.Detail.Should().Be(GrpcStatusMapper.UnimplementedMethodMessage);
        }

        [Theory]
        // A tag whose varint never terminates: ReadTag throws.
        [InlineData(new byte[] { 0x80 })]
        // Unknown field 99, length-delimited, declaring 5 bytes that never arrive: SkipLastField throws.
        [InlineData(new byte[] { 0x9A, 0x06, 0x05 })]
        public async Task Malformed_protobuf_framing_is_invalid_argument(byte[] payload)
        {
            var status = await StatusOf(() => _client.RawUnaryAsync("Getwidgets", payload, User()));

            status.StatusCode.Should().Be(StatusCode.InvalidArgument, "garbage framing is the caller's bad request");
            status.Detail.Should().Be("The request message is not a valid protobuf encoding.");
        }

        [Fact]
        public async Task Update_that_sets_no_column_is_invalid_argument()
        {
            var status = await StatusOf(() => _client.UpdateAsync("widgets", new Dictionary<string, object?> { ["id"] = 1L }, User()));

            status.StatusCode.Should().Be(StatusCode.InvalidArgument);
            status.Detail.Should().Be(MutationArgumentBinder.NoUpdateColumnsMessage);
        }

        [Fact]
        public void Write_conflict_is_aborted_with_fixed_text()
        {
            // The conflict text a store raises may name the table or constraint; the wire must not.
            var conflict = new BifrostExecutionError("Lost update on 'secret_table'.") { ErrorCode = BifrostExecutionError.ConflictCode };

            var status = GrpcStatusMapper.Map(conflict, new FakeServerCallContext(), NullLogger.Instance);

            status.StatusCode.Should().Be(StatusCode.Aborted);
            status.Detail.Should().Be(GrpcStatusMapper.ConflictMessage);
        }

        [Fact]
        public void Statement_timeout_is_deadline_exceeded_with_fixed_text()
        {
            var timeout = BifrostExecutionError.FromDatabaseException(
                new TimeoutException("Timeout during reading attempt on host db.internal"));

            var status = GrpcStatusMapper.Map(timeout, new FakeServerCallContext(), NullLogger.Instance);

            status.StatusCode.Should().Be(StatusCode.DeadlineExceeded);
            status.Detail.Should().Be(BifrostExecutionError.StatementTimeoutMessage);
        }

        [Fact]
        public void Access_denied_outside_a_get_answers_like_an_absent_method()
        {
            var denied = new BifrostExecutionError("Tenant context missing for secret_table.") { ErrorCode = BifrostExecutionError.AccessDeniedCode };

            var listed = GrpcStatusMapper.Map(denied, new FakeServerCallContext(), NullLogger.Instance);
            var got = GrpcStatusMapper.Map(denied, new FakeServerCallContext(), NullLogger.Instance, denialIsNotFound: true);

            listed.StatusCode.Should().Be(StatusCode.Unimplemented);
            listed.Detail.Should().Be(GrpcStatusMapper.UnimplementedMethodMessage);
            // Control: a row-addressed Get still hides the denial as a missing row.
            got.StatusCode.Should().Be(StatusCode.NotFound);
            got.Detail.Should().Be(GrpcStatusMapper.RowNotFoundMessage);
        }

        private static async Task<Status> StatusOf(Func<Task> call)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(call);
            return ex.Status;
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
