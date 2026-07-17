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
    /// End-to-end PutObject/DeleteObject over the real mutation/read pipeline and a real local
    /// storage backing. Every write travels the authorized seam (so tenant scoping is enforced
    /// by the pipeline, never the adapter), the wire layer validates payload integrity and
    /// metadata before the store is touched, and the security-critical guarantees hold: a
    /// disabled surface builds no intent, a cross-tenant address destroys nothing and is
    /// non-enumerating, and an oversized/truncated/mis-hashed body is rejected without storing.
    ///
    /// <para>Fixtures deliberately span a single-column PK (including value <c>0</c>), a
    /// composite PK, and a target that already holds an object — the diversity the S3 slice-1
    /// lesson requires so a key-addressed write path is not tested vacuously.</para>
    /// </summary>
    public sealed class S3PutDeleteMiddlewareTests : IAsyncLifetime
    {
        private const string Endpoint = "/graphql";
        private static readonly DateTimeOffset SignTime = new(2026, 07, 16, 12, 00, 00, TimeSpan.Zero);
        private static readonly DateTime Uploaded = new(2026, 07, 15, 09, 15, 00, DateTimeKind.Utc);

        private static readonly byte[] Existing = "the original tenant-a object"u8.ToArray();

        private const string KeyA = "AKIATENANTA";
        private const string SecretA = "secretA-wJalrXUtnFEMI/K7MDENG";
        private const string KeyB = "AKIATENANTB";
        private const string SecretB = "secretB-wJalrXUtnFEMI/K7MDENG";

        private string _tempDir = null!;
        private S3ListingRealDbHarness _harness = null!;

        public async Task InitializeAsync()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "s3put_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            await WriteBlob("blobs/existing", Existing);

            _harness = await S3ListingRealDbHarness.StartAsync(
                nameof(S3PutDeleteMiddlewareTests), MetadataRules, SeedSql());
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
                $"(1, 'tenant-a', '{Pointer("blobs/existing", Existing, "text/plain")}')", // overwrite / cross-tenant target
                $"(0, 'tenant-a', NULL)",  // PK value 0, no object
                $"(5, 'tenant-a', NULL)",  // fresh single-PK target, no object
            }),
            "CREATE TABLE parts (region TEXT, sku TEXT, image TEXT, PRIMARY KEY(region, sku))",
            "INSERT INTO parts(region, sku, image) VALUES ('us', 'widget', NULL)", // composite-PK target
        };

        // ---- put: happy paths -----------------------------------------------------

        [Fact]
        public async Task Put_stores_object_and_returns_persisted_md5_etag()
        {
            var body = "brand new content"u8.ToArray();
            var (status, ctx) = await Run(PutA("/assets/data/5", body, "text/plain"));

            status.Should().Be(200);
            ctx.Response.Headers.ETag.ToString().Should().Be($"\"{Md5(body)}\"");

            // Round-trips through the real read path afterward.
            var (getStatus, got, _) = await RunGet(GetA("/assets/data/5"));
            getStatus.Should().Be(200);
            got.Should().Equal(body);
        }

        [Fact]
        public async Task Put_with_pk_value_zero_stores()
        {
            // PK value 0 is the slice-1 misfire case: a scoped-away guard read off .Value
            // rejects it. The write must persist normally.
            var body = "zero-keyed"u8.ToArray();
            var (status, _) = await Run(PutA("/assets/data/0", body, "text/plain"));
            status.Should().Be(200);

            var (getStatus, got, _) = await RunGet(GetA("/assets/data/0"));
            getStatus.Should().Be(200);
            got.Should().Equal(body);
        }

        [Fact]
        public async Task Put_to_composite_pk_stores()
        {
            var body = "composite payload"u8.ToArray();
            var (status, _) = await Run(PutA("/parts/image/us/widget", body, "image/png"));
            status.Should().Be(200);

            var (getStatus, got, _) = await RunGet(GetA("/parts/image/us/widget"));
            getStatus.Should().Be(200);
            got.Should().Equal(body);
        }

        [Fact]
        public async Task Put_overwrites_existing_object_and_reclaims_old_blob()
        {
            var body = "replacement content"u8.ToArray();
            var (status, ctx) = await Run(PutA("/assets/data/1", body, "text/plain"));

            status.Should().Be(200);
            ctx.Response.Headers.ETag.ToString().Should().Be($"\"{Md5(body)}\"");

            // New content is served, and the superseded blob was reclaimed (address is not the
            // storage key: the new bytes landed on a fresh key, the old one is collected).
            var (getStatus, got, _) = await RunGet(GetA("/assets/data/1"));
            getStatus.Should().Be(200);
            got.Should().Equal(body);
            File.Exists(Path.Combine(_tempDir, "blobs", "existing")).Should().BeFalse();
        }

        [Fact]
        public async Task Put_persists_user_metadata_that_round_trips_on_get()
        {
            var body = "with metadata"u8.ToArray();
            var ctx = PutA("/assets/data/5", body, "text/plain");
            ctx.Request.Headers["x-amz-meta-purpose"] = "demo";

            var (status, _) = await Run(ctx);
            status.Should().Be(200);

            var (getStatus, _, getCtx) = await RunGet(GetA("/assets/data/5"));
            getStatus.Should().Be(200);
            getCtx.Response.Headers["x-amz-meta-purpose"].ToString().Should().Be("demo");
        }

        // ---- put: payload validation ---------------------------------------------

        [Fact]
        public async Task Put_with_mismatched_content_sha256_is_rejected_and_stores_nothing()
        {
            var body = "actual bytes"u8.ToArray();
            // Sign a DIFFERENT payload hash than the body carries: validly signed, but the
            // declared single-part hash does not match the bytes.
            var ctx = PutA("/assets/data/5", body, "text/plain",
                declaredHash: S3SigV4.HashSha256Hex("some other bytes"u8.ToArray()));

            var (status, xml) = await RunError(ctx);
            status.Should().Be(400);
            xml.Element("Code")!.Value.Should().Be("XAmzContentSHA256Mismatch");

            (await RunGet(GetA("/assets/data/5"))).Status.Should().Be(404, "a rejected put stores nothing");
        }

        [Fact]
        public async Task Put_with_truncated_body_is_incomplete()
        {
            var body = "twelve chars"u8.ToArray();
            var ctx = PutA("/assets/data/5", body, "text/plain");
            // Declare more bytes than are sent: the received length must match the signed length.
            ctx.Request.ContentLength = body.Length + 5;

            var (status, xml) = await RunError(ctx);
            status.Should().Be(400);
            xml.Element("Code")!.Value.Should().Be("IncompleteBody");
        }

        [Fact]
        public async Task Put_without_content_length_is_rejected()
        {
            var body = "no length"u8.ToArray();
            var ctx = PutA("/assets/data/5", body, "text/plain");
            ctx.Request.ContentLength = null;

            var (status, xml) = await RunError(ctx);
            status.Should().Be(411);
            xml.Element("Code")!.Value.Should().Be("MissingContentLength");
        }

        [Fact]
        public async Task Put_body_over_cap_is_entity_too_large_even_when_content_length_lies()
        {
            var body = new byte[64];  // exceeds the 8-byte cap below
            var ctx = PutA("/assets/data/5", body, "application/octet-stream");
            // A dishonest small Content-Length slips past the pre-read header check; the streaming
            // read bound still stops it, so the endpoint never buffers past the cap.
            ctx.Request.ContentLength = 4;

            var (status, xml) = await RunError(ctx, opts => opts.MaxBodyBytes = 8);
            status.Should().Be(400);
            xml.Element("Code")!.Value.Should().Be("EntityTooLarge");
        }

        [Fact]
        public async Task Put_with_oversized_metadata_is_rejected()
        {
            var body = "small"u8.ToArray();
            var ctx = PutA("/assets/data/5", body, "text/plain");
            ctx.Request.Headers["x-amz-meta-bulk"] = new string('x', 4096);

            var (status, xml) = await RunError(ctx);
            status.Should().Be(400);
            xml.Element("Code")!.Value.Should().Be("InvalidArgument");
            (await RunGet(GetA("/assets/data/5"))).Status.Should().Be(404);
        }

        [Fact]
        public async Task Put_with_non_ascii_metadata_is_rejected()
        {
            var body = "small"u8.ToArray();
            var ctx = PutA("/assets/data/5", body, "text/plain");
            ctx.Request.Headers["x-amz-meta-label"] = "café";  // non-ASCII

            var (status, xml) = await RunError(ctx);
            status.Should().Be(400);
            xml.Element("Code")!.Value.Should().Be("InvalidArgument");
        }

        // ---- put: non-goals -------------------------------------------------------

        [Fact]
        public async Task Put_with_streaming_payload_is_not_implemented()
        {
            var body = "chunked"u8.ToArray();
            var ctx = PutA("/assets/data/5", body, "text/plain",
                declaredHash: "STREAMING-AWS4-HMAC-SHA256-PAYLOAD");

            var (status, xml) = await RunError(ctx);
            status.Should().Be(501);
            xml.Element("Code")!.Value.Should().Be("NotImplemented");
        }

        [Fact]
        public async Task Put_multipart_initiate_is_not_implemented()
        {
            var body = "part"u8.ToArray();
            var ctx = PutA("/assets/data/5", body, "text/plain", query: "?uploads");

            var (status, xml) = await RunError(ctx);
            status.Should().Be(501);
            xml.Element("Code")!.Value.Should().Be("NotImplemented");
        }

        // ---- put: authorization / addressing --------------------------------------

        [Fact]
        public async Task Cross_tenant_put_is_NoSuchKey_and_leaves_victim_untouched()
        {
            // tenant-b writes to tenant-a's object address. The pipeline scopes the row away,
            // so the write matches zero rows and — critically — the provider is never asked to
            // store anything: the victim's content is intact and the answer is non-enumerating.
            var body = "hostile overwrite"u8.ToArray();
            var (status, xml) = await RunError(PutB("/assets/data/1", body, "text/plain"));
            status.Should().Be(404);
            xml.Element("Code")!.Value.Should().Be("NoSuchKey");

            var (getStatus, got, _) = await RunGet(GetA("/assets/data/1"));
            getStatus.Should().Be(200);
            got.Should().Equal(Existing);
        }

        [Theory]
        [InlineData("/assets/data/..")]      // traversal payload as the key value
        [InlineData("/assets/data")]         // wrong arity (missing PK component)
        [InlineData("/assets/tenant_id/1")]  // column that is not a file object
        [InlineData("/assets/data/999")]     // no such row
        public async Task Put_to_unaddressable_target_is_NoSuchKey(string path)
        {
            var body = "payload"u8.ToArray();
            var (status, xml) = await RunError(PutA(path, body, "text/plain"));
            status.Should().Be(404);
            xml.Element("Code")!.Value.Should().Be("NoSuchKey");
        }

        [Fact]
        public async Task Put_is_not_implemented_when_writes_disabled()
        {
            var body = "denied"u8.ToArray();
            var (status, xml) = await RunError(PutA("/assets/data/5", body, "text/plain"), enableWrites: false);
            status.Should().Be(501);
            xml.Element("Code")!.Value.Should().Be("NotImplemented");
        }

        // ---- delete ---------------------------------------------------------------

        [Fact]
        public async Task Delete_removes_object_and_returns_204()
        {
            var (status, _) = await Run(DeleteA("/assets/data/1"));
            status.Should().Be(204);

            (await RunGet(GetA("/assets/data/1"))).Status.Should().Be(404, "the object was removed");
            File.Exists(Path.Combine(_tempDir, "blobs", "existing")).Should().BeFalse();
        }

        [Fact]
        public async Task Delete_of_composite_pk_object_returns_204()
        {
            // Seed an object on the composite-PK row first, then delete it.
            await Run(PutA("/parts/image/us/widget", "x"u8.ToArray(), "image/png"));

            var (status, _) = await Run(DeleteA("/parts/image/us/widget"));
            status.Should().Be(204);
            (await RunGet(GetA("/parts/image/us/widget"))).Status.Should().Be(404);
        }

        [Theory]
        [InlineData("/assets/data/5")]    // row visible, no object (idempotent no-op)
        [InlineData("/assets/data/999")]  // no such row
        [InlineData("/assets/data/..")]   // traversal payload
        [InlineData("/assets/data")]      // wrong arity
        public async Task Delete_of_missing_or_unaddressable_target_is_idempotent_204(string path)
        {
            var (status, _) = await Run(DeleteA(path));
            status.Should().Be(204);
        }

        [Fact]
        public async Task Cross_tenant_delete_is_204_and_leaves_victim_untouched()
        {
            // Non-enumerating idempotent 204, but the pipeline vetoed before the provider: the
            // victim's object is still there.
            var (status, _) = await Run(DeleteB("/assets/data/1"));
            status.Should().Be(204);

            var (getStatus, got, _) = await RunGet(GetA("/assets/data/1"));
            getStatus.Should().Be(200);
            got.Should().Equal(Existing);
        }

        [Fact]
        public async Task Delete_is_not_implemented_when_writes_disabled()
        {
            var (status, xml) = await RunError(DeleteA("/assets/data/1"), enableWrites: false);
            status.Should().Be(501);
            xml.Element("Code")!.Value.Should().Be("NotImplemented");
        }

        // ---- harness --------------------------------------------------------------

        private S3Middleware Build(bool enableWrites = true, Action<S3Options>? tweak = null)
        {
            var opts = new S3Options
            {
                Region = S3TestSigner.Region,
                Endpoint = Endpoint,
                EnableWrites = enableWrites,
                ContinuationTokenSecret = "put-delete-tests-secret",
            };
            tweak?.Invoke(opts);
            var keyStore = new FakeS3AccessKeyStore()
                .Add(KeyA, SecretA, S3TestSigner.Principal("user-a", tenant: "tenant-a"))
                .Add(KeyB, SecretB, S3TestSigner.Principal("user-b", tenant: "tenant-b"));
            var verifier = new S3SigV4Verifier(keyStore, BifrostAuthContextFactory.Instance, opts, new FixedClock(SignTime));
            var storage = new FileStorageService(
                databaseDefaultConfig: new StorageBucketConfig { BucketName = _tempDir, ProviderType = "local" });
            var seam = _harness.Seam(storage, opts, enableWrites: enableWrites);
            RequestDelegate next = _ => Task.CompletedTask;
            return new S3Middleware(next, opts, verifier, _harness.Listing(opts), seam, NullLogger<S3Middleware>.Instance);
        }

        private DefaultHttpContext PutA(string path, byte[] body, string? contentType, string? declaredHash = null, string? query = null)
            => SignedPut(path, body, contentType, SecretA, KeyA, declaredHash, query);

        private DefaultHttpContext PutB(string path, byte[] body, string? contentType)
            => SignedPut(path, body, contentType, SecretB, KeyB, declaredHash: null, query: null);

        private static DefaultHttpContext SignedPut(
            string path, byte[] body, string? contentType, string secret, string accessKeyId,
            string? declaredHash, string? query)
        {
            var hash = declaredHash ?? S3SigV4.HashSha256Hex(body);
            var ctx = S3TestSigner.BuildHeaderSigned(
                method: "PUT", path: path, query: query, signTime: SignTime,
                secret: secret, accessKeyId: accessKeyId, payloadHash: hash);
            ctx.Request.Body = new MemoryStream(body);
            ctx.Request.ContentLength = body.Length;
            if (contentType is not null)
                ctx.Request.Headers.ContentType = contentType;
            return ctx;
        }

        private DefaultHttpContext DeleteA(string path)
            => S3TestSigner.BuildHeaderSigned(method: "DELETE", path: path, signTime: SignTime, secret: SecretA, accessKeyId: KeyA);

        private DefaultHttpContext DeleteB(string path)
            => S3TestSigner.BuildHeaderSigned(method: "DELETE", path: path, signTime: SignTime, secret: SecretB, accessKeyId: KeyB);

        private DefaultHttpContext GetA(string path)
            => S3TestSigner.BuildHeaderSigned(method: "GET", path: path, signTime: SignTime, secret: SecretA, accessKeyId: KeyA);

        private async Task<(int Status, DefaultHttpContext Ctx)> Run(DefaultHttpContext ctx)
        {
            ctx.Response.Body = new MemoryStream();
            await Build().InvokeAsync(ctx);
            return (ctx.Response.StatusCode, ctx);
        }

        private async Task<(int Status, XElement Xml)> RunError(
            DefaultHttpContext ctx, Action<S3Options>? tweak = null, bool enableWrites = true)
        {
            ctx.Response.Body = new MemoryStream();
            await Build(enableWrites, tweak).InvokeAsync(ctx);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            return (ctx.Response.StatusCode, XElement.Parse(body));
        }

        private async Task<(int Status, byte[] Body, DefaultHttpContext Ctx)> RunGet(DefaultHttpContext ctx)
        {
            ctx.Response.Body = new MemoryStream();
            await Build().InvokeAsync(ctx);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            await ctx.Response.Body.CopyToAsync(ms);
            return (ctx.Response.StatusCode, ms.ToArray(), ctx);
        }

        private async Task WriteBlob(string fileKey, byte[] content)
        {
            var path = Path.Combine(_tempDir, fileKey.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content);
        }

        private static string Md5(byte[] content) => Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant();

        private static string Pointer(string fileKey, byte[] content, string contentType)
        {
            var metadata = new FileMetadata
            {
                FileKey = fileKey,
                Size = content.Length,
                ContentType = contentType,
                ETag = Md5(content),
                UploadedAt = Uploaded,
            };
            return metadata.ToJson().Replace("'", "''");
        }

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
