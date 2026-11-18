using BifrostQL.Core.Storage;
using BifrostQL.Server.S3;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// The listing projects only columns the caller may read (invariant 4). Every table here
    /// grants table-level read (<c>policy-actions: read</c>), so each fact tests a COLUMN deny,
    /// not a table deny.
    ///
    /// <para>Before the fix the listing selected every key and file column. A read-denied
    /// file column made the policy transformer refuse the whole scan, so a bucket with one
    /// denied and one readable file column listed nothing, while <c>ListBuckets</c> still
    /// advertised it. A denied key column did the same.</para>
    /// </summary>
    public sealed class S3ListingColumnPolicyTests : IAsyncLifetime
    {
        private const string Endpoint = "/graphql";
        private S3ListingRealDbHarness _harness = null!;

        private static readonly string[] MetadataRules =
        {
            // One readable and one read-denied file column.
            "main.photos { policy-actions: read; policy-read-deny: scan }",
            "main.photos.picture { file: json }",
            "main.photos.scan { file: json }",
            // The key column is read-denied: no object key can be built.
            "main.ledgers { policy-actions: read; policy-read-deny: id }",
            "main.ledgers.statement { file: json }",
            // The only file column is read-denied.
            "main.vaults { policy-actions: read; policy-read-deny: blob }",
            "main.vaults.blob { file: json }",
            // Control: the same shape with nothing denied.
            "main.opened { policy-actions: read }",
            "main.opened.blob { file: json }",
        };

        private static string[] SeedSql() => new[]
        {
            "DROP TABLE IF EXISTS photos",
            "CREATE TABLE photos (id INTEGER PRIMARY KEY, picture TEXT, scan TEXT)",
            $"INSERT INTO photos(id, picture, scan) VALUES (1, '{Pointer("p1")}', '{Pointer("s1")}'), (2, NULL, '{Pointer("s2")}')",
            "DROP TABLE IF EXISTS ledgers",
            "CREATE TABLE ledgers (id INTEGER PRIMARY KEY, statement TEXT)",
            $"INSERT INTO ledgers(id, statement) VALUES (1, '{Pointer("l1")}')",
            "DROP TABLE IF EXISTS vaults",
            "CREATE TABLE vaults (id INTEGER PRIMARY KEY, blob TEXT)",
            $"INSERT INTO vaults(id, blob) VALUES (1, '{Pointer("v1")}')",
            "DROP TABLE IF EXISTS opened",
            "CREATE TABLE opened (id INTEGER PRIMARY KEY, blob TEXT)",
            $"INSERT INTO opened(id, blob) VALUES (1, '{Pointer("o1")}')",
        };

        private static string Pointer(string etag) =>
            new FileMetadata
            {
                FileKey = $"key-{etag}",
                Size = 7,
                ETag = etag,
                UploadedAt = new DateTime(2026, 07, 16, 8, 30, 00, DateTimeKind.Utc),
            }.ToJson().Replace("'", "''");

        public async Task InitializeAsync()
            => _harness = await S3ListingRealDbHarness.StartAsync(
                nameof(S3ListingColumnPolicyTests), MetadataRules, SeedSql());

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        private S3Listing Lister() => _harness.Listing(new S3Options
        {
            Endpoint = Endpoint,
            ContinuationTokenSecret = "s3-listing-column-policy-secret",
        });

        private static Dictionary<string, object?> Ctx() =>
            new(StringComparer.OrdinalIgnoreCase) { ["user_id"] = "user-a" };

        [Fact]
        public async Task A_read_denied_file_column_is_left_out_and_the_readable_one_still_lists()
        {
            var page = await Lister().ListObjectsV2Async("photos", null, null, null, null, null, Ctx());

            page.Objects.Select(o => o.Key).Should().Equal(new[] { "picture/1" },
                "the scan column is read-denied, so its objects are not listed and do not block the readable column");
        }

        [Theory]
        [InlineData("ledgers")]
        [InlineData("vaults")]
        public async Task A_bucket_with_a_denied_key_or_no_readable_file_column_is_hidden_from_both_listings(string bucket)
        {
            var lister = Lister();

            var buckets = await lister.ListBucketsAsync(Ctx());
            buckets.Select(b => b.Name).Should().NotContain(bucket,
                "ListBuckets must not advertise a bucket ListObjectsV2 refuses");

            var denied = await Assert.ThrowsAsync<S3ProtocolException>(
                () => lister.ListObjectsV2Async(bucket, null, null, null, null, null, Ctx()));
            var missing = await Assert.ThrowsAsync<S3ProtocolException>(
                () => lister.ListObjectsV2Async("nosuchbucket", null, null, null, null, null, Ctx()));

            denied.Code.Should().Be("NoSuchBucket");
            denied.HttpStatus.Should().Be(missing.HttpStatus);
            denied.Code.Should().Be(missing.Code);
            denied.Message.Should().Be(missing.Message, "denied must read exactly like missing");
        }

        [Fact]
        public async Task Control_buckets_with_readable_columns_are_listed()
        {
            var lister = Lister();

            (await lister.ListBucketsAsync(Ctx())).Select(b => b.Name).Should().Equal("opened", "photos");
            (await lister.ListObjectsV2Async("opened", null, null, null, null, null, Ctx()))
                .Objects.Select(o => o.Key).Should().Equal("blob/1");
        }
    }
}
