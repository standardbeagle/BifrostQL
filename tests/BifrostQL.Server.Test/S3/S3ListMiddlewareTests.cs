using System.Xml.Linq;
using BifrostQL.Server.S3;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// Wire-level routing: a validly-signed GET at the service root lists buckets, a
    /// signed GET on a bucket with <c>list-type=2</c> lists objects, and both come back
    /// as the S3 XML documents AWS CLI / rclone parse. Drives the real
    /// <see cref="S3Middleware"/> over the real read pipeline (the SQLite-backed harness),
    /// so the response is produced by the actual auth → dispatch → listing path, not a stub.
    /// </summary>
    public sealed class S3ListMiddlewareTests : IAsyncLifetime
    {
        private static readonly DateTimeOffset SignTime = new(2026, 07, 16, 12, 00, 00, TimeSpan.Zero);
        private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

        private S3ListingRealDbHarness _harness = null!;

        private static readonly string[] MetadataRules =
        {
            "main.notes.content { file: json }",
        };

        private static string[] SeedSql() => new[]
        {
            "DROP TABLE IF EXISTS notes",
            "DROP TABLE IF EXISTS plain",
            "CREATE TABLE notes (slug TEXT PRIMARY KEY, content TEXT)",
            $"INSERT INTO notes(slug, content) VALUES ('alpha', '{Pointer()}'), ('beta', '{Pointer()}')",
            "CREATE TABLE plain (id INTEGER PRIMARY KEY, val TEXT)",
        };

        private static string Pointer() =>
            new BifrostQL.Core.Storage.FileMetadata
            {
                FileKey = "k",
                Size = 7,
                ETag = "abc",
                UploadedAt = new DateTime(2026, 07, 16, 8, 30, 00, DateTimeKind.Utc),
            }.ToJson().Replace("'", "''");

        public async Task InitializeAsync()
            => _harness = await S3ListingRealDbHarness.StartAsync(nameof(S3ListMiddlewareTests), MetadataRules, SeedSql());

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        private S3Middleware Build()
        {
            var opts = new S3Options
            {
                Region = S3TestSigner.Region,
                Endpoint = "/graphql",
                ContinuationTokenSecret = "wire-test-secret",
            };
            var keyStore = new FakeS3AccessKeyStore().Add(
                S3TestSigner.AccessKeyId, S3TestSigner.Secret, S3TestSigner.Principal());
            var verifier = new S3SigV4Verifier(keyStore, BifrostAuthContextFactory.Instance, opts, new FixedClock(SignTime));
            var listing = _harness.Listing(opts);
            var seam = _harness.Seam(options: opts);
            RequestDelegate next = _ => Task.CompletedTask;
            return new S3Middleware(next, opts, verifier, listing, seam, NullLogger<S3Middleware>.Instance);
        }

        private static async Task<(int Status, XElement Xml)> Run(S3Middleware middleware, DefaultHttpContext ctx)
        {
            ctx.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(ctx);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            return (ctx.Response.StatusCode, XElement.Parse(body));
        }

        [Fact]
        public async Task Get_service_root_lists_buckets()
        {
            var ctx = S3TestSigner.BuildHeaderSigned(path: "/", signTime: SignTime);

            var (status, xml) = await Run(Build(), ctx);

            status.Should().Be(200);
            xml.Name.LocalName.Should().Be("ListAllMyBucketsResult");
            xml.Descendants(Ns + "Bucket").Elements(Ns + "Name").Select(n => n.Value)
                .Should().Equal("notes"); // plain has no file column, so it is never a bucket
        }

        [Fact]
        public async Task Get_bucket_with_list_type_2_lists_objects()
        {
            var ctx = S3TestSigner.BuildHeaderSigned(path: "/notes", query: "?list-type=2", signTime: SignTime);

            var (status, xml) = await Run(Build(), ctx);

            status.Should().Be(200);
            xml.Name.LocalName.Should().Be("ListBucketResult");
            xml.Element(Ns + "Name")!.Value.Should().Be("notes");
            xml.Elements(Ns + "Contents").Elements(Ns + "Key").Select(k => k.Value)
                .Should().Equal("content/alpha", "content/beta");
            xml.Element(Ns + "KeyCount")!.Value.Should().Be("2");
            xml.Element(Ns + "IsTruncated")!.Value.Should().Be("false");
        }

        [Fact]
        public async Task Get_bucket_without_list_type_is_not_implemented()
        {
            // Legacy ListObjects v1 is a non-goal; only v2 (list-type=2) is served.
            var ctx = S3TestSigner.BuildHeaderSigned(path: "/notes", signTime: SignTime);

            var (status, xml) = await Run(Build(), ctx);

            status.Should().Be(501);
            xml.Element("Code")!.Value.Should().Be("NotImplemented");
        }

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
