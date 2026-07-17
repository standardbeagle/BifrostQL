using BifrostQL.Server.Auth;
using BifrostQL.Server.S3;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// Interop conformance for the SigV4 verifier against AWS's OWN published S3 signature
    /// examples. Every other SigV4 test in this suite signs with <see cref="S3TestSigner"/>,
    /// which shares the verifier's canonicalization — so a passing signature there proves only
    /// that the signer and verifier agree with each other, never that either agrees with AWS.
    /// These tests close that gap: they feed the verifier a request whose expected signature was
    /// computed by AWS's reference implementation and published verbatim in the AWS
    /// documentation. Accepting it proves the canonical-request construction (URI encoding,
    /// signed-header selection, credential scope, payload-hash placement) matches AWS byte for
    /// byte — real cross-implementation interoperability, not internal self-consistency.
    ///
    /// <para>Vectors are from AWS's "Signature Calculations for the Authorization Header:
    /// Transferring Payload in a Single Chunk (AWS Signature Version 4)" examples
    /// (credential <c>AKIAIOSFODNN7EXAMPLE</c> / secret
    /// <c>wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY</c>, region <c>us-east-1</c>, service
    /// <c>s3</c>, signing time <c>20130524T000000Z</c>). The signing time is 2013, so the clock
    /// is pinned to that instant (offline, no wall-clock dependency) to pass the skew window.</para>
    /// </summary>
    public sealed class S3SigV4PublishedVectorTests
    {
        // AWS published example credentials (identical across both single-chunk examples).
        private const string AwsAccessKeyId = "AKIAIOSFODNN7EXAMPLE";
        private const string AwsSecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
        private const string AwsHost = "examplebucket.s3.amazonaws.com";
        private const string AwsAmzDate = "20130524T000000Z";
        private const string EmptyPayloadHash =
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        // The AWS published request time; the verifier's skew window is measured against this.
        private static readonly DateTimeOffset SigningInstant =
            new(2013, 05, 24, 00, 00, 00, TimeSpan.Zero);

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private static S3SigV4Verifier Verifier()
        {
            var opts = new S3Options { Region = "us-east-1" };
            var store = new FakeS3AccessKeyStore()
                .Add(AwsAccessKeyId, AwsSecretKey, S3TestSigner.Principal("aws-example-user"));
            return new S3SigV4Verifier(store, BifrostAuthContextFactory.Instance, opts, new FixedClock(SigningInstant));
        }

        private static DefaultHttpContext NewRequest(string method, string path)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = method;
            ctx.Request.Scheme = "https";
            ctx.Request.Host = new HostString(AwsHost);
            ctx.Request.Path = path;
            ctx.Request.Headers["Host"] = AwsHost;
            ctx.Request.Headers["x-amz-date"] = AwsAmzDate;
            return ctx;
        }

        [Fact]
        public async Task Aws_published_get_object_vector_is_accepted()
        {
            // AWS "GET Object" example: GET /test.txt with a byte range, empty payload.
            // SignedHeaders = host;range;x-amz-content-sha256;x-amz-date.
            const string publishedSignature =
                "f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41";

            var ctx = NewRequest("GET", "/test.txt");
            ctx.Request.Headers["Range"] = "bytes=0-9";
            ctx.Request.Headers["x-amz-content-sha256"] = EmptyPayloadHash;
            ctx.Request.Headers["Authorization"] =
                $"{S3SigV4.Algorithm} Credential={AwsAccessKeyId}/20130524/us-east-1/s3/aws4_request, " +
                "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, " +
                $"Signature={publishedSignature}";

            var identity = await Verifier().VerifyAsync(ctx.Request, CancellationToken.None);

            identity.Should().NotBeEmpty(
                "the verifier's canonical form matches AWS's, so the published GET signature validates");
        }

        [Fact]
        public async Task Aws_published_put_object_vector_is_accepted()
        {
            // AWS "PUT Object" example: PUT /test$file.text with a non-empty payload, a Date
            // header, and a storage-class header, all signed. This exercises the write verb,
            // a URI-encoded key ('$' -> %24 in the canonical URI), a non-empty payload hash,
            // and a five-header signed set. SignedHeaders =
            // date;host;x-amz-content-sha256;x-amz-date;x-amz-storage-class.
            const string publishedSignature =
                "98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd";
            const string payloadHash =
                "44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072"; // SHA-256("Welcome to Amazon S3.")

            var ctx = NewRequest("PUT", "/test$file.text");
            ctx.Request.Headers["Date"] = "Fri, 24 May 2013 00:00:00 GMT";
            ctx.Request.Headers["x-amz-storage-class"] = "REDUCED_REDUNDANCY";
            ctx.Request.Headers["x-amz-content-sha256"] = payloadHash;
            ctx.Request.Headers["Authorization"] =
                $"{S3SigV4.Algorithm} Credential={AwsAccessKeyId}/20130524/us-east-1/s3/aws4_request, " +
                "SignedHeaders=date;host;x-amz-content-sha256;x-amz-date;x-amz-storage-class, " +
                $"Signature={publishedSignature}";

            var identity = await Verifier().VerifyAsync(ctx.Request, CancellationToken.None);

            identity.Should().NotBeEmpty(
                "the verifier's canonical form matches AWS's for a write verb with an encoded key and non-empty payload");
        }

        [Fact]
        public async Task Tampering_a_published_vector_signature_is_rejected()
        {
            // Flip the final hex digit of AWS's published GET signature: the same request that
            // validates above must now be a genuine mismatch, proving the acceptance was the
            // signature agreeing, not the verifier ignoring it.
            const string tamperedSignature =
                "f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb42";

            var ctx = NewRequest("GET", "/test.txt");
            ctx.Request.Headers["Range"] = "bytes=0-9";
            ctx.Request.Headers["x-amz-content-sha256"] = EmptyPayloadHash;
            ctx.Request.Headers["Authorization"] =
                $"{S3SigV4.Algorithm} Credential={AwsAccessKeyId}/20130524/us-east-1/s3/aws4_request, " +
                "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, " +
                $"Signature={tamperedSignature}";

            var ex = await Assert.ThrowsAsync<S3ProtocolException>(
                () => Verifier().VerifyAsync(ctx.Request, CancellationToken.None));
            ex.Code.Should().Be("SignatureDoesNotMatch");
        }
    }
}
