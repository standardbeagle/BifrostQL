using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using BifrostQL.Server.Test.S3;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
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
            var middleware = new BifrostBlobMiddleware(_ => Task.CompletedTask, options, options.RequireAuth!.Value);
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
            var middleware = new BifrostBlobMiddleware(_ => Task.CompletedTask, new BifrostBlobOptions(), requiresAuth: false);
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

        // Invariant 5: Range bounds are digits only. long.TryParse's default style admits a sign
        // and surrounding whitespace, so "bytes=+0-" was served as a 206 window from byte 0.
        [Theory]
        [InlineData("bytes=+0-")]
        [InlineData("bytes=0-+5")]
        [InlineData("bytes=-+4")]
        [InlineData("bytes= 0-5")]
        public async Task A_Range_bound_that_is_not_all_digits_is_ignored_and_serves_the_full_body(string range)
        {
            var (status, body, headers) = await RequestWithHeaderAsync(range);

            status.Should().Be(200, "a malformed Range is ignored (RFC 9110), never honoured");
            body.Should().Equal(Png);
            headers.ContentRange.ToString().Should().BeEmpty();
        }

        [Fact]
        public async Task A_zero_length_suffix_Range_is_unsatisfiable()
        {
            var (status, _, headers) = await RequestWithHeaderAsync("bytes=-0");

            status.Should().Be(416);
            headers.ContentRange.ToString().Should().Be($"bytes */{Png.Length}");
        }

        // Invariant 5: the key grammar is invariant. Under a culture whose positive sign is "PLUS",
        // the culture-default parse read k.id=PLUS0 as 0 and served the row.
        [Fact]
        public async Task An_integer_key_is_parsed_with_the_invariant_culture()
        {
            var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.PositiveSign = "PLUS";
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = culture;
            try
            {
                var (status, body, _) = await RequestAsync("/_blob/files/data", "?k.id=PLUS0");
                status.Should().Be(404);
                System.Text.Encoding.UTF8.GetString(body).Should().Be("Not found.");

                // Control: the invariant grammar still addresses the row, sign included.
                var (okStatus, okBody, _) = await RequestAsync("/_blob/files/data", "?k.id=%2B0");
                okStatus.Should().Be(200);
                okBody.Should().Equal(Png);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
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
            missing.Should().Be(404);
            System.Text.Encoding.UTF8.GetString(body).Should().Be("Not found.",
                "a partial composite key is a shape check — it must not name the missing column");
        }

        // Finding M12: every shape check runs BEFORE the pipeline gate, so a 400 that
        // names the PK column/type — where an unknown or read-denied table answers 404 —
        // lets a read-denied caller enumerate tables, blob columns and key names.
        [Theory]
        [InlineData("/_blob/ghosts/data", "?k.id=1")]   // unknown table
        [InlineData("/_blob/vault/secret", "?k.id=1")]  // policy read-denied table
        [InlineData("/_blob/vault/secret", null)]       // denied table, no key at all
        [InlineData("/_blob/files/data", null)]         // known table, missing key
        [InlineData("/_blob/files/data", "?k.id=abc")]  // known table, malformed key
        public async Task ShapeChecks_NeverPreemptThePipelineGate_AllAnswerTheIdentical404(
            string path, string? query)
        {
            var (status, body, _) = await RequestAsync(path, query);

            status.Should().Be(404);
            System.Text.Encoding.UTF8.GetString(body).Should().Be("Not found.",
                "unknown, denied and malformed-key must be indistinguishable — no identifiers on the wire");
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

        [Theory]
        [InlineData(8, false)]
        [InlineData(1024, true)]
        public async Task TheByteCap_IsCheckedInTheDatabase_BeforeTheValueIsRead(int maxBlobBytes, bool served)
        {
            // An oversized value used to be read whole (as base64) and only then refused, so
            // every refused fetch still cost its size in server memory. The size is now measured
            // in the database first; the value column is selected only when it fits.
            var recorder = new RecordingReads(_harness.Reads);
            var options = new BifrostBlobOptions { MaxBlobBytes = maxBlobBytes };
            var middleware = new BifrostBlobMiddleware(_ => Task.CompletedTask, options, requiresAuth: false);
            var context = new DefaultHttpContext { RequestServices = new ReadsOverride(_harness.Services, recorder) };
            context.Request.Method = "GET";
            context.Request.Path = "/_blob/files/data";
            context.Request.QueryString = new QueryString("?k.id=0");
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            context.Response.StatusCode.Should().Be(served ? 200 : 413);
            recorder.Selected.Should().NotBeEmpty().And.OnlyContain(c => c.Count == 1);
            recorder.Selected[0].Single().ComputedColumn.Should().NotBeNull("the first read measures the size");
            var valueReads = recorder.Selected.Count(c => c.Single().ComputedColumn is null && c.Single().DbDbName == "data");
            valueReads.Should().Be(served ? 1 : 0,
                served ? "control: a value under the cap is read and served" : "an oversized value is never selected");
        }

        /// <summary>Records every select list sent through the real read seam.</summary>
        private sealed class RecordingReads : IQueryIntentExecutor
        {
            private readonly IQueryIntentExecutor _inner;
            public RecordingReads(IQueryIntentExecutor inner) => _inner = inner;
            public List<IReadOnlyList<GqlObjectColumn>> Selected { get; } = new();
            public Task<IDbModel> GetModelAsync(string? endpoint = null) => _inner.GetModelAsync(endpoint);
            public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
            {
                Selected.Add(intent.Query.ScalarColumns.ToList());
                return _inner.ExecuteAsync(intent, cancellationToken);
            }
        }

        private sealed class ReadsOverride : IServiceProvider
        {
            private readonly IServiceProvider _inner;
            private readonly IQueryIntentExecutor _reads;
            public ReadsOverride(IServiceProvider inner, IQueryIntentExecutor reads) => (_inner, _reads) = (inner, reads);
            public object? GetService(Type serviceType) =>
                serviceType == typeof(IQueryIntentExecutor) ? _reads : _inner.GetService(serviceType);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void Base64DecodedLength_MatchesTheDecodedArray_ForEveryPadding(int byteCount)
        {
            // The 413 check runs on this arithmetic instead of decoding, so it must agree with
            // the decoder exactly at every padding length (0, 1 and 2 '=' characters).
            var encoded = Convert.ToBase64String(Enumerable.Range(0, byteCount).Select(i => (byte)i).ToArray());

            BifrostBlobMiddleware.Base64DecodedLength(encoded).Should().Be(byteCount);
        }

        [Fact]
        public void Base64DecodedLength_RefusesANonCanonicalLength()
        {
            BifrostBlobMiddleware.Base64DecodedLength("abc").Should().BeNull();
        }

        private const string SecretDetail = "connection to db-internal-7 refused";

        private async Task<(int Status, string Body, FakeLogger<BifrostBlobMiddleware> Log)> RequestWithReadFaultAsync(Exception fault)
        {
            var log = new FakeLogger<BifrostBlobMiddleware>();
            var middleware = new BifrostBlobMiddleware(_ => Task.CompletedTask, new BifrostBlobOptions(), requiresAuth: false, log);
            var context = new DefaultHttpContext { RequestServices = new ReadsOverride(_harness.Services, new ThrowingReads(_harness.Reads, fault)) };
            context.Request.Method = "GET";
            context.Request.Path = "/_blob/files/data";
            context.Request.QueryString = new QueryString("?k.id=0");
            context.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(context);
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body);
            return (context.Response.StatusCode, await reader.ReadToEndAsync(), log);
        }

        [Fact]
        public async Task A_database_fault_answers_500_and_logs_at_error_not_404()
        {
            // SqlExecutionManager wraps every driver fault as a BifrostExecutionError, so a
            // type-matched catch read a database outage as "Not found." with nothing logged.
            var (status, body, log) = await RequestWithReadFaultAsync(
                BifrostExecutionError.FromDatabaseException(new InvalidOperationException(SecretDetail)));

            status.Should().Be(500, "a database fault is a server fault, not a missing blob");
            body.Should().NotContain(SecretDetail, "fault detail never reaches the wire (invariant 3)");
            log.Collector.GetSnapshot().Should().Contain(e => e.Level == LogLevel.Error && e.Exception != null,
                "an operator must see the fault above Debug");
        }

        [Theory]
        [InlineData(BifrostExecutionError.AccessDeniedCode)]
        [InlineData(BifrostExecutionError.NotFoundCode)]
        public async Task A_denial_or_absence_code_answers_the_identical_404(string errorCode)
        {
            var (status, body, log) = await RequestWithReadFaultAsync(
                new BifrostExecutionError(SecretDetail) { ErrorCode = errorCode });

            status.Should().Be(404);
            body.Should().Be("Not found.");
            log.Collector.GetSnapshot().Should().NotContain(e => e.Level >= LogLevel.Warning);
        }

        /// <summary>Resolves the model through the real seam, then fails every read.</summary>
        private sealed class ThrowingReads : IQueryIntentExecutor
        {
            private readonly IQueryIntentExecutor _inner;
            private readonly Exception _fault;
            public ThrowingReads(IQueryIntentExecutor inner, Exception fault) => (_inner, _fault) = (inner, fault);
            public Task<IDbModel> GetModelAsync(string? endpoint = null) => _inner.GetModelAsync(endpoint);
            public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
                => Task.FromException<QueryIntentResult>(_fault);
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
