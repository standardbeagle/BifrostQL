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
    /// Cross-op-class parity for the S3 seam's error mapping
    /// (.claude/rules/protocol-adapter-security.md invariants 9 and 10). Per-slice tests each
    /// assert their own op class against its own acceptance criteria and are structurally blind
    /// to divergence BETWEEN op classes; these facts diff the op classes against each other.
    ///
    /// <para>The contract: ONE condition (a missing tenant claim, a policy read-deny) must
    /// produce ONE wire signal class across ListObjectsV2, GetObject, HeadObject, PutObject and
    /// DeleteObject. A single op class answering <c>500 InternalError</c> where its siblings
    /// answer a clean non-enumerating 404/204 is an existence/authorization ORACLE: the caller
    /// learns from the 500 that the address resolved far enough to reach the read pipeline.</para>
    ///
    /// <para>Fixture note: every caller here is DELIBERATELY tenant-less or read-denied. A suite
    /// of authenticated, fully-authorized callers cannot manifest this class of bug at all.</para>
    /// </summary>
    public sealed class S3ErrorMappingParityTests : IAsyncLifetime
    {
        private const string Endpoint = "/graphql";
        private static readonly DateTimeOffset SignTime = new(2026, 07, 16, 12, 00, 00, TimeSpan.Zero);

        // A key whose principal carries NO tenant claim: every read against a tenant-filtered
        // table fails closed inside the pipeline with BifrostExecutionError.
        private const string TenantlessKey = "AKIANOTENANT";
        private const string TenantlessSecret = "no-tenant-wJalrXUtnFEMI/K7MDENG";

        private S3ListingRealDbHarness _harness = null!;

        private static readonly string[] MetadataRules =
        {
            "main.assets { tenant-filter: tenant_id }",
            "main.assets.data { file: json }",
            // Table-level read is granted, but the FILE column itself is read-denied — the
            // listing explicitly selects file columns, so it hits the column read guard.
            "main.vaults { policy-actions: read; policy-read-deny: blob }",
            "main.vaults.blob { file: json }",
        };

        private static string[] SeedSql() => new[]
        {
            "DROP TABLE IF EXISTS assets",
            "DROP TABLE IF EXISTS vaults",
            "CREATE TABLE assets (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, data TEXT)",
            $"INSERT INTO assets(id, tenant_id, data) VALUES (1, 'tenant-a', '{Pointer()}')",
            "CREATE TABLE vaults (id INTEGER PRIMARY KEY, blob TEXT)",
            $"INSERT INTO vaults(id, blob) VALUES (1, '{Pointer()}')",
        };

        private static string Pointer() =>
            new FileMetadata
            {
                FileKey = "k",
                Size = 7,
                ETag = "abc",
                UploadedAt = new DateTime(2026, 07, 16, 8, 30, 00, DateTimeKind.Utc),
            }.ToJson().Replace("'", "''");

        public async Task InitializeAsync()
            => _harness = await S3ListingRealDbHarness.StartAsync(
                nameof(S3ErrorMappingParityTests), MetadataRules, SeedSql());

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        // ---- condition A: a caller with NO tenant claim on a tenant-filtered bucket ---------

        [Fact]
        public async Task A_tenantless_caller_gets_the_same_not_found_class_from_every_read_op()
        {
            var list = await Run(Signed("GET", "/assets", "?list-type=2"));
            var get = await Run(Signed("GET", "/assets/data/1"));
            var head = await Run(Signed("HEAD", "/assets/data/1"));

            // No op class may leak a 500: that would tell a tenant-less caller its address
            // resolved past the bucket gate and reached the pipeline.
            list.Status.Should().Be(404);
            get.Status.Should().Be(404);
            head.Status.Should().Be(404);
            list.Status.Should().Be(get.Status, "list and get must not diverge on one condition");
        }

        [Fact]
        public async Task A_tenantless_caller_gets_the_same_not_found_class_from_every_write_op()
        {
            var put = await Run(SignedPut("/assets/data/1", "payload"u8.ToArray()), enableWrites: true);
            var del = await Run(Signed("DELETE", "/assets/data/1"), enableWrites: true);
            var list = await Run(Signed("GET", "/assets", "?list-type=2"), enableWrites: true);

            put.Status.Should().Be(404);
            del.Status.Should().Be(204, "S3 delete is idempotent by contract");
            list.Status.Should().NotBe(500,
                "the list op class must not be the one that reports a server fault for a " +
                "condition every write op treats as a clean, non-enumerating answer");
        }

        // ---- condition B: a policy read-deny on the file column itself ----------------------

        [Fact]
        public async Task A_read_denied_file_column_gets_the_same_not_found_class_from_list_and_get()
        {
            var list = await Run(Signed("GET", "/vaults", "?list-type=2"));
            var get = await Run(Signed("GET", "/vaults/blob/1"));

            get.Status.Should().Be(404);
            list.Status.Should().Be(404);
            list.Code.Should().NotBe("InternalError");
        }

        // ---- the funnel keeps genuine faults distinguishable from denials -------------------

        [Fact]
        public async Task An_unknown_bucket_still_answers_no_such_bucket()
        {
            // The widened mapping must not swallow the ordinary addressing answers.
            var list = await Run(Signed("GET", "/nosuchbucket", "?list-type=2"));

            list.Status.Should().Be(404);
            list.Code.Should().Be("NoSuchBucket");
        }

        // ---- helpers -----------------------------------------------------------------------

        private async Task<(int Status, string? Code)> Run(DefaultHttpContext ctx, bool enableWrites = false)
        {
            var middleware = Build(enableWrites);
            ctx.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(ctx);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            string? code = null;
            if (body.Length > 0)
            {
                try { code = XElement.Parse(body).Element("Code")?.Value; }
                catch (System.Xml.XmlException) { /* a success body is not an error envelope */ }
            }
            return (ctx.Response.StatusCode, code);
        }

        private S3Middleware Build(bool enableWrites)
        {
            var opts = new S3Options
            {
                Region = S3TestSigner.Region,
                Endpoint = Endpoint,
                EnableWrites = enableWrites,
                ContinuationTokenSecret = "parity-tests-secret",
            };
            var keyStore = new FakeS3AccessKeyStore().Add(
                TenantlessKey, TenantlessSecret, S3TestSigner.Principal("no-tenant-user", tenant: null));
            var verifier = new S3SigV4Verifier(keyStore, BifrostAuthContextFactory.Instance, opts, new FixedClock(SignTime));
            RequestDelegate next = _ => Task.CompletedTask;
            return new S3Middleware(
                next, opts, verifier, _harness.Listing(opts),
                _harness.Seam(options: opts, enableWrites: enableWrites),
                NullLogger<S3Middleware>.Instance);
        }

        private static DefaultHttpContext Signed(string method, string path, string? query = null)
            => S3TestSigner.BuildHeaderSigned(
                method: method, path: path, query: query, signTime: SignTime,
                secret: TenantlessSecret, accessKeyId: TenantlessKey);

        private static DefaultHttpContext SignedPut(string path, byte[] body)
        {
            var ctx = S3TestSigner.BuildHeaderSigned(
                method: "PUT", path: path, signTime: SignTime,
                secret: TenantlessSecret, accessKeyId: TenantlessKey,
                payloadHash: S3SigV4.HashSha256Hex(body));
            ctx.Request.Body = new MemoryStream(body);
            ctx.Request.ContentLength = body.Length;
            ctx.Request.Headers.ContentType = "text/plain";
            return ctx;
        }

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
