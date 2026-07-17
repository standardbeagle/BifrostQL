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
    /// End-to-end GetObject/HeadObject over the real read pipeline and a real local
    /// storage backing: rows are resolved through the authorized seam before any file
    /// is touched, content and validator headers are served, single byte ranges and
    /// conditional requests behave per the acceptance criteria, and every
    /// missing/unauthorized/malformed address answers the same non-enumerating 404.
    ///
    /// <para>Fixtures span a single-column PK (including value <c>0</c>), a composite
    /// PK, a zero-byte object, and a tenant boundary — the diversity the S3 slice-1
    /// lesson requires so a key-addressed read path is not tested vacuously.</para>
    /// </summary>
    public sealed class S3GetObjectMiddlewareTests : IAsyncLifetime
    {
        private const string Endpoint = "/graphql";
        private static readonly DateTimeOffset SignTime = new(2026, 07, 16, 12, 00, 00, TimeSpan.Zero);
        private static readonly DateTime Uploaded = new(2026, 07, 15, 09, 15, 00, DateTimeKind.Utc);

        // Content of each seeded object, keyed by its storage FileKey.
        private static readonly byte[] Full = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        private static readonly byte[] Hello = "hello, world"u8.ToArray();
        private static readonly byte[] Single = new byte[] { 7 };
        private static readonly byte[] Composite = Enumerable.Range(0, 50).Select(i => (byte)(i + 5)).ToArray();
        private static readonly byte[] Empty = Array.Empty<byte>();

        private string _tempDir = null!;
        private S3ListingRealDbHarness _harness = null!;

        // Two principals bound to two tenants, to prove the tenant boundary end to end.
        private const string KeyA = "AKIATENANTA";
        private const string SecretA = "secretA-wJalrXUtnFEMI/K7MDENG";
        private const string KeyB = "AKIATENANTB";
        private const string SecretB = "secretB-wJalrXUtnFEMI/K7MDENG";

        public async Task InitializeAsync()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "s3get_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            await WriteBlob("blobs/full", Full);
            await WriteBlob("blobs/hello", Hello);
            await WriteBlob("blobs/single", Single);
            await WriteBlob("blobs/composite", Composite);
            await WriteBlob("blobs/empty", Empty);

            _harness = await S3ListingRealDbHarness.StartAsync(nameof(S3GetObjectMiddlewareTests), MetadataRules, SeedSql());
        }

        public async Task DisposeAsync()
        {
            await _harness.DisposeAsync();
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        private static readonly string[] MetadataRules =
        {
            "main.assets { tenant-filter: tenant_id }",
            "main.assets.data { file: json }",
            "main.parts.image { file: json }",
        };

        private string[] SeedSql() => new[]
        {
            "DROP TABLE IF EXISTS assets",
            "DROP TABLE IF EXISTS parts",
            "CREATE TABLE assets (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, data TEXT)",
            "INSERT INTO assets(id, tenant_id, data) VALUES\n" + string.Join(",\n", new[]
            {
                $"(1, 'tenant-a', '{Pointer("blobs/full", Full, "text/plain", ("purpose", "demo"))}')",
                $"(2, 'tenant-b', '{Pointer("blobs/hello", Hello, "text/plain")}')",
                $"(3, 'tenant-a', NULL)",                                 // row visible, no object
                $"(0, 'tenant-a', '{Pointer("blobs/single", Single, "application/octet-stream")}')", // PK value 0
                $"(9, 'tenant-a', '{Pointer("blobs/empty", Empty, "text/plain")}')",  // zero-byte object
            }),
            // Composite-PK table: object key is image/{region}/{sku}.
            "CREATE TABLE parts (region TEXT, sku TEXT, image TEXT, PRIMARY KEY(region, sku))",
            $"INSERT INTO parts(region, sku, image) VALUES ('us', 'widget', '{Pointer("blobs/composite", Composite, "image/png")}')",
        };

        // ---- full read ------------------------------------------------------------

        [Fact]
        public async Task Get_full_object_returns_content_and_validator_headers()
        {
            var (status, body, ctx) = await Run(SignedA("/assets/data/1"));

            status.Should().Be(200);
            body.Should().Equal(Full);
            ctx.Response.ContentLength.Should().Be(100);
            ctx.Response.ContentType.Should().Be("text/plain");
            ctx.Response.Headers.ETag.ToString().Should().Be($"\"{ETagOf(Full)}\"");
            ctx.Response.Headers.LastModified.ToString().Should().Be(
                new DateTimeOffset(Uploaded).ToString("R"));
            ctx.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
            ctx.Response.Headers["x-amz-meta-purpose"].ToString().Should().Be("demo");
        }

        [Fact]
        public async Task Head_returns_the_same_headers_with_no_body()
        {
            var (status, body, ctx) = await Run(SignedA("/assets/data/1", method: "HEAD"));

            status.Should().Be(200);
            body.Should().BeEmpty();
            ctx.Response.ContentLength.Should().Be(100);
            ctx.Response.Headers.ETag.ToString().Should().Be($"\"{ETagOf(Full)}\"");
            ctx.Response.Headers["x-amz-meta-purpose"].ToString().Should().Be("demo");
        }

        [Fact]
        public async Task Get_zero_byte_object_returns_empty_200()
        {
            var (status, body, ctx) = await Run(SignedA("/assets/data/9"));

            status.Should().Be(200);
            body.Should().BeEmpty();
            ctx.Response.ContentLength.Should().Be(0);
        }

        [Fact]
        public async Task Get_object_with_pk_value_zero_returns_content()
        {
            // PK value 0 is the S3 slice-1 misfire case: it must resolve normally.
            var (status, body, _) = await Run(SignedA("/assets/data/0"));

            status.Should().Be(200);
            body.Should().Equal(Single);
        }

        [Fact]
        public async Task Get_composite_pk_object_returns_content()
        {
            var (status, body, _) = await Run(SignedA("/parts/image/us/widget"));

            status.Should().Be(200);
            body.Should().Equal(Composite);
        }

        // ---- ranges ---------------------------------------------------------------

        [Fact]
        public async Task Get_closed_range_returns_206_with_content_range()
        {
            var (status, body, ctx) = await Run(SignedA("/assets/data/1", range: "bytes=10-19"));

            status.Should().Be(206);
            body.Should().Equal(Full[10..20]);
            ctx.Response.ContentLength.Should().Be(10);
            ctx.Response.Headers.ContentRange.ToString().Should().Be("bytes 10-19/100");
        }

        [Fact]
        public async Task Get_suffix_range_returns_last_bytes()
        {
            var (status, body, ctx) = await Run(SignedA("/assets/data/1", range: "bytes=-5"));

            status.Should().Be(206);
            body.Should().Equal(Full[95..100]);
            ctx.Response.Headers.ContentRange.ToString().Should().Be("bytes 95-99/100");
        }

        [Fact]
        public async Task Get_range_on_composite_pk_object_is_partial()
        {
            var (status, body, ctx) = await Run(SignedA("/parts/image/us/widget", range: "bytes=0-9"));

            status.Should().Be(206);
            body.Should().Equal(Composite[0..10]);
            ctx.Response.Headers.ContentRange.ToString().Should().Be("bytes 0-9/50");
        }

        [Fact]
        public async Task Unsatisfiable_range_returns_416_with_content_range()
        {
            var (status, bodyText, ctx) = await RunText(SignedA("/assets/data/1", range: "bytes=200-300"));

            status.Should().Be(416);
            ctx.Response.Headers.ContentRange.ToString().Should().Be("bytes */100");
            XElement.Parse(bodyText).Element("Code")!.Value.Should().Be("InvalidRange");
        }

        [Fact]
        public async Task Range_on_zero_byte_object_is_unsatisfiable()
        {
            var (status, _, _) = await RunText(SignedA("/assets/data/9", range: "bytes=0-0"));
            status.Should().Be(416);
        }

        [Fact]
        public async Task Multi_range_is_ignored_and_serves_whole_object()
        {
            // Multipart byteranges is a non-goal; a multi-range request degrades to the
            // whole object (200), never a wrong partial body.
            var (status, body, _) = await Run(SignedA("/assets/data/1", range: "bytes=0-1,5-6"));

            status.Should().Be(200);
            body.Should().Equal(Full);
        }

        [Fact]
        public async Task Overflow_range_start_is_unsatisfiable_not_a_crash()
        {
            // A 29-nines start overflows Int64; it must map to a clean 416, never an
            // unhandled OverflowException reaching the host (invariant 5).
            var (status, _, _) = await RunText(
                SignedA("/assets/data/1", range: "bytes=99999999999999999999999999999-"));
            status.Should().Be(416);
        }

        // ---- conditional requests -------------------------------------------------

        [Fact]
        public async Task If_none_match_matching_etag_is_304_without_body()
        {
            var ctx = SignedA("/assets/data/1");
            ctx.Request.Headers.IfNoneMatch = $"\"{ETagOf(Full)}\"";

            var (status, body, response) = await Run(ctx);

            status.Should().Be(304);
            body.Should().BeEmpty();
            response.Response.Headers.ETag.ToString().Should().Be($"\"{ETagOf(Full)}\"");
        }

        [Fact]
        public async Task If_match_wrong_etag_is_412()
        {
            var ctx = SignedA("/assets/data/1");
            ctx.Request.Headers.IfMatch = "\"deadbeef\"";

            var (status, _, _) = await RunText(ctx);

            status.Should().Be(412);
        }

        // ---- non-enumerating errors ----------------------------------------------

        [Fact]
        public async Task Missing_row_is_NoSuchKey()
        {
            var (status, bodyText, _) = await RunText(SignedA("/assets/data/999"));
            status.Should().Be(404);
            XElement.Parse(bodyText).Element("Code")!.Value.Should().Be("NoSuchKey");
        }

        [Fact]
        public async Task Row_with_no_object_is_NoSuchKey()
        {
            var (status, bodyText, _) = await RunText(SignedA("/assets/data/3"));
            status.Should().Be(404);
            XElement.Parse(bodyText).Element("Code")!.Value.Should().Be("NoSuchKey");
        }

        [Fact]
        public async Task Cross_tenant_object_is_NoSuchKey_not_forbidden()
        {
            // tenant-b asks for tenant-a's object: the row is scoped away by the pipeline,
            // so the answer is identical to "missing" — never AccessDenied, which would
            // confirm the object exists.
            var (status, bodyText, _) = await RunText(SignedB("/assets/data/1"));
            status.Should().Be(404);
            XElement.Parse(bodyText).Element("Code")!.Value.Should().Be("NoSuchKey");
        }

        [Theory]
        [InlineData("/assets/data/..")]       // '..' as the key value: rejected by the key map, not the fs
        [InlineData("/assets/data")]          // missing PK component (wrong arity)
        [InlineData("/assets/notacolumn/1")]  // column that is not a file object
        public async Task Traversal_or_malformed_address_is_NoSuchKey(string path)
        {
            // A traversal payload can never reach the provider: the key map rejects it as
            // an addressing fault, indistinguishable from a missing key.
            var (status, bodyText, _) = await RunText(SignedA(path));
            status.Should().Be(404);
            XElement.Parse(bodyText).Element("Code")!.Value.Should().Be("NoSuchKey");
        }

        // ---- cancellation ---------------------------------------------------------

        [Fact]
        public async Task Cancelled_request_writes_no_body()
        {
            var ctx = SignedA("/assets/data/1");
            ctx.RequestAborted = new CancellationToken(canceled: true);
            ctx.Response.Body = new MemoryStream();

            await Build().InvokeAsync(ctx);

            ctx.Response.Body.Length.Should().Be(0);
        }

        // ---- harness --------------------------------------------------------------

        private S3Middleware Build()
        {
            var opts = new S3Options
            {
                Region = S3TestSigner.Region,
                Endpoint = Endpoint,
                ContinuationTokenSecret = "get-object-tests-secret",
            };
            var keyStore = new FakeS3AccessKeyStore()
                .Add(KeyA, SecretA, S3TestSigner.Principal("user-a", tenant: "tenant-a"))
                .Add(KeyB, SecretB, S3TestSigner.Principal("user-b", tenant: "tenant-b"));
            var verifier = new S3SigV4Verifier(keyStore, BifrostAuthContextFactory.Instance, opts, new FixedClock(SignTime));
            var storage = new FileStorageService(
                databaseDefaultConfig: new StorageBucketConfig { BucketName = _tempDir, ProviderType = "local" });
            var seam = _harness.Seam(storage, opts);
            RequestDelegate next = _ => Task.CompletedTask;
            return new S3Middleware(next, opts, verifier, _harness.Listing(opts), seam, NullLogger<S3Middleware>.Instance);
        }

        private DefaultHttpContext SignedA(string path, string method = "GET", string? range = null)
            => WithRange(S3TestSigner.BuildHeaderSigned(method: method, path: path, signTime: SignTime, secret: SecretA, accessKeyId: KeyA), range);

        private DefaultHttpContext SignedB(string path, string method = "GET")
            => S3TestSigner.BuildHeaderSigned(method: method, path: path, signTime: SignTime, secret: SecretB, accessKeyId: KeyB);

        private static DefaultHttpContext WithRange(DefaultHttpContext ctx, string? range)
        {
            if (range is not null)
                ctx.Request.Headers.Range = range;
            return ctx;
        }

        private async Task<(int Status, byte[] Body, DefaultHttpContext Ctx)> Run(DefaultHttpContext ctx)
        {
            ctx.Response.Body = new MemoryStream();
            await Build().InvokeAsync(ctx);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            await ctx.Response.Body.CopyToAsync(ms);
            return (ctx.Response.StatusCode, ms.ToArray(), ctx);
        }

        private async Task<(int Status, string Body, DefaultHttpContext Ctx)> RunText(DefaultHttpContext ctx)
        {
            var (status, body, context) = await Run(ctx);
            return (status, System.Text.Encoding.UTF8.GetString(body), context);
        }

        private async Task WriteBlob(string fileKey, byte[] content)
        {
            var path = Path.Combine(_tempDir, fileKey.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content);
        }

        private static string ETagOf(byte[] content)
            => Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant();

        private static string Pointer(string fileKey, byte[] content, string contentType, params (string Key, string Value)[] custom)
        {
            var metadata = new FileMetadata
            {
                FileKey = fileKey,
                Size = content.Length,
                ContentType = contentType,
                ETag = ETagOf(content),
                UploadedAt = Uploaded,
                CustomMetadata = custom.Length == 0 ? null : custom.ToDictionary(c => c.Key, c => c.Value),
            };
            return metadata.ToJson().Replace("'", "''");
        }

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
