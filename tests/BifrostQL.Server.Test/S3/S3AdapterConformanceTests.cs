using System.Xml.Linq;
using BifrostQL.Server.S3;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// Security conformance for the S3 protocol adapter, proving — as a single coherent matrix
    /// across every operation class — the two properties the shared conformance kit
    /// (<c>ProtocolAdapterConformanceTests</c>) exists to prove for the other adapters: that a
    /// caller's identity is projected through the ONE shared auth seam, and that the
    /// query/mutation seams cannot be skipped around.
    ///
    /// <para><b>Why S3 does not DERIVE <c>ProtocolAdapterConformanceTests</c>.</b> That kit is
    /// shaped for a <c>IProtocolAdapter</c> registered via <c>AddProtocolAdapter&lt;T&gt;</c>
    /// whose wire carries a generic relational read/write — a request expressed as a table name,
    /// a column list, and a GraphQL-shaped filter (see <c>ConformanceReadRequest</c> /
    /// <c>ConformanceMutationRequest</c>). The S3 front door has neither shape: it is HTTP
    /// middleware (not an <c>IProtocolAdapter</c>), its wire object is a bucket/key addressing a
    /// single FILE COLUMN's blob (not an arbitrary column projection), and it has no wire verb
    /// that reads columns <c>X, Y</c> of table <c>T</c> or inserts a new row. Forcing S3 through
    /// the kit's read/mutation request model would require faking a request shape the protocol
    /// cannot express, which proves nothing about the real S3 request path. So instead of
    /// deriving the kit, this class proves the SAME underlying invariants against S3's actual
    /// operation classes.</para>
    ///
    /// <para><b>Shared auth projection (this class).</b> Every S3 operation class routes through
    /// <see cref="S3SigV4Verifier"/> — which projects the resolved principal through the shared
    /// <c>IBifrostAuthContextFactory</c>, fail-closed — BEFORE any lister or object seam is
    /// consulted. The matrix below drives every operation class (list-buckets, list-objects,
    /// get, head, put, delete, copy) with a tampered signature over an exploding lister and
    /// seam: each is rejected with a clean 403 at the auth gate, and the fact that the exploding
    /// data path never runs (a 403, not the 404/500 the explosion would produce) is the proof
    /// that the shared identity gate is applied and unskippable for that class.</para>
    ///
    /// <para><b>Unskippable query/mutation seams (companion tests).</b> The middleware's only
    /// data paths ARE the lister (over <c>IQueryIntentExecutor</c>) and the object seam (over
    /// <c>IQueryIntentExecutor</c> reads / <c>IMutationIntentExecutor</c> writes) — there is no
    /// raw-SQL side door. That every authorized operation class actually travels those seams
    /// (so tenant scoping, soft-delete, and policy gating apply unskippably) is proven end to
    /// end against the real pipeline by <see cref="S3ListMiddlewareTests"/> (list),
    /// <see cref="S3GetObjectMiddlewareTests"/> (get/head), and
    /// <see cref="S3PutDeleteMiddlewareTests"/> (put/delete/copy), plus the seam-level
    /// cross-tenant proofs in <c>FileResolverSecurityTests</c>.</para>
    /// </summary>
    public sealed class S3AdapterConformanceTests
    {
        private static readonly DateTimeOffset SignTime = new(2026, 07, 16, 12, 00, 00, TimeSpan.Zero);

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        /// <summary>A read seam that throws if the auth gate ever lets a request reach it.</summary>
        private sealed class ExplodingReads : BifrostQL.Core.Resolvers.IQueryIntentExecutor
        {
            public Task<BifrostQL.Core.Model.IDbModel> GetModelAsync(string? endpoint = null)
                => throw new InvalidOperationException("data path reached before the auth gate rejected the request");

            public Task<BifrostQL.Core.Resolvers.QueryIntentResult> ExecuteAsync(
                BifrostQL.Core.Resolvers.QueryIntent intent, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("data path reached before the auth gate rejected the request");
        }

        /// <summary>A write seam that throws if the auth gate ever lets a request reach it.</summary>
        private sealed class ExplodingWrites : BifrostQL.Core.Resolvers.IMutationIntentExecutor
        {
            public Task<BifrostQL.Core.Resolvers.MutationIntentResult> ExecuteAsync(
                BifrostQL.Core.Resolvers.MutationIntent intent, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("data path reached before the auth gate rejected the request");

            public Task<BifrostQL.Core.Resolvers.MutationBatchIntentResult> ExecuteBatchAsync(
                BifrostQL.Core.Resolvers.MutationBatchIntent intent, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("data path reached before the auth gate rejected the request");
        }

        private static S3Middleware Build()
        {
            // Writes stay ENABLED so the write dispatch is genuinely wired: were the auth gate
            // skippable for a write class, the request would reach the exploding seam rather than
            // short-circuiting on the (disabled) 501 path — the gate-first proof must not be
            // confounded by the write toggle.
            var opts = new S3Options { Region = S3TestSigner.Region, Endpoint = "/graphql", EnableWrites = true };
            var keyStore = new FakeS3AccessKeyStore().Add(
                S3TestSigner.AccessKeyId, S3TestSigner.Secret, S3TestSigner.Principal("s3-user", tenant: "tenant-a"));
            var verifier = new S3SigV4Verifier(keyStore, BifrostAuthContextFactory.Instance, opts, new FixedClock(SignTime));
            var listing = new S3Listing(new ExplodingReads(), opts);
            var seam = new BifrostQL.Core.Storage.FileObjectSeam(
                new ExplodingReads(), new ExplodingWrites(), null,
                new BifrostQL.Core.Storage.FileObjectSeamOptions { Endpoint = "/graphql", EnableWrites = true });
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

        public static IEnumerable<object[]> OperationClasses() => new[]
        {
            new object[] { "list-buckets", "GET", "/", null! },
            new object[] { "list-objects", "GET", "/notes", "?list-type=2" },
            new object[] { "get-object", "GET", "/notes/content/alpha", null! },
            new object[] { "head-object", "HEAD", "/notes/content/alpha", null! },
            new object[] { "put-object", "PUT", "/notes/content/alpha", null! },
            new object[] { "delete-object", "DELETE", "/notes/content/alpha", null! },
            new object[] { "copy-object", "PUT", "/notes/content/beta", null! },
        };

        [Theory]
        [MemberData(nameof(OperationClasses))]
        public async Task Every_operation_class_is_gated_by_the_shared_identity_seam_before_any_data_seam(
            string operationClass, string method, string path, string? query)
        {
            // A validly-signed request, then its signature is corrupted: the request is now
            // unauthenticated. The shared SigV4 identity gate must reject it BEFORE the lister
            // or object seam is touched.
            var ctx = S3TestSigner.BuildHeaderSigned(method: method, path: path, query: query, signTime: SignTime);
            if (operationClass == "copy-object")
                ctx.Request.Headers["x-amz-copy-source"] = "/notes/content/alpha";
            var auth = ctx.Request.Headers["Authorization"].ToString();
            ctx.Request.Headers["Authorization"] = auth[..^8] + "deadbeef";

            var (status, xml) = await Run(Build(), ctx);

            // A clean 403 at the gate. Had the gate been skippable for this class, the request
            // would have reached the exploding seam and surfaced as a 404 (read addressing fault)
            // or 500 (internal) instead — so 403 here is the proof the identity seam ran first
            // and no data path was consulted for an unauthenticated caller.
            status.Should().Be(403, $"the {operationClass} class must be gated by the shared identity seam first");
            xml.Name.LocalName.Should().Be("Error");
            xml.Element("Code")!.Value.Should().Be("SignatureDoesNotMatch");
        }
    }
}
