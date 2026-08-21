using BifrostQL.Server;
using BifrostQL.Server.Test.S3;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// The direct binary-link endpoint over the REAL read pipeline (in-memory SQLite
    /// through <c>IQueryIntentExecutor</c>): bytes stream with sniffed types and Range
    /// windows, and every not-found-shaped condition — absent row, NULL value, unknown
    /// table/column, non-blob column, policy read-deny, tenant fail-closed — answers
    /// the IDENTICAL 404, so the link surface adds no oracle the GraphQL surface does
    /// not have. Fixtures span a PK value of 0 and a composite PK per the
    /// key-addressed-path fixture rule.
    /// </summary>
    public sealed class BifrostBlobMiddlewareTests : IAsyncLifetime
    {
        private static readonly byte[] Png =
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }
                .Concat(Enumerable.Range(0, 64).Select(i => (byte)i)).ToArray();
        private static readonly byte[] Pdf =
            "%PDF-1.4 minimal"u8.ToArray();
        private static readonly byte[] CompositeBytes =
            Enumerable.Range(0, 40).Select(i => (byte)(i + 3)).ToArray();

        private S3ListingRealDbHarness _harness = null!;

        public async Task InitializeAsync()
        {
            _harness = await S3ListingRealDbHarness.StartAsync(nameof(BifrostBlobMiddlewareTests), MetadataRules, SeedSql());
        }

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        private static readonly string[] MetadataRules =
        {
            "main.scoped { tenant-filter: tenant_id }",
            "main.vault { policy-actions: create }",
        };

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

        private string[] SeedSql() => new[]
        {
            "CREATE TABLE files (id INTEGER PRIMARY KEY, data BLOB)",
            // PK value 0 on purpose: a zero key must address, never read as falsy.
            $"INSERT INTO files (id, data) VALUES (0, X'{Hex(Png)}')",
            $"INSERT INTO files (id, data) VALUES (1, X'{Hex(Pdf)}')",
            "INSERT INTO files (id, data) VALUES (2, NULL)",
            "CREATE TABLE parts (a INTEGER NOT NULL, b INTEGER NOT NULL, image BLOB, PRIMARY KEY (a, b))",
            $"INSERT INTO parts (a, b, image) VALUES (1, 2, X'{Hex(CompositeBytes)}')",
            "CREATE TABLE vault (id INTEGER PRIMARY KEY, secret BLOB)",
            $"INSERT INTO vault (id, secret) VALUES (1, X'{Hex(Pdf)}')",
            "CREATE TABLE scoped (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, data BLOB)",
            $"INSERT INTO scoped (id, tenant_id, data) VALUES (1, 1, X'{Hex(Png)}')",
            "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)",
            "INSERT INTO notes (id, body) VALUES (1, 'plain text')",
        };

        private async Task<(int Status, byte[] Body, IHeaderDictionary Headers)> RequestAsync(
            string path, string? query = null, string method = "GET",
            Action<BifrostBlobOptions>? configure = null)
        {
            var options = new BifrostBlobOptions { RequireAuth = false };
            configure?.Invoke(options);
            var middleware = new BifrostBlobMiddleware(_ => Task.CompletedTask, options);
            var context = new DefaultHttpContext { RequestServices = _harness.Services };
            context.Request.Method = method;
            context.Request.Path = path;
            if (query is not null) context.Request.QueryString = new QueryString(query);
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var buffer = new MemoryStream();
            await context.Response.Body.CopyToAsync(buffer);
            return (context.Response.StatusCode, buffer.ToArray(), context.Response.Headers);
        }

        [Fact]
        public async Task Get_PngBlob_StreamsInlineWithSniffedTypeAndNoSniffHeader()
        {
            var (status, body, headers) = await RequestAsync("/_blob/files/data", "?k.id=0");

            status.Should().Be(200);
            body.Should().Equal(Png, "the exact stored bytes stream out — no base64, no re-encoding");
            headers.ContentType.ToString().Should().Be("image/png");
            headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
            headers.ContentDisposition.ToString().Should().StartWith("inline;");
            headers["Accept-Ranges"].ToString().Should().Be("bytes");
        }

        [Fact]
        public async Task Get_PdfBlob_IsAnAttachment_NeverInline()
        {
            var (status, body, headers) = await RequestAsync("/_blob/files/data", "?k.id=1");

            status.Should().Be(200);
            body.Should().Equal(Pdf);
            headers.ContentType.ToString().Should().Be("application/pdf");
            headers.ContentDisposition.ToString().Should().StartWith("attachment;",
                "only magic-byte-verified images render inline on this origin");
        }

        [Fact]
        public async Task Get_WithRange_ServesAWindow_AndUnsatisfiableRangeIs416()
        {
            var context = await RequestWithHeaderAsync("bytes=8-17");
            context.Status.Should().Be(206);
            context.Body.Should().Equal(Png.Skip(8).Take(10).ToArray());
            context.Headers.ContentRange.ToString().Should().Be($"bytes 8-17/{Png.Length}");

            var suffix = await RequestWithHeaderAsync("bytes=-4");
            suffix.Status.Should().Be(206);
            suffix.Body.Should().Equal(Png.Skip(Png.Length - 4).ToArray());

            var beyond = await RequestWithHeaderAsync($"bytes={Png.Length + 10}-");
            beyond.Status.Should().Be(416);
            beyond.Headers.ContentRange.ToString().Should().Be($"bytes */{Png.Length}");
        }

        private async Task<(int Status, byte[] Body, IHeaderDictionary Headers)> RequestWithHeaderAsync(string range)
        {
            var middleware = new BifrostBlobMiddleware(_ => Task.CompletedTask, new BifrostBlobOptions { RequireAuth = false });
            var context = new DefaultHttpContext { RequestServices = _harness.Services };
            context.Request.Method = "GET";
            context.Request.Path = "/_blob/files/data";
            context.Request.QueryString = new QueryString("?k.id=0");
            context.Request.Headers.Range = range;
            context.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(context);
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var buffer = new MemoryStream();
            await context.Response.Body.CopyToAsync(buffer);
            return (context.Response.StatusCode, buffer.ToArray(), context.Response.Headers);
        }

        [Fact]
        public async Task Head_ReturnsHeadersAndLength_WithoutABody()
        {
            var (status, body, headers) = await RequestAsync("/_blob/files/data", "?k.id=0", method: "HEAD");

            status.Should().Be(200);
            body.Should().BeEmpty();
            headers.ContentLength.Should().Be(Png.Length);
        }

        [Fact]
        public async Task CompositeKey_RequiresEveryKeyColumn()
        {
            var (okStatus, okBody, _) = await RequestAsync("/_blob/parts/image", "?k.a=1&k.b=2");
            okStatus.Should().Be(200);
            okBody.Should().Equal(CompositeBytes);

            var (missing, body, _) = await RequestAsync("/_blob/parts/image", "?k.a=1");
            missing.Should().Be(400);
            System.Text.Encoding.UTF8.GetString(body).Should().Contain("k.b",
                "a composite key is addressed in full — never a first-column guess");
        }

        [Theory]
        [InlineData("/_blob/files/data", "?k.id=9000")]          // absent row
        [InlineData("/_blob/files/data", "?k.id=2")]             // NULL value
        [InlineData("/_blob/ghosts/data", "?k.id=1")]            // unknown table
        [InlineData("/_blob/files/nope", "?k.id=1")]             // unknown column
        [InlineData("/_blob/notes/body", "?k.id=1")]             // non-blob column
        [InlineData("/_blob/vault/secret", "?k.id=1")]           // policy read-deny
        [InlineData("/_blob/scoped/data", "?k.id=1")]            // tenant filter, no tenant context: fail closed
        public async Task EveryNotFoundShapedCondition_AnswersTheIdentical404(string path, string query)
        {
            var (status, body, _) = await RequestAsync(path, query);

            status.Should().Be(404);
            System.Text.Encoding.UTF8.GetString(body).Should().Be("Not found.",
                "denied, hidden, mistyped and absent must be indistinguishable on this surface");
        }

        [Fact]
        public async Task OverTheByteCap_IsAnExplicit413_NeverATruncatedBody()
        {
            var (status, body, _) = await RequestAsync("/_blob/files/data", "?k.id=0",
                configure: o => o.MaxBlobBytes = 8);

            status.Should().Be(413);
            System.Text.Encoding.UTF8.GetString(body).Should().Contain("8-byte");
        }

        [Fact]
        public async Task RequireAuth_RefusesAnAnonymousCaller()
        {
            var (status, _, _) = await RequestAsync("/_blob/files/data", "?k.id=0",
                configure: o => o.RequireAuth = true);

            status.Should().Be(401);
        }

        [Fact]
        public async Task NonReadMethods_Are405()
        {
            var (status, _, headers) = await RequestAsync("/_blob/files/data", "?k.id=0", method: "POST");

            status.Should().Be(405);
            headers.Allow.ToString().Should().Contain("GET");
        }
    }
}
