using BifrostQL.Server.S3;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// Unit coverage for the <c>x-amz-copy-source</c> strict decoder. A well-formed
    /// reference splits into (bucket, key) with the key still key-map-escaped for the
    /// seam; every malformed, rooted, traversal, or double-encoded reference is a clean
    /// protocol error rather than a downstream surprise.
    /// </summary>
    public sealed class S3CopySourceTests
    {
        [Theory]
        [InlineData("/assets/data/1", "assets", "data/1")]
        [InlineData("assets/data/1", "assets", "data/1")]        // leading slash optional
        [InlineData("/parts/image/us/widget", "parts", "image/us/widget")]  // composite key
        public void Parses_bucket_and_key(string header, string bucket, string key)
        {
            var (b, k) = S3CopySource.Parse(header);
            b.Should().Be(bucket);
            k.Should().Be(key);
        }

        [Fact]
        public void Single_url_decode_undoes_the_wire_layer()
        {
            // The S3 client URL-encodes the reference; one decode reveals the canonical form.
            var (b, k) = S3CopySource.Parse("/my-bucket/col%2Fkey/7");
            b.Should().Be("my-bucket");
            k.Should().Be("col/key/7");
        }

        [Theory]
        [InlineData("")]                          // empty
        [InlineData("/")]                          // rooted, no bucket
        [InlineData("//assets/data/1")]            // rooted (double leading slash)
        [InlineData("assets")]                     // no key component
        [InlineData("/assets/")]                   // empty key
        [InlineData("/assets/data/..")]            // literal traversal segment
        [InlineData("/assets/data/%2E%2E")]        // single-encoded traversal
        [InlineData("/assets/data/%252E%252E")]    // double-encoded traversal
        [InlineData("/assets/../secrets/1")]       // traversal in the bucket-adjacent segment
        public void Rejects_malformed_or_traversal_as_invalid_argument(string header)
        {
            var act = () => S3CopySource.Parse(header);
            act.Should().Throw<S3ProtocolException>()
                .Which.Code.Should().Be("InvalidArgument");
        }

        [Fact]
        public void Rejects_versioned_source_as_not_implemented()
        {
            var act = () => S3CopySource.Parse("/assets/data/1?versionId=abc");
            act.Should().Throw<S3ProtocolException>()
                .Which.Code.Should().Be("NotImplemented");
        }
    }
}
