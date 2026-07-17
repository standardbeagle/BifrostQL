using System.Security.Cryptography;
using System.Xml.Linq;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Storage;
using BifrostQL.Server.S3;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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
                $"(7, 'tenant-b', NULL)",  // cross-tenant destination: owned by tenant-b
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

        // ---- put/delete: post-authorization residue (orphaned blob) --------------

        [Fact]
        public async Task Put_compensation_double_fault_is_sanitized_500_and_logs_residue_at_error()
        {
            // Blob uploaded, the pointer write is scoped away to zero rows (the pipeline
            // narrowed it), THEN the compensating rollback delete also fails: the just-uploaded
            // object is orphaned residue. Unlike a clean denial (NoSuchKey), this is a
            // post-authorization internal failure that left storage behind — the wire answers a
            // sanitized 500 (no seam detail on the wire, invariant 3) and the orphan is logged at
            // Error WITH its storage key, never swallowed at Debug.
            var log = new CapturingLogger<S3Middleware>();
            var seam = ResidueSeam(new DeleteFailingProvider(), scopeAwayWrites: true);
            var ctx = PutA("/assets/data/5", "orphaned"u8.ToArray(), "text/plain");
            var (status, xml) = await RunErrorWithSeam(ctx, seam, log);

            status.Should().Be(500);
            xml.Element("Code")!.Value.Should().Be("InternalError");
            xml.ToString().Should().NotContain("orphan", "the seam's internal residue detail must not reach the wire");

            var error = log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
            error.Message.Should().Contain("residue");
            log.Entries.Should().NotContain(
                e => e.Level == LogLevel.Debug,
                "the orphan must be operator-visible at Error, not swallowed at Debug");
        }

        [Fact]
        public async Task Delete_when_blob_delete_fails_after_pointer_cleared_is_204_and_logs_residue_at_error()
        {
            // The pipeline clears the pointer (committed), then the backing blob delete
            // fails: the object is now unreferenced residue an operator must reclaim. DELETE
            // stays idempotent (204), but the orphan is surfaced at Error WITH its storage
            // key — never swallowed at Debug (the exact regression this rework fixes).
            var log = new CapturingLogger<S3Middleware>();
            var (status, ctx) = await RunWith(DeleteA("/assets/data/1"), new DeleteFailingProvider(), log);

            status.Should().Be(204);
            var error = log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
            error.Message.Should().Contain("blobs/existing", "the orphaned storage key must be in the operator log");
            error.Message.Should().Contain("residue");
            log.Entries.Should().NotContain(
                e => e.Level == LogLevel.Debug,
                "the orphan must be operator-visible at Error, not swallowed at Debug");

            // The pointer really was cleared, even though the blob delete failed.
            (await RunGet(GetA("/assets/data/1"))).Status.Should().Be(404);
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

        // ---- copy: happy paths ----------------------------------------------------

        [Fact]
        public async Task Copy_stores_destination_and_returns_source_md5_in_result()
        {
            // Copy tenant-a's existing object at data/1 onto the empty data/5.
            var (status, body) = await RunCopy(CopyA("/assets/data/5", "/assets/data/1"));

            status.Should().Be(200);
            body.Should().Contain("<CopyObjectResult");
            body.Should().Contain(Md5(Existing), "the result ETag is the destination's stored MD5");

            // The destination now serves the source bytes; the source is untouched.
            var (destStatus, dest, _) = await RunGet(GetA("/assets/data/5"));
            destStatus.Should().Be(200);
            dest.Should().Equal(Existing);
            (await RunGet(GetA("/assets/data/1"))).Body.Should().Equal(Existing);
        }

        [Fact]
        public async Task Copy_to_pk_value_zero_destination_stores()
        {
            var (status, _) = await RunCopy(CopyA("/assets/data/0", "/assets/data/1"));
            status.Should().Be(200);
            (await RunGet(GetA("/assets/data/0"))).Body.Should().Equal(Existing);
        }

        [Fact]
        public async Task Copy_to_composite_pk_destination_stores()
        {
            var (status, _) = await RunCopy(CopyA("/parts/image/us/widget", "/assets/data/1"));
            status.Should().Be(200);
            (await RunGet(GetA("/parts/image/us/widget"))).Body.Should().Equal(Existing);
        }

        [Fact]
        public async Task Copy_over_pre_existing_destination_reclaims_old_blob()
        {
            // Seed a fresh source object, then copy it over data/1 which already holds an object.
            var src = "fresh source bytes"u8.ToArray();
            (await Run(PutA("/assets/data/5", src, "text/plain"))).Status.Should().Be(200);

            var (status, _) = await RunCopy(CopyA("/assets/data/1", "/assets/data/5"));
            status.Should().Be(200);

            var (destStatus, dest, _) = await RunGet(GetA("/assets/data/1"));
            destStatus.Should().Be(200);
            dest.Should().Equal(src);
            // The superseded blob landed on a fresh key, so the old one is collected.
            File.Exists(Path.Combine(_tempDir, "blobs", "existing")).Should().BeFalse();
        }

        // ---- copy: metadata directive ---------------------------------------------

        [Fact]
        public async Task Copy_default_directive_inherits_source_content_type_and_metadata()
        {
            // Source carries a content type and user metadata; a default (COPY) copy inherits both.
            var src = PutA("/assets/data/5", "payload"u8.ToArray(), "application/json");
            src.Request.Headers["x-amz-meta-purpose"] = "demo";
            (await Run(src)).Status.Should().Be(200);

            var (status, _) = await RunCopy(CopyA("/assets/data/0", "/assets/data/5"));
            status.Should().Be(200);

            var (_, _, getCtx) = await RunGet(GetA("/assets/data/0"));
            getCtx.Response.ContentType.Should().Be("application/json");
            getCtx.Response.Headers["x-amz-meta-purpose"].ToString().Should().Be("demo");
        }

        [Fact]
        public async Task Copy_replace_directive_uses_request_content_type_and_metadata()
        {
            var src = PutA("/assets/data/5", "payload"u8.ToArray(), "application/json");
            src.Request.Headers["x-amz-meta-purpose"] = "demo";
            (await Run(src)).Status.Should().Be(200);

            var copy = CopyA("/assets/data/0", "/assets/data/5", directive: "REPLACE", contentType: "text/csv");
            copy.Request.Headers["x-amz-meta-stage"] = "final";
            var (status, _) = await RunCopy(copy);
            status.Should().Be(200);

            var (_, body, getCtx) = await RunGet(GetA("/assets/data/0"));
            body.Should().Equal("payload"u8.ToArray(), "content is copied even when metadata is replaced");
            getCtx.Response.ContentType.Should().Be("text/csv");
            getCtx.Response.Headers["x-amz-meta-stage"].ToString().Should().Be("final");
            getCtx.Response.Headers.ContainsKey("x-amz-meta-purpose").Should().BeFalse("REPLACE drops the source metadata");
        }

        [Fact]
        public async Task Copy_with_unknown_metadata_directive_is_invalid_argument()
        {
            var (status, xml) = await RunError(CopyA("/assets/data/5", "/assets/data/1", directive: "MERGE"));
            status.Should().Be(400);
            xml.Element("Code")!.Value.Should().Be("InvalidArgument");
        }

        // ---- copy: self-copy ------------------------------------------------------

        [Fact]
        public async Task Self_copy_without_replace_is_invalid_request()
        {
            var (status, xml) = await RunError(CopyA("/assets/data/1", "/assets/data/1"));
            status.Should().Be(400);
            xml.Element("Code")!.Value.Should().Be("InvalidRequest");
            // The object is untouched.
            (await RunGet(GetA("/assets/data/1"))).Body.Should().Equal(Existing);
        }

        [Fact]
        public async Task Self_copy_with_replace_rewrites_metadata()
        {
            var src = PutA("/assets/data/5", "keep bytes"u8.ToArray(), "application/json");
            src.Request.Headers["x-amz-meta-old"] = "1";
            (await Run(src)).Status.Should().Be(200);

            var (status, _) = await RunCopy(
                CopyA("/assets/data/5", "/assets/data/5", directive: "REPLACE", contentType: "text/plain"));
            status.Should().Be(200);

            var (_, body, getCtx) = await RunGet(GetA("/assets/data/5"));
            body.Should().Equal("keep bytes"u8.ToArray());
            getCtx.Response.ContentType.Should().Be("text/plain");
            getCtx.Response.Headers.ContainsKey("x-amz-meta-old").Should().BeFalse();
        }

        // ---- copy: authorization / addressing -------------------------------------

        [Fact]
        public async Task Cross_tenant_source_is_NoSuchKey_and_writes_nothing()
        {
            // tenant-b reads tenant-a's source. The source read is scoped away, so the copy is
            // a non-enumerating NoSuchKey and tenant-b's own destination is never written.
            var (status, xml) = await RunError(CopyBTo("/assets/data/7", "/assets/data/1"));
            status.Should().Be(404);
            xml.Element("Code")!.Value.Should().Be("NoSuchKey");

            (await RunGet(GetB("/assets/data/7"))).Status.Should().Be(404, "the destination was never written");
        }

        [Fact]
        public async Task Cross_tenant_destination_is_NoSuchKey_and_leaves_source_intact()
        {
            // tenant-a reads its OWN source (data/1) but targets data/7, owned by tenant-b. The
            // source read passes; the destination write is scoped away by the pipeline, so the
            // copy is NoSuchKey and neither the source nor the cross-tenant destination changes.
            var (status, xml) = await RunError(CopyA("/assets/data/7", "/assets/data/1"));
            status.Should().Be(404);
            xml.Element("Code")!.Value.Should().Be("NoSuchKey");

            (await RunGet(GetA("/assets/data/1"))).Body.Should().Equal(Existing, "the source is intact");
            (await RunGet(GetB("/assets/data/7"))).Status.Should().Be(404, "the destination was never written");
        }

        [Fact]
        public async Task Copy_from_missing_source_is_NoSuchKey()
        {
            var (status, xml) = await RunError(CopyA("/assets/data/5", "/assets/data/999"));
            status.Should().Be(404);
            xml.Element("Code")!.Value.Should().Be("NoSuchKey");
        }

        [Theory]
        [InlineData("/assets/data/..")]         // literal traversal in the source key
        [InlineData("/assets/data/%252E%252E")] // double-encoded traversal
        [InlineData("//assets/data/1")]         // rooted source reference
        public async Task Copy_with_malformed_source_is_invalid_argument(string copySource)
        {
            var (status, xml) = await RunError(CopyA("/assets/data/5", copySource));
            status.Should().Be(400);
            xml.Element("Code")!.Value.Should().Be("InvalidArgument");
        }

        [Fact]
        public async Task Copy_is_not_implemented_when_writes_disabled()
        {
            var (status, xml) = await RunError(CopyA("/assets/data/5", "/assets/data/1"), enableWrites: false);
            status.Should().Be(501);
            xml.Element("Code")!.Value.Should().Be("NotImplemented");
        }

        [Fact]
        public async Task Copy_honors_cancellation_and_writes_nothing()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var ctx = CopyA("/assets/data/5", "/assets/data/1");
            ctx.RequestAborted = cts.Token;
            ctx.Response.Body = new MemoryStream();

            await Build().InvokeAsync(ctx);

            // A cancelled copy stored nothing at the destination.
            (await RunGet(GetA("/assets/data/5"))).Status.Should().Be(404);
        }

        // ---- copy: post-authorization residue -------------------------------------

        [Fact]
        public async Task Copy_compensation_double_fault_is_sanitized_500_and_logs_residue_at_error()
        {
            // Source resolves, its bytes are read, the destination blob is uploaded to a fresh
            // key, the pointer write is scoped away to zero rows, THEN the compensating rollback
            // delete also fails: the just-uploaded object is orphaned residue. As with PutObject,
            // the wire answers a sanitized 500 (no seam detail, invariant 3) and the orphan is
            // logged at Error WITH its storage key — never swallowed at Debug.
            var log = new CapturingLogger<S3Middleware>();
            var seam = ResidueSeam(new DeleteFailingProvider(), scopeAwayWrites: true);
            var (status, xml) = await RunErrorWithSeam(CopyA("/assets/data/5", "/assets/data/1"), seam, log);

            status.Should().Be(500);
            xml.Element("Code")!.Value.Should().Be("InternalError");
            xml.ToString().Should().NotContain("orphan", "the seam's internal residue detail must not reach the wire");

            var error = log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
            error.Message.Should().Contain("residue");
            log.Entries.Should().NotContain(
                e => e.Level == LogLevel.Debug,
                "the orphan must be operator-visible at Error, not swallowed at Debug");
        }

        // ---- harness --------------------------------------------------------------

        private S3Middleware Build(
            bool enableWrites = true, Action<S3Options>? tweak = null,
            IStorageProvider? storageProvider = null, ILogger<S3Middleware>? logger = null,
            FileObjectSeam? seamOverride = null)
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
            FileObjectSeam seam;
            if (seamOverride is not null)
            {
                seam = seamOverride;
            }
            else
            {
                var bucketConfig = new StorageBucketConfig { BucketName = _tempDir, ProviderType = "local" };
                FileStorageService storage;
                if (storageProvider is not null)
                {
                    var providerFactory = new StorageProviderFactory();
                    providerFactory.RegisterProvider(storageProvider);
                    storage = new FileStorageService(providerFactory, bucketConfig);
                }
                else
                {
                    storage = new FileStorageService(databaseDefaultConfig: bucketConfig);
                }
                seam = _harness.Seam(storage, opts, enableWrites: enableWrites);
            }
            RequestDelegate next = _ => Task.CompletedTask;
            return new S3Middleware(
                next, opts, verifier, _harness.Listing(opts), seam,
                logger ?? NullLogger<S3Middleware>.Instance);
        }

        /// <summary>
        /// A seam over the real read/write pipeline but with a delete-failing storage provider,
        /// optionally wrapping writes so every update reports zero affected rows — the injection
        /// point for the post-authorization residue paths (a compensating rollback or a post-clear
        /// blob delete that fails, orphaning the just-written/cleared object).
        /// </summary>
        private FileObjectSeam ResidueSeam(IStorageProvider provider, bool scopeAwayWrites)
        {
            var providerFactory = new StorageProviderFactory();
            providerFactory.RegisterProvider(provider);
            var storage = new FileStorageService(
                providerFactory, new StorageBucketConfig { BucketName = _tempDir, ProviderType = "local" });
            IMutationIntentExecutor writes = scopeAwayWrites ? new ScopeAwayWrites() : _harness.Writes;
            return new FileObjectSeam(
                _harness.Reads, writes, storage,
                new FileObjectSeamOptions { Endpoint = Endpoint, EnableWrites = true });
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

        private DefaultHttpContext GetB(string path)
            => S3TestSigner.BuildHeaderSigned(method: "GET", path: path, signTime: SignTime, secret: SecretB, accessKeyId: KeyB);

        private DefaultHttpContext CopyA(string destPath, string copySource, string? directive = null, string? contentType = null)
            => SignedCopy(destPath, copySource, directive, contentType, SecretA, KeyA);

        private DefaultHttpContext CopyBTo(string destPath, string copySource)
            => SignedCopy(destPath, copySource, directive: null, contentType: null, SecretB, KeyB);

        /// <summary>
        /// A copy is a PUT to the destination carrying x-amz-copy-source and an empty body (the
        /// bytes come from the source). The copy-source and directive headers ride unsigned —
        /// the signed subset still authenticates the request, exactly as the metadata headers do
        /// on a normal put.
        /// </summary>
        private static DefaultHttpContext SignedCopy(
            string destPath, string copySource, string? directive, string? contentType, string secret, string accessKeyId)
        {
            var ctx = S3TestSigner.BuildHeaderSigned(
                method: "PUT", path: destPath, signTime: SignTime,
                secret: secret, accessKeyId: accessKeyId, payloadHash: S3TestSigner.EmptyPayloadHash);
            ctx.Request.Body = new MemoryStream(Array.Empty<byte>());
            ctx.Request.ContentLength = 0;
            ctx.Request.Headers["x-amz-copy-source"] = copySource;
            if (directive is not null)
                ctx.Request.Headers["x-amz-metadata-directive"] = directive;
            if (contentType is not null)
                ctx.Request.Headers.ContentType = contentType;
            return ctx;
        }

        private async Task<(int Status, string Body)> RunCopy(DefaultHttpContext ctx)
        {
            ctx.Response.Body = new MemoryStream();
            await Build().InvokeAsync(ctx);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
        }

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

        private async Task<(int Status, DefaultHttpContext Ctx)> RunWith(
            DefaultHttpContext ctx, IStorageProvider provider, ILogger<S3Middleware> logger)
        {
            ctx.Response.Body = new MemoryStream();
            await Build(storageProvider: provider, logger: logger).InvokeAsync(ctx);
            return (ctx.Response.StatusCode, ctx);
        }

        private async Task<(int Status, XElement Xml)> RunErrorWithSeam(
            DefaultHttpContext ctx, FileObjectSeam seam, ILogger<S3Middleware> logger)
        {
            ctx.Response.Body = new MemoryStream();
            await Build(logger: logger, seamOverride: seam).InvokeAsync(ctx);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            return (ctx.Response.StatusCode, XElement.Parse(body));
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

        /// <summary>Real local reads/uploads, but every delete throws — forces the post-clear residue path.</summary>
        private sealed class DeleteFailingProvider : IStorageProvider
        {
            private readonly LocalStorageProvider _inner = new();
            public string ProviderType => "local";
            public Task<string> UploadAsync(StorageBucketConfig c, string k, byte[] b, string? t = null, CancellationToken ct = default) => _inner.UploadAsync(c, k, b, t, ct);
            public Task<byte[]> DownloadAsync(StorageBucketConfig c, string k, CancellationToken ct = default) => _inner.DownloadAsync(c, k, ct);
            public Task DeleteAsync(StorageBucketConfig c, string k, CancellationToken ct = default) => throw new InvalidOperationException("simulated storage delete failure");
            public Task<bool> ExistsAsync(StorageBucketConfig c, string k, CancellationToken ct = default) => _inner.ExistsAsync(c, k, ct);
            public Task<string> GetPresignedUrlAsync(StorageBucketConfig c, string k, int e = 15, bool u = false) => _inner.GetPresignedUrlAsync(c, k, e, u);
        }

        /// <summary>
        /// Reports every update as matching zero rows — the pipeline-scoped-away case (row
        /// reassigned/soft-deleted/out-of-tenant between the seam's read and its write) that the
        /// seam detects via AffectedRows and turns into a compensating rollback.
        /// </summary>
        private sealed class ScopeAwayWrites : IMutationIntentExecutor
        {
            public Task<MutationIntentResult> ExecuteAsync(MutationIntent intent, CancellationToken cancellationToken = default)
                => Task.FromResult(new MutationIntentResult { AffectedRows = 0 });
            public Task<MutationBatchIntentResult> ExecuteBatchAsync(MutationBatchIntent intent, CancellationToken cancellationToken = default)
                => Task.FromResult(new MutationBatchIntentResult { TotalAffected = 0 });
        }

        /// <summary>Captures every log entry so a test can assert the operator-visible level (Error/Warning, not Debug).</summary>
        private sealed class CapturingLogger<T> : ILogger<T>
        {
            public readonly record struct Entry(LogLevel Level, string Message, Exception? Exception);

            public List<Entry> Entries { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
        }
    }
}
