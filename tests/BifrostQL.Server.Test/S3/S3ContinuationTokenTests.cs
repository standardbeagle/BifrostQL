using System.Text;
using BifrostQL.Server.S3;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// The opaque continuation token's integrity contract: a token round-trips only
    /// against the exact binding it was issued for. Any tamper — flipped MAC, altered
    /// position, cross-bucket/prefix/delimiter/page-size/identity replay — fails closed
    /// as a curated <see cref="S3ProtocolException"/>, never a silent "start over".
    /// </summary>
    public sealed class S3ContinuationTokenTests
    {
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("unit-test-secret-key-0123456789ab");
        private static readonly DateTimeOffset Now = new(2026, 07, 16, 12, 00, 00, TimeSpan.Zero);
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

        private static S3ListBinding Binding(
            string bucket = "documents", string prefix = "", string delimiter = "",
            int maxKeys = 1000, string identity = "id-a")
            => new(bucket, prefix, delimiter, maxKeys, identity);

        [Fact]
        public void Round_trips_position_under_matching_binding()
        {
            var token = S3ContinuationToken.Issue("body/5", Now, Binding(), Secret);

            var position = S3ContinuationToken.Decode(token, Binding(), Secret, Now, Ttl);

            position.Should().Be("body/5");
        }

        [Fact]
        public void Rejects_a_token_whose_position_was_tampered()
        {
            var token = S3ContinuationToken.Issue("body/5", Now, Binding(), Secret);
            // Corrupt the base64url position segment (before the '.') so the MAC no
            // longer covers the transmitted position.
            var dot = token.IndexOf('.');
            var tampered = "Zm9yZ2Vk" + token[dot..]; // "forged" position, original MAC

            var act = () => S3ContinuationToken.Decode(tampered, Binding(), Secret, Now, Ttl);

            act.Should().Throw<S3ProtocolException>().Which.Code.Should().Be("InvalidArgument");
        }

        [Fact]
        public void Rejects_a_token_reused_against_a_different_bucket()
        {
            var token = S3ContinuationToken.Issue("body/5", Now, Binding(bucket: "documents"), Secret);

            var act = () => S3ContinuationToken.Decode(token, Binding(bucket: "invoices"), Secret, Now, Ttl);

            act.Should().Throw<S3ProtocolException>();
        }

        [Theory]
        [InlineData("prefix", "other", "", "", 1000, 1000, "id", "id")]
        [InlineData("", "", "/", "|", 1000, 1000, "id", "id")]
        [InlineData("", "", "", "", 1000, 500, "id", "id")]
        [InlineData("", "", "", "", 1000, 1000, "id-a", "id-b")]
        public void Rejects_a_token_replayed_against_any_changed_binding_field(
            string issuePrefix, string decodePrefix, string issueDelim, string decodeDelim,
            int issueMax, int decodeMax, string issueId, string decodeId)
        {
            var token = S3ContinuationToken.Issue(
                "body/5", Now, new S3ListBinding("b", issuePrefix, issueDelim, issueMax, issueId), Secret);

            var act = () => S3ContinuationToken.Decode(
                token, new S3ListBinding("b", decodePrefix, decodeDelim, decodeMax, decodeId), Secret, Now, Ttl);

            act.Should().Throw<S3ProtocolException>();
        }

        [Fact]
        public void A_delimiter_cannot_be_smuggled_into_a_prefix_to_forge_a_match()
        {
            // Length-prefixed canonicalization makes (prefix="a/", delimiter="") distinct
            // from (prefix="a", delimiter="/") — otherwise a concatenated "a/" could match.
            var token = S3ContinuationToken.Issue("k", Now, new S3ListBinding("b", "a/", "", 10, "id"), Secret);

            var act = () => S3ContinuationToken.Decode(token, new S3ListBinding("b", "a", "/", 10, "id"), Secret, Now, Ttl);

            act.Should().Throw<S3ProtocolException>();
        }

        [Theory]
        [InlineData("")]
        [InlineData("no-dot-separator")]
        [InlineData("....")]
        [InlineData("not!base64.also!bad")]
        public void Rejects_a_malformed_token(string token)
        {
            var act = () => S3ContinuationToken.Decode(token, Binding(), Secret, Now, Ttl);

            act.Should().Throw<S3ProtocolException>().Which.Code.Should().Be("InvalidArgument");
        }

        // ---- expiry --------------------------------------------------------------------------

        [Theory]
        [InlineData(0)]
        [InlineData(15 * 60)]      // exactly at the TTL
        [InlineData(-59)]          // another instance's clock slightly ahead
        public void Accepts_a_token_within_its_ttl(int ageSeconds)
        {
            var token = S3ContinuationToken.Issue("body/5", Now, Binding(), Secret);

            S3ContinuationToken.Decode(token, Binding(), Secret, Now.AddSeconds(ageSeconds), Ttl)
                .Should().Be("body/5");
        }

        [Theory]
        [InlineData(15 * 60 + 1)]  // one second past the TTL
        [InlineData(24 * 60 * 60)] // a day-old captured token
        [InlineData(-61 * 60)]     // issued an hour in the future
        public void Rejects_a_token_outside_its_ttl_like_a_forged_one(int ageSeconds)
        {
            var token = S3ContinuationToken.Issue("body/5", Now, Binding(), Secret);

            var act = () => S3ContinuationToken.Decode(token, Binding(), Secret, Now.AddSeconds(ageSeconds), Ttl);

            act.Should().Throw<S3ProtocolException>().Which.Message
                .Should().Be("The continuation token provided is not valid.");
        }

        [Fact]
        public void The_issue_time_is_covered_by_the_mac()
        {
            // Re-stamping an old token's issue time to now, keeping its MAC, must fail: the time
            // is inside the MAC, so a client cannot extend a token.
            var stale = S3ContinuationToken.Issue("body/5", Now.AddDays(-1), Binding(), Secret);
            var dot = stale.IndexOf('.');
            var payload = Encoding.UTF8.GetString(ProtocolBase64Url.Decode(stale[..dot]));
            var restamped = Now.ToUnixTimeSeconds() + payload[payload.IndexOf('|')..];
            var forged = ProtocolBase64Url.Encode(Encoding.UTF8.GetBytes(restamped)) + stale[dot..];

            var act = () => S3ContinuationToken.Decode(forged, Binding(), Secret, Now, Ttl);

            act.Should().Throw<S3ProtocolException>();
        }

        [Fact]
        public void A_position_containing_the_separator_round_trips()
        {
            // A common prefix built from a client delimiter may contain '|'.
            var token = S3ContinuationToken.Issue("a|b|", Now, Binding(delimiter: "|"), Secret);

            S3ContinuationToken.Decode(token, Binding(delimiter: "|"), Secret, Now, Ttl).Should().Be("a|b|");
        }

        [Fact]
        public void Rejects_a_token_signed_with_a_different_secret()
        {
            var token = S3ContinuationToken.Issue("body/5", Now, Binding(), Secret);
            var otherSecret = Encoding.UTF8.GetBytes("a-completely-different-secret-key");

            var act = () => S3ContinuationToken.Decode(token, Binding(), otherSecret, Now, Ttl);

            act.Should().Throw<S3ProtocolException>();
        }
    }
}
