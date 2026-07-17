using System.Xml.Linq;
using BifrostQL.Server.S3;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// Middleware-level behavior: deterministic S3 XML error envelopes, the authenticated 501
    /// for the not-yet-implemented data path, request-size limits enforced before buffering,
    /// and the guarantee that no internal/credential detail leaks onto the wire.
    /// </summary>
    public sealed class S3MiddlewareTests
    {
        private static readonly DateTimeOffset SignTime = new(2026, 07, 16, 12, 00, 00, TimeSpan.Zero);

        private sealed class TestClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private static (S3Middleware Middleware, bool[] NextCalled) Build(
            S3Options? options = null, IS3AccessKeyStore? store = null, DateTimeOffset? now = null)
        {
            var opts = options ?? new S3Options { Region = S3TestSigner.Region };
            var keyStore = store ?? new FakeS3AccessKeyStore().Add(
                S3TestSigner.AccessKeyId, S3TestSigner.Secret, S3TestSigner.Principal());
            var verifier = new S3SigV4Verifier(keyStore, BifrostAuthContextFactory.Instance, opts,
                new TestClock(now ?? SignTime));
            var nextCalled = new[] { false };
            RequestDelegate next = _ => { nextCalled[0] = true; return Task.CompletedTask; };
            // These middleware tests exercise auth/limits/501 paths only; none reaches a
            // list operation, so the lister's read seam is never invoked.
            var listing = new S3Listing(new UnusedReads(), opts);
            return (new S3Middleware(next, opts, verifier, listing, NullLogger<S3Middleware>.Instance), nextCalled);
        }

        private static async Task<(int Status, string Body, XElement Xml)> Run(
            S3Middleware middleware, DefaultHttpContext ctx)
        {
            ctx.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(ctx);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            return (ctx.Response.StatusCode, body, XElement.Parse(body));
        }

        [Fact]
        public async Task Authenticated_data_request_returns_not_implemented_501()
        {
            var (middleware, nextCalled) = Build();
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime);

            var (status, _, xml) = await Run(middleware, ctx);

            status.Should().Be(501);
            xml.Element("Code")!.Value.Should().Be("NotImplemented");
            nextCalled[0].Should().BeFalse("the S3 endpoint terminates the request");
        }

        [Fact]
        public async Task Auth_failure_returns_deterministic_xml_error_with_request_id()
        {
            var (middleware, _) = Build();
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime);
            var auth = ctx.Request.Headers["Authorization"].ToString();
            ctx.Request.Headers["Authorization"] = auth[..^8] + "deadbeef";

            var (status, body, xml) = await Run(middleware, ctx);

            status.Should().Be(403);
            xml.Name.LocalName.Should().Be("Error");
            xml.Element("Code")!.Value.Should().Be("SignatureDoesNotMatch");
            xml.Element("RequestId")!.Value.Should().NotBeNullOrEmpty();
            ctx.Response.Headers["x-amz-request-id"].ToString().Should().Be(xml.Element("RequestId")!.Value);
            ctx.Response.ContentType.Should().Contain("xml");

            // The wire must never carry the secret, the access key id, or canonical strings.
            body.Should().NotContain(S3TestSigner.Secret);
            body.Should().NotContain(S3TestSigner.AccessKeyId);
            body.Should().NotContain("AWS4-HMAC-SHA256");
        }

        [Fact]
        public async Task Oversized_body_is_rejected_from_content_length_without_buffering()
        {
            var opts = new S3Options { Region = S3TestSigner.Region, MaxBodyBytes = 1024 };
            var (middleware, _) = Build(opts);
            var ctx = S3TestSigner.BuildHeaderSigned(method: "PUT", signTime: SignTime);
            // Declare an oversized payload but attach a body stream that throws if read, proving
            // the limit is enforced from Content-Length before anything is buffered.
            ctx.Request.ContentLength = opts.MaxBodyBytes + 1;
            ctx.Request.Body = new ThrowingStream();

            var (status, _, xml) = await Run(middleware, ctx);

            status.Should().Be(400);
            xml.Element("Code")!.Value.Should().Be("EntityTooLarge");
        }

        [Fact]
        public async Task Oversized_url_is_rejected()
        {
            var opts = new S3Options { Region = S3TestSigner.Region, MaxUrlLength = 16 };
            var (middleware, _) = Build(opts);
            var ctx = S3TestSigner.BuildHeaderSigned(path: "/bucket/" + new string('k', 64), signTime: SignTime);

            var (status, _, xml) = await Run(middleware, ctx);

            status.Should().Be(400);
            xml.Element("Code")!.Value.Should().Be("InvalidArgument");
        }

        [Fact]
        public async Task Cancelled_request_writes_no_body()
        {
            var (middleware, _) = Build();
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime);
            ctx.RequestAborted = new CancellationToken(canceled: true);
            ctx.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(ctx);

            ctx.Response.Body.Length.Should().Be(0, "a client abort short-circuits before any write");
        }

        /// <summary>A read seam that must never be called on these auth/limit/501 paths.</summary>
        private sealed class UnusedReads : BifrostQL.Core.Resolvers.IQueryIntentExecutor
        {
            public Task<BifrostQL.Core.Model.IDbModel> GetModelAsync(string? endpoint = null)
                => throw new InvalidOperationException("read seam must not be invoked");

            public Task<BifrostQL.Core.Resolvers.QueryIntentResult> ExecuteAsync(
                BifrostQL.Core.Resolvers.QueryIntent intent, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("read seam must not be invoked");
        }

        private sealed class ThrowingStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("body must not be buffered");
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw new InvalidOperationException("body must not be buffered");
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
