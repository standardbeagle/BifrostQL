using System.Security.Cryptography;
using System.Xml.Linq;
using BifrostQL.Core.Storage;
using BifrostQL.Server.S3;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// Compatibility fixtures that replay the REQUEST SHAPES the real <c>aws s3</c> CLI and
    /// <c>rclone</c> emit — the exact verb, path, query-parameter set, and characteristic
    /// headers each client puts on the wire — against the in-process S3 middleware over a
    /// SQLite-backed, local-storage fixture, and assert each round-trips to the correct S3
    /// response. The signature itself is (re)computed by <see cref="S3TestSigner"/> with a known
    /// key so the request authenticates offline; what is under test is that the middleware
    /// ACCEPTS the shape these clients produce and returns the XML/status they expect to parse.
    /// (That our canonicalization matches the one those clients sign with is proven separately
    /// by <see cref="S3SigV4PublishedVectorTests"/> against AWS's published vectors.)
    ///
    /// <para><b>Shape provenance.</b> Query/header sets mirror the documented and
    /// widely-observed wire behaviour of these clients:</para>
    /// <list type="bullet">
    /// <item><c>aws s3 ls s3://bucket</c> issues <c>GET /?list-type=2&amp;prefix=&amp;delimiter=%2F&amp;encoding-type=url</c>.</item>
    /// <item><c>aws s3 ls</c> (no bucket) issues <c>GET /</c> (ListAllMyBuckets).</item>
    /// <item><c>aws s3 cp s3://bucket/key -</c> (download) issues <c>GET /bucket/key</c>.</item>
    /// <item><c>aws s3 rm s3://bucket/key</c> issues <c>DELETE /bucket/key</c>.</item>
    /// <item><c>aws s3 cp file s3://bucket/key</c> (upload) defaults to an aws-chunked streaming
    /// body signed as <c>STREAMING-AWS4-HMAC-SHA256-PAYLOAD</c>. Streaming-chunk payload
    /// signatures are an explicit non-goal of this epic, so this is the documented compatibility
    /// BOUNDARY: aws-cli uploads answer a clean 501, not a corrupt store.</item>
    /// <item><c>rclone</c> list issues <c>GET /?list-type=2&amp;max-keys=1000&amp;prefix=&amp;delimiter=&amp;encoding-type=url</c>;
    /// copy download is a plain <c>GET</c>; copy UPLOAD sends a single-chunk PUT whose
    /// <c>x-amz-content-sha256</c> is the real payload hash (not aws-chunked) — which the
    /// middleware serves — and delete is a <c>DELETE</c>. rclone's upload path is thus the
    /// opposite side of the aws-cli boundary and round-trips fully.</item>
    /// </list>
    /// </summary>
    public sealed class S3ClientCompatibilityTests : IAsyncLifetime
    {
        private const string Endpoint = "/graphql";
        private static readonly DateTimeOffset SignTime = new(2026, 07, 16, 12, 00, 00, TimeSpan.Zero);
        private static readonly DateTime Uploaded = new(2026, 07, 15, 09, 15, 00, DateTimeKind.Utc);
        private static readonly byte[] ReportBytes = "quarterly report body"u8.ToArray();

        private string _tempDir = null!;
        private S3ListingRealDbHarness _harness = null!;

        private static readonly string[] MetadataRules =
        {
            "main.docs { tenant-filter: tenant_id }",
            "main.docs.blob { file: json }",
        };

        private string[] SeedSql() => new[]
        {
            "DROP TABLE IF EXISTS docs",
            "CREATE TABLE docs (name TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, blob TEXT)",
            "INSERT INTO docs(name, tenant_id, blob) VALUES\n" + string.Join(",\n", new[]
            {
                $"('report', 'tenant-a', '{Pointer("blobs/report", ReportBytes, "text/plain")}')",
                "('upload', 'tenant-a', NULL)", // fresh target for an upload round-trip
            }),
        };

        public async Task InitializeAsync()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "s3compat_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            await WriteBlob("blobs/report", ReportBytes);
            _harness = await S3ListingRealDbHarness.StartAsync(
                nameof(S3ClientCompatibilityTests), MetadataRules, SeedSql());
        }

        public async Task DisposeAsync()
        {
            await _harness.DisposeAsync();
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        // ---- aws s3 ---------------------------------------------------------------

        [Fact]
        public async Task Aws_cli_s3_ls_lists_buckets()
        {
            // `aws s3 ls` with no bucket -> ListAllMyBuckets.
            var ctx = S3TestSigner.BuildHeaderSigned(path: "/", signTime: SignTime, secret: Secret, accessKeyId: Key);

            var (status, xml) = await RunXml(ctx);

            status.Should().Be(200);
            xml.Name.LocalName.Should().Be("ListAllMyBucketsResult");
            xml.Descendants(Ns + "Name").Select(n => n.Value).Should().Contain("docs");
        }

        [Fact]
        public async Task Aws_cli_s3_ls_bucket_lists_objects()
        {
            // `aws s3 ls s3://docs` -> GET /docs?list-type=2&prefix=&delimiter=/&encoding-type=url
            var ctx = S3TestSigner.BuildHeaderSigned(
                path: "/docs", query: "?list-type=2&prefix=&delimiter=%2F&encoding-type=url",
                signTime: SignTime, secret: Secret, accessKeyId: Key);

            var (status, xml) = await RunXml(ctx);

            status.Should().Be(200);
            xml.Name.LocalName.Should().Be("ListBucketResult");
            // delimiter=/ rolls the object key "blob/report" into the "blob/" common prefix —
            // exactly the directory rollup aws-cli renders for `s3 ls`.
            xml.Elements(Ns + "CommonPrefixes").Elements(Ns + "Prefix").Select(p => p.Value)
                .Should().Contain("blob/");
        }

        [Fact]
        public async Task Aws_cli_s3_cp_download_returns_object_bytes()
        {
            // `aws s3 cp s3://docs/blob/report -` -> GET the object.
            var ctx = S3TestSigner.BuildHeaderSigned(
                path: "/docs/blob/report", signTime: SignTime, secret: Secret, accessKeyId: Key);

            var (status, body) = await RunBytes(ctx);

            status.Should().Be(200);
            body.Should().Equal(ReportBytes);
        }

        [Fact]
        public async Task Aws_cli_s3_rm_deletes_object()
        {
            // `aws s3 rm s3://docs/blob/report` -> DELETE.
            var ctx = S3TestSigner.BuildHeaderSigned(
                method: "DELETE", path: "/docs/blob/report",
                signTime: SignTime, secret: Secret, accessKeyId: Key);

            var (status, _) = await Run(ctx);
            status.Should().Be(204);

            // The object is gone on a subsequent read.
            var getCtx = S3TestSigner.BuildHeaderSigned(
                path: "/docs/blob/report", signTime: SignTime, secret: Secret, accessKeyId: Key);
            (await Run(getCtx)).Status.Should().Be(404);
        }

        [Fact]
        public async Task Aws_cli_s3_cp_upload_is_the_streaming_compatibility_boundary()
        {
            // `aws s3 cp file s3://docs/blob/upload.txt` defaults to an aws-chunked streaming body
            // declared as STREAMING-AWS4-HMAC-SHA256-PAYLOAD. Streaming-chunk signatures are a
            // non-goal, so the documented boundary is a clean 501 (not a corrupt/partial store).
            var body = "aws-cli streaming upload"u8.ToArray();
            var ctx = S3TestSigner.BuildHeaderSigned(
                method: "PUT", path: "/docs/blob/upload", signTime: SignTime,
                secret: Secret, accessKeyId: Key, payloadHash: "STREAMING-AWS4-HMAC-SHA256-PAYLOAD");
            ctx.Request.Body = new MemoryStream(body);
            ctx.Request.ContentLength = body.Length;
            ctx.Request.Headers.ContentType = "text/plain";

            var (status, xml) = await RunXml(ctx);

            status.Should().Be(501);
            xml.Element("Code")!.Value.Should().Be("NotImplemented");
        }

        // ---- rclone ---------------------------------------------------------------

        [Fact]
        public async Task Rclone_list_lists_objects()
        {
            // rclone issues GET /docs?list-type=2&max-keys=1000&prefix=&delimiter=&encoding-type=url.
            var ctx = S3TestSigner.BuildHeaderSigned(
                path: "/docs", query: "?list-type=2&max-keys=1000&prefix=&delimiter=&encoding-type=url",
                signTime: SignTime, secret: Secret, accessKeyId: Key);

            var (status, xml) = await RunXml(ctx);

            status.Should().Be(200);
            xml.Name.LocalName.Should().Be("ListBucketResult");
            xml.Elements(Ns + "Contents").Elements(Ns + "Key").Select(k => k.Value)
                .Should().Contain("blob/report");
        }

        [Fact]
        public async Task Rclone_copy_download_returns_object_bytes()
        {
            var ctx = S3TestSigner.BuildHeaderSigned(
                path: "/docs/blob/report", signTime: SignTime, secret: Secret, accessKeyId: Key);

            var (status, body) = await RunBytes(ctx);

            status.Should().Be(200);
            body.Should().Equal(ReportBytes);
        }

        [Fact]
        public async Task Rclone_copy_upload_single_chunk_round_trips()
        {
            // rclone uploads a single-chunk PUT whose x-amz-content-sha256 is the real payload
            // hash (not aws-chunked), which the middleware serves. The opposite side of the
            // aws-cli streaming boundary: rclone uploads round-trip fully.
            var body = "rclone single-chunk upload"u8.ToArray();
            var ctx = S3TestSigner.BuildHeaderSigned(
                method: "PUT", path: "/docs/blob/upload", signTime: SignTime,
                secret: Secret, accessKeyId: Key, payloadHash: S3SigV4.HashSha256Hex(body));
            ctx.Request.Body = new MemoryStream(body);
            ctx.Request.ContentLength = body.Length;
            ctx.Request.Headers.ContentType = "text/plain";

            var (status, putCtx) = await Run(ctx);
            status.Should().Be(200);
            putCtx.Response.Headers.ETag.ToString().Should().Be($"\"{Md5(body)}\"");

            // Reads back through the real pipeline.
            var getCtx = S3TestSigner.BuildHeaderSigned(
                path: "/docs/blob/upload", signTime: SignTime, secret: Secret, accessKeyId: Key);
            var (getStatus, got) = await RunBytes(getCtx);
            getStatus.Should().Be(200);
            got.Should().Equal(body);
        }

        [Fact]
        public async Task Rclone_delete_removes_object()
        {
            var ctx = S3TestSigner.BuildHeaderSigned(
                method: "DELETE", path: "/docs/blob/report",
                signTime: SignTime, secret: Secret, accessKeyId: Key);

            var (status, _) = await Run(ctx);
            status.Should().Be(204);
        }

        // ---- fixture wiring -------------------------------------------------------

        private const string Key = "AKIACOMPAT";
        private const string Secret = "compat-wJalrXUtnFEMI/K7MDENG";
        private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

        private S3Middleware Build()
        {
            var opts = new S3Options
            {
                Region = S3TestSigner.Region,
                Endpoint = Endpoint,
                EnableWrites = true,
                ContinuationTokenSecret = "compat-tests-secret",
            };
            var keyStore = new FakeS3AccessKeyStore()
                .Add(Key, Secret, S3TestSigner.Principal("compat-user", tenant: "tenant-a"));
            var verifier = new S3SigV4Verifier(keyStore, BifrostAuthContextFactory.Instance, opts, new FixedClock(SignTime));
            var storage = new FileStorageService(
                databaseDefaultConfig: new StorageBucketConfig { BucketName = _tempDir, ProviderType = "local" });
            var seam = _harness.Seam(storage, opts, enableWrites: true);
            RequestDelegate next = _ => Task.CompletedTask;
            return new S3Middleware(next, opts, verifier, _harness.Listing(opts), seam, NullLogger<S3Middleware>.Instance);
        }

        private async Task<(int Status, DefaultHttpContext Ctx)> Run(DefaultHttpContext ctx)
        {
            ctx.Response.Body = new MemoryStream();
            await Build().InvokeAsync(ctx);
            return (ctx.Response.StatusCode, ctx);
        }

        private async Task<(int Status, XElement Xml)> RunXml(DefaultHttpContext ctx)
        {
            ctx.Response.Body = new MemoryStream();
            await Build().InvokeAsync(ctx);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            return (ctx.Response.StatusCode, XElement.Parse(body));
        }

        private async Task<(int Status, byte[] Body)> RunBytes(DefaultHttpContext ctx)
        {
            ctx.Response.Body = new MemoryStream();
            await Build().InvokeAsync(ctx);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            await ctx.Response.Body.CopyToAsync(ms);
            return (ctx.Response.StatusCode, ms.ToArray());
        }

        private async Task WriteBlob(string fileKey, byte[] content)
        {
            var path = Path.Combine(_tempDir, fileKey.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content);
        }

        private static string Md5(byte[] content) => Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant();

        private static string Pointer(string fileKey, byte[] content, string contentType)
            => new FileMetadata
            {
                FileKey = fileKey,
                Size = content.Length,
                ContentType = contentType,
                ETag = Md5(content),
                UploadedAt = Uploaded,
            }.ToJson().Replace("'", "''");

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
