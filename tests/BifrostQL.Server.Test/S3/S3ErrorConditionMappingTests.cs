using System.Xml.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Storage;
using BifrostQL.Server.S3;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// The S3 error funnel maps by CONDITION CODE, never by exception type
    /// (.claude/rules/protocol-adapter-security.md invariant 10). Only <c>NOT_FOUND</c> and
    /// <c>ACCESS_DENIED</c> answer the non-enumerating 404; <c>CONFLICT</c> and
    /// <c>EXCLUSION_VIOLATION</c> answer 409; every other failure is a fault that answers a
    /// sanitized 500 and is logged at Error. DeleteObject answers 204 only when the object is gone
    /// for the caller, never when the delete did not happen.
    ///
    /// <para>Before the fix the funnel caught <c>InvalidOperationException or
    /// BifrostExecutionError</c> by type and logged at Debug, so an exhausted pool, an
    /// <see cref="ObjectDisposedException"/> (an InvalidOperationException) and a CONFLICT all
    /// read as a missing object, and DeleteObject answered 204 for all of them while the row kept
    /// its object.</para>
    /// </summary>
    public sealed class S3ErrorConditionMappingTests : IAsyncLifetime
    {
        private const string Endpoint = "/graphql";
        private const string SecretDetail = "pool-exhausted-detail-must-stay-off-the-wire";
        private static readonly DateTimeOffset SignTime = new(2026, 07, 16, 12, 00, 00, TimeSpan.Zero);
        private static readonly byte[] Existing = "the stored object"u8.ToArray();

        private const string KeyA = "AKIATENANTA";
        private const string SecretA = "secretA-wJalrXUtnFEMI/K7MDENG";

        private string _tempDir = null!;
        private S3ListingRealDbHarness _harness = null!;

        private static readonly string[] MetadataRules =
        {
            "main.assets { tenant-filter: tenant_id }",
            "main.assets.data { file: json }",
        };

        public async Task InitializeAsync()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "s3cond_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "blobs"));
            await File.WriteAllBytesAsync(Path.Combine(_tempDir, "blobs", "existing"), Existing);

            var pointer = new FileMetadata
            {
                FileKey = "blobs/existing",
                Size = Existing.Length,
                ContentType = "text/plain",
                ETag = "abc",
                UploadedAt = new DateTime(2026, 07, 15, 9, 15, 0, DateTimeKind.Utc),
            }.ToJson().Replace("'", "''");

            _harness = await S3ListingRealDbHarness.StartAsync(nameof(S3ErrorConditionMappingTests), MetadataRules, new[]
            {
                "DROP TABLE IF EXISTS assets",
                "CREATE TABLE assets (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, data TEXT)",
                $"INSERT INTO assets(id, tenant_id, data) VALUES (1, 'tenant-a', '{pointer}'), (5, 'tenant-a', NULL)",
            });
        }

        public async Task DisposeAsync()
        {
            await _harness.DisposeAsync();
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        // ---- faults answer 500 and log at Error, on every op class --------------------------

        public static TheoryData<string, string> FaultsByOpClass()
        {
            var data = new TheoryData<string, string>();
            foreach (var fault in new[] { "pool", "disposed", "database", "uncoded" })
                foreach (var op in new[] { "GET", "HEAD", "DELETE", "LIST" })
                    data.Add(fault, op);
            return data;
        }

        [Theory]
        [MemberData(nameof(FaultsByOpClass))]
        public async Task A_read_fault_answers_500_and_logs_at_error_not_404(string fault, string op)
        {
            var log = new FakeLogger<S3Middleware>();
            var reads = new ThrowingReads(Fault(fault));
            var ctx = op switch
            {
                "LIST" => Signed("GET", "/assets", "?list-type=2"),
                _ => Signed(op, "/assets/data/1"),
            };

            var (status, code, body) = await Run(ctx, Build(reads, _harness.Writes, log));

            status.Should().Be(500, $"{fault} on {op} is a server fault, not a missing object");
            if (op != "HEAD")
                code.Should().Be("InternalError");
            body.Should().NotContain(SecretDetail, "fault detail never reaches the wire (invariant 3)");
            log.Collector.GetSnapshot().Should().Contain(e => e.Level == LogLevel.Error && e.Exception != null,
                "an operator must see the fault above Debug");
        }

        // ---- denial and absence still answer the non-enumerating 404 -----------------------

        [Theory]
        [InlineData(BifrostExecutionError.AccessDeniedCode)]
        [InlineData(BifrostExecutionError.NotFoundCode)]
        public async Task A_denial_or_absence_code_answers_no_such_key(string errorCode)
        {
            var log = new FakeLogger<S3Middleware>();
            var reads = new ThrowingReads(new BifrostExecutionError(SecretDetail) { ErrorCode = errorCode });

            var (status, code, body) = await Run(Signed("GET", "/assets/data/1"), Build(reads, _harness.Writes, log));

            status.Should().Be(404);
            code.Should().Be("NoSuchKey");
            body.Should().NotContain(SecretDetail);
            log.Collector.GetSnapshot().Should().NotContain(e => e.Level >= LogLevel.Warning);
        }

        // ---- conflicts answer 409 -----------------------------------------------------------

        [Theory]
        [InlineData(BifrostExecutionError.ConflictCode)]
        [InlineData(BifrostExecutionError.ExclusionViolationCode)]
        public async Task A_conflicting_write_answers_409_not_404(string errorCode)
        {
            var log = new FakeLogger<S3Middleware>();
            var writes = new ThrowingWrites(new BifrostExecutionError(SecretDetail) { ErrorCode = errorCode });
            var body = "replacement"u8.ToArray();
            var ctx = S3TestSigner.BuildHeaderSigned(
                method: "PUT", path: "/assets/data/1", signTime: SignTime,
                secret: SecretA, accessKeyId: KeyA, payloadHash: S3SigV4.HashSha256Hex(body));
            ctx.Request.Body = new MemoryStream(body);
            ctx.Request.ContentLength = body.Length;

            var (status, code, wire) = await Run(ctx, Build(_harness.Reads, writes, log));

            status.Should().Be(409);
            code.Should().Be("OperationAborted");
            wire.Should().NotContain(SecretDetail);
        }

        // ---- DeleteObject answers 204 only when the object is gone --------------------------

        [Fact]
        public async Task A_delete_whose_write_faults_is_500_and_the_object_survives()
        {
            var log = new FakeLogger<S3Middleware>();
            var writes = new ThrowingWrites(BifrostExecutionError.FromDatabaseException(new InvalidOperationException(SecretDetail)));

            var (status, code, body) = await Run(Signed("DELETE", "/assets/data/1"), Build(_harness.Reads, writes, log));

            status.Should().Be(500, "the pointer was not cleared, so the delete did not happen");
            code.Should().Be("InternalError");
            body.Should().NotContain(SecretDetail);
            log.Collector.GetSnapshot().Should().Contain(e => e.Level == LogLevel.Error);
            (await GetStatus()).Should().Be(200, "the object is still there");
        }

        [Fact]
        public async Task A_delete_whose_write_matches_zero_rows_is_not_204_and_the_object_survives()
        {
            // The caller can see the row, but the pointer write affected no rows (scoped away by
            // the write pipeline). The object is still stored and still readable, so 204 would lie.
            var log = new FakeLogger<S3Middleware>();

            var (status, code, _) = await Run(
                Signed("DELETE", "/assets/data/1"), Build(_harness.Reads, new ZeroRowWrites(), log));

            status.Should().NotBe(204);
            status.Should().Be(404);
            code.Should().Be("NoSuchKey");
            (await GetStatus()).Should().Be(200, "the object is still there");
        }

        [Fact]
        public async Task A_delete_of_a_target_the_caller_cannot_see_stays_an_idempotent_204()
        {
            // Control for the two facts above: the 204 they forbid is still produced where the
            // object is absent from the caller's view.
            var (status, _, _) = await Run(
                Signed("DELETE", "/assets/data/999"), Build(_harness.Reads, _harness.Writes, new FakeLogger<S3Middleware>()));

            status.Should().Be(204);
        }

        // ---- helpers ------------------------------------------------------------------------

        private static Exception Fault(string kind) => kind switch
        {
            "pool" => new InvalidOperationException(
                "Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool. " + SecretDetail),
            "disposed" => new ObjectDisposedException("SqliteConnection", SecretDetail),
            "database" => BifrostExecutionError.FromDatabaseException(new InvalidOperationException(SecretDetail)),
            "uncoded" => new BifrostExecutionError(SecretDetail),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        private async Task<int> GetStatus()
        {
            var ctx = Signed("GET", "/assets/data/1");
            ctx.Response.Body = new MemoryStream();
            await Build(_harness.Reads, _harness.Writes, new FakeLogger<S3Middleware>()).InvokeAsync(ctx);
            return ctx.Response.StatusCode;
        }

        private S3Middleware Build(IQueryIntentExecutor reads, IMutationIntentExecutor writes, ILogger<S3Middleware> logger)
        {
            var opts = new S3Options
            {
                Region = S3TestSigner.Region,
                Endpoint = Endpoint,
                EnableWrites = true,
                ContinuationTokenSecret = "condition-mapping-secret",
            };
            var keyStore = new FakeS3AccessKeyStore().Add(KeyA, SecretA, S3TestSigner.Principal("user-a", tenant: "tenant-a"));
            var verifier = new S3SigV4Verifier(keyStore, BifrostAuthContextFactory.Instance, opts, new FixedClock(SignTime));
            var storage = new FileStorageService(
                databaseDefaultConfig: new StorageBucketConfig { BucketName = _tempDir, ProviderType = "local" });
            var seam = new FileObjectSeam(reads, writes, storage,
                new FileObjectSeamOptions { Endpoint = Endpoint, EnableWrites = true });
            RequestDelegate next = _ => Task.CompletedTask;
            return new S3Middleware(next, opts, verifier, new S3Listing(reads, opts), seam, logger);
        }

        private static DefaultHttpContext Signed(string method, string path, string? query = null)
            => S3TestSigner.BuildHeaderSigned(
                method: method, path: path, query: query, signTime: SignTime, secret: SecretA, accessKeyId: KeyA);

        private static async Task<(int Status, string? Code, string Body)> Run(DefaultHttpContext ctx, S3Middleware middleware)
        {
            ctx.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(ctx);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            string? code = null;
            if (body.Length > 0)
            {
                try { code = XElement.Parse(body).Element("Code")?.Value; }
                catch (System.Xml.XmlException) { /* not an error envelope */ }
            }
            return (ctx.Response.StatusCode, code, body);
        }

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        /// <summary>Every read throws the configured exception, as a failing database would.</summary>
        private sealed class ThrowingReads(Exception fault) : IQueryIntentExecutor
        {
            public Task<IDbModel> GetModelAsync(string? endpoint = null) => Task.FromException<IDbModel>(fault);

            public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
                => Task.FromException<QueryIntentResult>(fault);
        }

        /// <summary>Every write throws the configured exception.</summary>
        private sealed class ThrowingWrites(Exception fault) : IMutationIntentExecutor
        {
            public Task<MutationIntentResult> ExecuteAsync(MutationIntent intent, CancellationToken cancellationToken = default)
                => Task.FromException<MutationIntentResult>(fault);

            public Task<MutationBatchIntentResult> ExecuteBatchAsync(MutationBatchIntent intent, CancellationToken cancellationToken = default)
                => Task.FromException<MutationBatchIntentResult>(fault);
        }

        /// <summary>Every write reports zero affected rows: the write pipeline scoped the row away.</summary>
        private sealed class ZeroRowWrites : IMutationIntentExecutor
        {
            public Task<MutationIntentResult> ExecuteAsync(MutationIntent intent, CancellationToken cancellationToken = default)
                => Task.FromResult(new MutationIntentResult { AffectedRows = 0 });

            public Task<MutationBatchIntentResult> ExecuteBatchAsync(MutationBatchIntent intent, CancellationToken cancellationToken = default)
                => Task.FromResult(new MutationBatchIntentResult { TotalAffected = 0 });
        }
    }
}
