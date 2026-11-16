using BifrostQL.Core.Model;
using BifrostQL.Server.Grpc;
using FluentAssertions;
using Xunit;
using GrpcMetadata = Grpc.Core.Metadata;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// List paging and Stream truncation against the row window the SQL actually applies, end to
    /// end over the real pipeline and seeded SQLite. List mints a token only when a sentinel row
    /// proves more rows exist (a page ending exactly at the last row carries none), and under a
    /// max-query-rows ceiling below the page it narrows the page and keeps paging instead of
    /// silently dropping rows. A Stream that stops while rows remain says so in the
    /// <c>bifrost-truncated</c> trailer; a complete one carries no such trailer.
    /// </summary>
    public sealed class GrpcRowWindowTests
    {
        // Five rows: page sizes 5 (exact fit), 4 (one more page) and 3 (a cut stream) each bind.
        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS widgets",
            "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
            "INSERT INTO widgets(id, name) VALUES (1,'a'),(2,'b'),(3,'c'),(4,'d'),(5,'e')",
        };

        private static readonly string[] NoMetadata = System.Array.Empty<string>();

        // Ceiling 3 leaves a page of 2 beside the sentinel row; it sits below every page size used.
        private static readonly string[] CeilingMetadata =
        {
            $":root {{ {MetadataKeys.Model.MaxQueryRows}: 3 }}",
        };

        private static GrpcMetadata User() => GrpcRealDbHarness.Identity("u1", roles: "member");

        private static IReadOnlyList<int> Ids(IEnumerable<IReadOnlyDictionary<string, object?>> rows)
            => rows.Select(r => Convert.ToInt32(r["id"])).ToList();

        // ---- List: token only when more rows exist ---------------------------------------

        [Fact]
        public async Task A_list_page_ending_exactly_at_the_last_row_carries_no_token()
        {
            await using var harness = await GrpcRealDbHarness.StartAsync("window-exact", NoMetadata, SeedSql);
            var client = new GrpcWireTestClient(harness.Invoker, harness.Contract);

            var page = await client.ListPageAsync("widgets", User(), orderBy: "id asc", pageSize: 5);

            Ids(page.Rows).Should().Equal(1, 2, 3, 4, 5);
            page.NextPageToken.Should().BeNull("every row fit on the page; a token would only buy an empty round trip");

            // Control: one row short of the set, the token is minted and resumes at the last row.
            var first = await client.ListPageAsync("widgets", User(), orderBy: "id asc", pageSize: 4);
            Ids(first.Rows).Should().Equal(1, 2, 3, 4);
            first.NextPageToken.Should().NotBeNull();
            var last = await client.ListPageAsync("widgets", User(), orderBy: "id asc", pageSize: 4, pageToken: first.NextPageToken);
            Ids(last.Rows).Should().Equal(5);
            last.NextPageToken.Should().BeNull();
        }

        [Fact]
        public async Task A_ceiling_below_the_page_narrows_the_list_page_and_keeps_paging()
        {
            await using var harness = await GrpcRealDbHarness.StartAsync("window-ceiling", CeilingMetadata, SeedSql);
            var client = new GrpcWireTestClient(harness.Invoker, harness.Contract);

            var page = await client.ListPageAsync("widgets", User(), orderBy: "id asc", pageSize: 10);
            page.NextPageToken.Should().NotBeNull("the ceiling cut the page while rows remain");
            Ids(page.Rows).Should().Equal(1, 2);

            // Walking every token yields every row exactly once: the offset advances by the
            // narrowed page, not by the requested page_size.
            var seen = new List<int>(Ids(page.Rows));
            var token = page.NextPageToken;
            while (token is not null)
            {
                var next = await client.ListPageAsync("widgets", User(), orderBy: "id asc", pageSize: 10, pageToken: token);
                seen.AddRange(Ids(next.Rows));
                token = next.NextPageToken;
            }
            seen.Should().Equal(1, 2, 3, 4, 5);
        }

        // ---- Stream: an explicit trailer when it stopped early ---------------------------

        [Fact]
        public async Task A_stream_cut_by_its_page_size_sets_the_truncated_trailer()
        {
            await using var harness = await GrpcRealDbHarness.StartAsync("window-stream-cut", NoMetadata, SeedSql);
            var client = new GrpcWireTestClient(harness.Invoker, harness.Contract);

            var cut = await client.StreamWithTrailersAsync("widgets", User(), orderBy: "id asc", pageSize: 3);
            Ids(cut.Rows).Should().Equal(1, 2, 3);
            cut.Trailers.GetValue(BifrostDynamicGrpcService.TruncatedTrailer).Should().Be("true");

            // Control: the same stream with room for every row ends complete and carries no trailer.
            var whole = await client.StreamWithTrailersAsync("widgets", User(), orderBy: "id asc", pageSize: 5);
            Ids(whole.Rows).Should().Equal(1, 2, 3, 4, 5);
            whole.Trailers.Get(BifrostDynamicGrpcService.TruncatedTrailer).Should().BeNull();
        }

        [Fact]
        public async Task A_stream_cut_by_the_ceiling_sets_the_truncated_trailer()
        {
            await using var harness = await GrpcRealDbHarness.StartAsync("window-stream-ceiling", CeilingMetadata, SeedSql);
            var client = new GrpcWireTestClient(harness.Invoker, harness.Contract);

            var cut = await client.StreamWithTrailersAsync("widgets", User(), orderBy: "id asc");
            cut.Trailers.GetValue(BifrostDynamicGrpcService.TruncatedTrailer).Should().Be("true");
            Ids(cut.Rows).Should().Equal(1, 2);

            // Control: a filter that fits under the ceiling streams complete with no trailer.
            var fits = await client.StreamWithTrailersAsync(
                "widgets", User(), filter: """{ "id": { "_lte": "2" } }""", orderBy: "id asc");
            Ids(fits.Rows).Should().Equal(1, 2);
            fits.Trailers.Get(BifrostDynamicGrpcService.TruncatedTrailer).Should().BeNull();
        }
    }
}
