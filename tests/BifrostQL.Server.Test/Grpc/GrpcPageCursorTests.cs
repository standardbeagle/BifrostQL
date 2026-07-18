using System.Text;
using BifrostQL.Server.Grpc;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Criterion 3 at the cursor seam: the page token carries POSITION ONLY, integrity-protected by
    /// an HMAC over the (table, query shape, page size, identity) binding re-derived from the LIVE
    /// request. A token replayed against a different table/query/page-size/identity, tampered, or
    /// expired fails closed as a clean INVALID_ARGUMENT — never a silent "start from the top", never
    /// an unhandled fault (invariants 2, 3, 5).
    /// </summary>
    public class GrpcPageCursorTests
    {
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("cursor-test-secret-0123456789");
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

        private static GrpcPageBinding Binding(string table = "widgets", string? filter = null, int pageSize = 25, string identity = "alice")
            => new(table, GrpcPageCursor.QueryShapeHash(filter, null), pageSize, identity);

        [Fact]
        public void Round_trips_the_offset_for_the_same_binding()
        {
            var now = DateTimeOffset.UtcNow;
            var token = GrpcPageCursor.Issue(50, now, Binding(), Secret);

            GrpcPageCursor.Decode(token, Binding(), Secret, now, Ttl).Should().Be(50);
        }

        [Fact]
        public void A_token_replayed_across_tenants_fails_closed()
        {
            var now = DateTimeOffset.UtcNow;
            // Minted by identity A...
            var token = GrpcPageCursor.Issue(50, now, Binding(identity: "alice-fp"), Secret);

            // ...replayed by identity B: the re-derived binding differs, so the MAC cannot match.
            var act = () => GrpcPageCursor.Decode(token, Binding(identity: "bob-fp"), Secret, now, Ttl);
            act.Should().Throw<GrpcRequestException>()
                .Where(e => e.StatusCode == global::Grpc.Core.StatusCode.InvalidArgument);
        }

        [Fact]
        public void A_token_replayed_across_tables_or_queries_fails_closed()
        {
            var now = DateTimeOffset.UtcNow;
            var token = GrpcPageCursor.Issue(50, now, Binding(table: "widgets"), Secret);

            Invoking(() => GrpcPageCursor.Decode(token, Binding(table: "orders"), Secret, now, Ttl))
                .Should().Throw<GrpcRequestException>();
            Invoking(() => GrpcPageCursor.Decode(token, Binding(filter: "different"), Secret, now, Ttl))
                .Should().Throw<GrpcRequestException>();
            Invoking(() => GrpcPageCursor.Decode(token, Binding(pageSize: 99), Secret, now, Ttl))
                .Should().Throw<GrpcRequestException>();
        }

        [Fact]
        public void A_forged_or_tampered_token_fails_closed()
        {
            var now = DateTimeOffset.UtcNow;
            var token = GrpcPageCursor.Issue(50, now, Binding(), Secret);
            var tampered = token[..^2] + (token[^1] == 'A' ? "BB" : "AA");

            Invoking(() => GrpcPageCursor.Decode(tampered, Binding(), Secret, now, Ttl))
                .Should().Throw<GrpcRequestException>();
            // A completely made-up token also fails closed, not an unhandled fault.
            Invoking(() => GrpcPageCursor.Decode("garbage.token", Binding(), Secret, now, Ttl))
                .Should().Throw<GrpcRequestException>();
        }

        [Fact]
        public void An_expired_token_fails_closed_like_a_forgery()
        {
            var issued = DateTimeOffset.UtcNow.AddHours(-1);
            var token = GrpcPageCursor.Issue(50, issued, Binding(), Secret);

            Invoking(() => GrpcPageCursor.Decode(token, Binding(), Secret, DateTimeOffset.UtcNow, Ttl))
                .Should().Throw<GrpcRequestException>()
                .Where(e => e.StatusCode == global::Grpc.Core.StatusCode.InvalidArgument);
        }

        [Fact]
        public void A_wrong_secret_fails_closed()
        {
            var now = DateTimeOffset.UtcNow;
            var token = GrpcPageCursor.Issue(50, now, Binding(), Secret);

            Invoking(() => GrpcPageCursor.Decode(token, Binding(), Encoding.UTF8.GetBytes("other-secret"), now, Ttl))
                .Should().Throw<GrpcRequestException>();
        }

        private static Action Invoking(Action a) => a;
    }
}
