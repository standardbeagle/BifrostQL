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

        private static S3ListBinding Binding(
            string bucket = "documents", string prefix = "", string delimiter = "",
            int maxKeys = 1000, string identity = "id-a")
            => new(bucket, prefix, delimiter, maxKeys, identity);

        [Fact]
        public void Round_trips_position_under_matching_binding()
        {
            var token = S3ContinuationToken.Issue("body/5", Binding(), Secret);

            var position = S3ContinuationToken.Decode(token, Binding(), Secret);

            position.Should().Be("body/5");
        }

        [Fact]
        public void Rejects_a_token_whose_position_was_tampered()
        {
            var token = S3ContinuationToken.Issue("body/5", Binding(), Secret);
            // Corrupt the base64url position segment (before the '.') so the MAC no
            // longer covers the transmitted position.
            var dot = token.IndexOf('.');
            var tampered = "Zm9yZ2Vk" + token[dot..]; // "forged" position, original MAC

            var act = () => S3ContinuationToken.Decode(tampered, Binding(), Secret);

            act.Should().Throw<S3ProtocolException>().Which.Code.Should().Be("InvalidArgument");
        }

        [Fact]
        public void Rejects_a_token_reused_against_a_different_bucket()
        {
            var token = S3ContinuationToken.Issue("body/5", Binding(bucket: "documents"), Secret);

            var act = () => S3ContinuationToken.Decode(token, Binding(bucket: "invoices"), Secret);

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
                "body/5", new S3ListBinding("b", issuePrefix, issueDelim, issueMax, issueId), Secret);

            var act = () => S3ContinuationToken.Decode(
                token, new S3ListBinding("b", decodePrefix, decodeDelim, decodeMax, decodeId), Secret);

            act.Should().Throw<S3ProtocolException>();
        }

        [Fact]
        public void A_delimiter_cannot_be_smuggled_into_a_prefix_to_forge_a_match()
        {
            // Length-prefixed canonicalization makes (prefix="a/", delimiter="") distinct
            // from (prefix="a", delimiter="/") — otherwise a concatenated "a/" could match.
            var token = S3ContinuationToken.Issue("k", new S3ListBinding("b", "a/", "", 10, "id"), Secret);

            var act = () => S3ContinuationToken.Decode(token, new S3ListBinding("b", "a", "/", 10, "id"), Secret);

            act.Should().Throw<S3ProtocolException>();
        }

        [Theory]
        [InlineData("")]
        [InlineData("no-dot-separator")]
        [InlineData("....")]
        [InlineData("not!base64.also!bad")]
        public void Rejects_a_malformed_token(string token)
        {
            var act = () => S3ContinuationToken.Decode(token, Binding(), Secret);

            act.Should().Throw<S3ProtocolException>().Which.Code.Should().Be("InvalidArgument");
        }

        [Fact]
        public void Rejects_a_token_signed_with_a_different_secret()
        {
            var token = S3ContinuationToken.Issue("body/5", Binding(), Secret);
            var otherSecret = Encoding.UTF8.GetBytes("a-completely-different-secret-key");

            var act = () => S3ContinuationToken.Decode(token, Binding(), otherSecret);

            act.Should().Throw<S3ProtocolException>();
        }
    }
}
