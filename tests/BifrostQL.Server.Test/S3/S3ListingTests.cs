using System.Xml.Linq;
using BifrostQL.Core.Storage;
using BifrostQL.Server.S3;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// End-to-end listing behavior over the real read pipeline: bucket enumeration is
    /// identity-filtered to configured file mappings, object listing is tenant/soft-delete
    /// scoped through the authorized intent seam, and prefix/delimiter/pagination/token
    /// semantics match S3. Every security property is proven against a seeded SQLite
    /// database behind the full transformer stack — not a mock.
    /// </summary>
    public sealed class S3ListingTests : IAsyncLifetime
    {
        private const string Endpoint = "/graphql";
        private const string TokenSecret = "s3-listing-tests-fixed-secret-key";
        private S3ListingRealDbHarness _harness = null!;

        private static readonly string[] MetadataRules =
        {
            "main.documents { tenant-filter: tenant_id; soft-delete: deleted_at }",
            "main.documents.body { file: json }",
            "main.documents.thumbnail { file: json }",
            "main.notes.content { file: json }",
            "main.restricted.body { file: json }",
            // Read is NOT in the allowed actions, so the bucket is invisible even to
            // admins (invariant 4 fail-closed) — it must never appear or be listable.
            "main.restricted { policy-actions: create,update,delete }",
        };

        private static string[] SeedSql()
        {
            var rows = new List<string>();
            // tenant-a: ids 1 (body+thumb), 2 (body only).
            rows.Add($"(1, 'tenant-a', NULL, '{Pointer(11, "etag1")}', '{Pointer(21, "thumb1")}')");
            rows.Add($"(2, 'tenant-a', NULL, '{Pointer(12, "etag2")}', NULL)");
            // tenant-b: ids 3,4,6..14 visible (body only), id 5 soft-deleted.
            foreach (var id in new[] { 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14 })
                rows.Add($"({id}, 'tenant-b', NULL, '{Pointer(100 + id, $"etag{id}")}', NULL)");
            rows.Add($"(5, 'tenant-b', '2026-01-01T00:00:00Z', '{Pointer(105, "etag5")}', NULL)");

            return new[]
            {
                "DROP TABLE IF EXISTS documents",
                "DROP TABLE IF EXISTS plain",
                "DROP TABLE IF EXISTS notes",
                "DROP TABLE IF EXISTS restricted",
                """
                CREATE TABLE documents (
                    id INTEGER PRIMARY KEY,
                    tenant_id TEXT NOT NULL,
                    deleted_at TEXT,
                    body TEXT,
                    thumbnail TEXT
                )
                """,
                "INSERT INTO documents(id, tenant_id, deleted_at, body, thumbnail) VALUES\n" + string.Join(",\n", rows),
                // A table with no file column: never inferred as a bucket.
                "CREATE TABLE plain (id INTEGER PRIMARY KEY, val TEXT)",
                "INSERT INTO plain(id, val) VALUES (1, 'x')",
                // A string-PK table for Unicode object keys.
                "CREATE TABLE notes (slug TEXT PRIMARY KEY, content TEXT)",
                $"INSERT INTO notes(slug, content) VALUES ('apple', '{Pointer(1, "n1")}'), ('café', '{Pointer(2, "n2")}')",
                // A file-configured but read-denied table: a visible bucket would leak it.
                "CREATE TABLE restricted (id INTEGER PRIMARY KEY, body TEXT)",
                $"INSERT INTO restricted(id, body) VALUES (1, '{Pointer(9, "r1")}')",
            };
        }

        private static string Pointer(long size, string etag) =>
            new FileMetadata
            {
                FileKey = $"key-{etag}",
                Size = size,
                ETag = etag,
                UploadedAt = new DateTime(2026, 07, 16, 8, 30, 00, DateTimeKind.Utc),
                ContentType = "application/octet-stream",
            }.ToJson().Replace("'", "''");

        public async Task InitializeAsync()
            => _harness = await S3ListingRealDbHarness.StartAsync(nameof(S3ListingTests), MetadataRules, SeedSql());

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        private S3Options Options(int maxListMaterialize = 10_000, int maxKeysLimit = 1000) => new()
        {
            Endpoint = Endpoint,
            ContinuationTokenSecret = TokenSecret,
            MaxListMaterialize = maxListMaterialize,
            MaxKeysLimit = maxKeysLimit,
        };

        private S3Listing Lister(int maxListMaterialize = 10_000, int maxKeysLimit = 1000)
            => _harness.Listing(Options(maxListMaterialize, maxKeysLimit));

        private static Dictionary<string, object?> Ctx(string userId, string? tenant = null, params string[] roles)
        {
            var ctx = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["user_id"] = userId };
            if (tenant is not null) ctx["tenant_id"] = tenant;
            if (roles.Length > 0) ctx["roles"] = roles;
            return ctx;
        }

        // ---- bucket listing -------------------------------------------------------

        [Fact]
        public async Task ListBuckets_returns_only_configured_visible_file_mappings()
        {
            var buckets = await Lister().ListBucketsAsync(Ctx("user-a", "tenant-a"));

            // documents + notes are file-configured and readable; plain has no file
            // column and restricted is read-denied — neither is inferred or exposed.
            buckets.Select(b => b.Name).Should().Equal("documents", "notes");
        }

        [Fact]
        public async Task ListObjectsV2_unknown_or_non_file_or_denied_bucket_is_NoSuchBucket()
        {
            var lister = Lister();
            var ctx = Ctx("user-a", "tenant-a");

            foreach (var bucket in new[] { "does-not-exist", "plain", "restricted" })
            {
                var act = async () => await lister.ListObjectsV2Async(bucket, null, null, null, null, null, ctx);
                (await act.Should().ThrowAsync<S3ProtocolException>()).Which.Code.Should().Be("NoSuchBucket");
            }
        }

        // ---- tenant isolation + soft delete --------------------------------------

        [Fact]
        public async Task ListObjectsV2_scopes_objects_to_the_callers_tenant()
        {
            var lister = Lister();

            var a = await lister.ListObjectsV2Async("documents", null, null, null, null, null, Ctx("user-a", "tenant-a"));
            var b = await lister.ListObjectsV2Async("documents", null, null, null, null, null, Ctx("user-b", "tenant-b"));

            // tenant-a: ids 1,2 → body/1, body/2, thumbnail/1 (tenant scope from the pipeline).
            a.Objects.Select(o => o.Key).Should().BeEquivalentTo("body/1", "body/2", "thumbnail/1");
            // tenant-b never sees tenant-a's rows, and the soft-deleted id 5 is excluded.
            b.Objects.Select(o => o.Key).Should().NotContain("body/1")
                .And.NotContain("thumbnail/1")
                .And.NotContain("body/5");
            b.Objects.Select(o => o.Key).Should().Contain(new[] { "body/3", "body/4" });
        }

        [Fact]
        public async Task ListObjectsV2_empty_tenant_returns_an_empty_page()
        {
            var page = await Lister().ListObjectsV2Async(
                "documents", null, null, null, null, null, Ctx("user-c", "tenant-c"));

            page.Objects.Should().BeEmpty();
            page.CommonPrefixes.Should().BeEmpty();
            page.IsTruncated.Should().BeFalse();
            page.NextContinuationToken.Should().BeNull();
        }

        // ---- object metadata ------------------------------------------------------

        [Fact]
        public async Task ListObjectsV2_object_carries_size_etag_and_last_modified()
        {
            var page = await Lister().ListObjectsV2Async(
                "documents", "body/1", null, null, null, null, Ctx("user-a", "tenant-a"));

            var obj = page.Objects.Single(o => o.Key == "body/1");
            obj.Size.Should().Be(11);
            obj.ETag.Should().Be("etag1");
            obj.LastModified.Should().Be(new DateTime(2026, 07, 16, 8, 30, 00, DateTimeKind.Utc));
        }

        // ---- Unicode keys ---------------------------------------------------------

        [Fact]
        public async Task ListObjectsV2_percent_encodes_unicode_keys_and_renders_valid_xml()
        {
            var page = await Lister().ListObjectsV2Async("notes", null, null, null, null, null, Ctx("user-a"));

            // 'café' → UTF-8 percent-encoded, so the key is injective, traversal-safe ASCII.
            page.Objects.Select(o => o.Key).Should().Equal("content/apple", "content/caf%C3%A9");

            var xml = XElement.Parse(S3ListXml.ListObjectsV2(page));
            XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
            xml.Elements(ns + "Contents").Elements(ns + "Key").Select(k => k.Value)
                .Should().Contain("content/caf%C3%A9");
        }

        // ---- prefix / delimiter ---------------------------------------------------

        [Fact]
        public async Task ListObjectsV2_prefix_filters_to_matching_keys()
        {
            var page = await Lister().ListObjectsV2Async(
                "documents", "thumbnail/", null, null, null, null, Ctx("user-a", "tenant-a"));

            page.Objects.Select(o => o.Key).Should().Equal("thumbnail/1");
            page.Prefix.Should().Be("thumbnail/");
        }

        [Fact]
        public async Task ListObjectsV2_delimiter_rolls_keys_into_common_prefixes()
        {
            var page = await Lister().ListObjectsV2Async(
                "documents", null, "/", null, null, null, Ctx("user-a", "tenant-a"));

            // With '/' and no prefix, body/* and thumbnail/* each collapse to one
            // CommonPrefix; no plain keys are listed and KeyCount counts the prefixes.
            page.Objects.Should().BeEmpty();
            page.CommonPrefixes.Should().Equal("body/", "thumbnail/");
        }

        // ---- pagination -----------------------------------------------------------

        [Fact]
        public async Task ListObjectsV2_pages_the_full_set_exactly_once_with_ordinal_key_order()
        {
            var lister = Lister();
            var ctx = Ctx("user-b", "tenant-b");
            var expected = new[] { 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14 }.Select(i => $"body/{i}").ToHashSet();

            var seen = new List<string>();
            string? token = null;
            var pageCount = 0;
            do
            {
                var page = await lister.ListObjectsV2Async("documents", null, null, 2, token, null, ctx);
                page.Objects.Count.Should().BeLessThanOrEqualTo(2);
                seen.AddRange(page.Objects.Select(o => o.Key));
                token = page.NextContinuationToken;
                pageCount++;
                pageCount.Should().BeLessThan(20, "pagination must terminate");
            } while (token is not null);

            // Union of all pages == the full visible set, each key exactly once (no
            // skip, no duplicate across page boundaries).
            seen.Should().OnlyHaveUniqueItems();
            seen.Should().BeEquivalentTo(expected);
            // Emitted in ascending ordinal key order, so body/10 sorts before body/2's
            // neighbours body/3 — a numeric sort would order these differently.
            seen.Should().BeInAscendingOrder(StringComparer.Ordinal);
            seen.IndexOf("body/10").Should().BeLessThan(seen.IndexOf("body/3"));
        }

        [Fact]
        public async Task ListObjectsV2_first_page_is_truncated_and_carries_a_next_token()
        {
            var page = await Lister().ListObjectsV2Async(
                "documents", null, null, 2, null, null, Ctx("user-b", "tenant-b"));

            page.Objects.Should().HaveCount(2);
            page.IsTruncated.Should().BeTrue();
            page.NextContinuationToken.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task ListObjectsV2_max_keys_is_capped_to_the_configured_limit()
        {
            var page = await Lister(maxKeysLimit: 3).ListObjectsV2Async(
                "documents", null, null, 99999, null, null, Ctx("user-b", "tenant-b"));

            page.MaxKeys.Should().Be(3);
            page.Objects.Should().HaveCount(3);
            page.IsTruncated.Should().BeTrue();
        }

        // ---- token binding --------------------------------------------------------

        [Fact]
        public async Task Continuation_token_from_one_bucket_is_rejected_on_another()
        {
            var lister = Lister();
            var ctxB = Ctx("user-b", "tenant-b");

            var page = await lister.ListObjectsV2Async("documents", null, null, 2, null, null, ctxB);
            var documentsToken = page.NextContinuationToken!;

            // Replaying the documents token against the notes bucket recomputes a
            // different MAC (bucket is bound) and fails closed — no cross-bucket resume.
            var act = async () => await lister.ListObjectsV2Async(
                "notes", null, null, 2, documentsToken, null, Ctx("user-a"));
            (await act.Should().ThrowAsync<S3ProtocolException>()).Which.Code.Should().Be("InvalidArgument");
        }

        [Fact]
        public async Task Tampered_continuation_token_is_rejected()
        {
            var lister = Lister();
            var ctx = Ctx("user-b", "tenant-b");
            var page = await lister.ListObjectsV2Async("documents", null, null, 2, null, null, ctx);
            var token = page.NextContinuationToken!;
            var tampered = token[..^3] + (token.EndsWith("A") ? "B__" : "A__");

            var act = async () => await lister.ListObjectsV2Async("documents", null, null, 2, tampered, null, ctx);
            (await act.Should().ThrowAsync<S3ProtocolException>()).Which.Code.Should().Be("InvalidArgument");
        }

        [Fact]
        public async Task Continuation_token_bound_to_a_different_page_size_is_rejected()
        {
            var lister = Lister();
            var ctx = Ctx("user-b", "tenant-b");
            var page = await lister.ListObjectsV2Async("documents", null, null, 2, null, null, ctx);
            var token = page.NextContinuationToken!;

            // Same bucket/identity, but a widened page size changes the binding.
            var act = async () => await lister.ListObjectsV2Async("documents", null, null, 5, token, null, ctx);
            await act.Should().ThrowAsync<S3ProtocolException>();
        }

        // ---- materialization bound ------------------------------------------------

        [Fact]
        public async Task ListObjectsV2_fails_fast_when_the_bucket_exceeds_the_scan_cap()
        {
            // tenant-b has more than one object; a cap of 1 must fail fast rather than
            // silently truncate (which would lie about completeness).
            var act = async () => await Lister(maxListMaterialize: 1).ListObjectsV2Async(
                "documents", null, null, null, null, null, Ctx("user-b", "tenant-b"));

            (await act.Should().ThrowAsync<S3ProtocolException>()).Which.Code.Should().Be("InvalidArgument");
        }
    }
}
