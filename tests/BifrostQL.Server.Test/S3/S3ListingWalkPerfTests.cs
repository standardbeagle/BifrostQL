using System.Diagnostics;
using BifrostQL.Core.Storage;
using BifrostQL.Server.S3;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// A measurement, not a regression gate: wall time of a full ListObjectsV2 walk (every
    /// page, following continuation tokens) over a seeded in-memory SQLite bucket behind the
    /// real read pipeline, at page size 1000. Prints the median of 5 timed walks after one
    /// warm-up walk. Runs only when <c>BIFROST_PERF=1</c>.
    /// </summary>
    public sealed class S3ListingWalkPerfTests(ITestOutputHelper output)
    {
        private const string Endpoint = "/graphql";
        private const int PageSize = 1000;
        private const int WarmupWalks = 1;
        private const int TimedWalks = 5;

        [SkippableTheory]
        [InlineData(1000)]
        [InlineData(5000)]
        [InlineData(9999)]
        public async Task Full_walk_wall_time(int rows)
        {
            Skip.IfNot(Environment.GetEnvironmentVariable("BIFROST_PERF") == "1",
                "Measurement only: set BIFROST_PERF=1 to run.");

            var pointer = new FileMetadata
            {
                FileKey = "key",
                Size = 1,
                ETag = "etag",
                UploadedAt = new DateTime(2026, 07, 16, 8, 30, 00, DateTimeKind.Utc),
                ContentType = "application/octet-stream",
            }.ToJson().Replace("'", "''");
            await using var harness = await S3ListingRealDbHarness.StartAsync(
                $"perf{rows}",
                new[] { "main.docs.body { file: json }" },
                new[]
                {
                    "CREATE TABLE docs (id INTEGER PRIMARY KEY, body TEXT)",
                    $"WITH RECURSIVE g(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM g WHERE i < {rows}) " +
                    $"INSERT INTO docs(id, body) SELECT i, '{pointer}' FROM g",
                });
            var lister = harness.Listing(new S3Options { Endpoint = Endpoint, ContinuationTokenSecret = "perf-secret" });
            var ctx = new Dictionary<string, object?> { ["user_id"] = "perf" };

            async Task<int> WalkAsync()
            {
                var listed = 0;
                string? token = null;
                do
                {
                    var page = await lister.ListObjectsV2Async("docs", null, null, PageSize, token, null, ctx);
                    listed += page.Objects.Count;
                    token = page.NextContinuationToken;
                } while (token is not null);
                return listed;
            }

            for (var i = 0; i < WarmupWalks; i++)
                (await WalkAsync()).Should().Be(rows);

            var timings = new List<double>();
            for (var i = 0; i < TimedWalks; i++)
            {
                var watch = Stopwatch.StartNew();
                (await WalkAsync()).Should().Be(rows);
                timings.Add(watch.Elapsed.TotalMilliseconds);
            }

            timings.Sort();
            output.WriteLine(
                $"S3 full walk rows={rows} page={PageSize}: median {timings[TimedWalks / 2]:F1} ms " +
                $"(min {timings[0]:F1}, max {timings[^1]:F1})");
        }
    }
}
