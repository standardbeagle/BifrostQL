using System.Diagnostics;
using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// Denial-of-service bounds on the catalog responder's pattern matching. Both surfaces run on
    /// RAW CLIENT SQL — the recognition scan on EVERY query before parsing, and LIKE on every
    /// synthesized catalog row — so an unbounded match time is a DoS reachable by one message.
    ///
    /// <para>These assert WALL-CLOCK bounds deliberately: the defect being pinned is
    /// super-linear match cost, and only elapsed time can distinguish "matched correctly" from
    /// "matched correctly after backtracking for an hour". The bounds are set thousands of times
    /// above the fixed implementation's cost, so they cannot flake on a loaded machine while still
    /// failing decisively against the backtracking versions (which do not finish at all).</para>
    /// </summary>
    public sealed class PgCatalogRegexDosTests
    {
        [Theory]
        // The pathological pattern is the ALTERNATING one — '%a%a%a…%x' — not a run of '%'.
        // (A run collapses under the regex engine's own optimizer, so '%%%%x' never blew up; each
        // '%' here is a SEPARATE '.*' with a literal between, which is the shape that explodes.)
        // Measured against the old regex translation with a 10 s ceiling: reps=5/len=60 TIMED OUT,
        // reps=8/len=30 took 2.3 s, reps=10/len=30 took 6.9 s, reps=12/len=30 TIMED OUT — and the
        // shipped code had NO ceiling at all, so these did not fail, they hung a core forever.
        [InlineData(5, 60)]
        [InlineData(8, 120)]
        [InlineData(12, 240)]
        [InlineData(20, 512)]
        public void Like_WithAlternatingWildcards_DoesNotBacktrackCatastrophically(int repeats, int valueLength)
        {
            var pattern = string.Concat(Enumerable.Repeat("%a", repeats)) + "%x";
            var value = new string('a', valueLength);

            var stopwatch = Stopwatch.StartNew();
            var matched = PgCatalogResponder.Like(value, pattern);
            stopwatch.Stop();

            matched.Should().BeFalse("the value contains no 'x'");
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
                "the glob scan is O(value x pattern); the regex translation was exponential");
        }

        [Theory]
        // Ordinary LIKE semantics must be unchanged by the rewrite.
        [InlineData("orders", "orders", true)]
        [InlineData("orders", "ord%", true)]
        [InlineData("orders", "%ers", true)]
        [InlineData("orders", "%rde%", true)]
        [InlineData("orders", "order_", true)]
        [InlineData("orders", "order__", false)]
        [InlineData("orders", "%", true)]
        [InlineData("orders", "", false)]
        [InlineData("", "%", true)]
        [InlineData("", "", true)]
        [InlineData("orders", "Orders", false)]
        [InlineData("orders", "ord", false)]
        [InlineData("a.b", "a.b", true)]
        // '.' is a regex metacharacter but a LITERAL in LIKE: the old translation escaped it, and
        // the glob scan must keep treating it literally rather than as "any character".
        [InlineData("axb", "a.b", false)]
        public void Like_MatchesSqlSemantics(string value, string pattern, bool expected)
            => PgCatalogResponder.Like(value, pattern).Should().Be(expected);

        [Fact]
        public void Like_WithNullOperands_IsFalse()
        {
            PgCatalogResponder.Like(null, "%").Should().BeFalse();
            PgCatalogResponder.Like("x", null).Should().BeFalse();
        }

        [Fact]
        public void RecognitionPatterns_AreBoundedByTheEngine_NotByAWallClock()
        {
            // The recognition scan runs DescribeRelationsPattern against RAW CLIENT SQL on EVERY
            // query, before parsing. Its patterns are NonBacktracking, so the linear bound is a
            // property of the ENGINE — protocol-adapter-security invariant 1 is satisfied by
            // NonBacktracking alone. A wall-clock match timeout on top of that is not a safety
            // bound but a false-positive generator: a host under load (the parallel epic gate)
            // deschedules the matching thread past the timeout, RegexMatchTimeoutException becomes
            // a client-facing syntax error, and a perfectly routable query fails — the full-suite
            // flakes of PreparedCatalogQuery_RoutesThroughCatalogResponder_OverExtendedPath and
            // UserQuery_WithCatalogTextInStringLiteral_ExecutesAndIsNotMisrouted (worktrack
            // 01M1N2VV1T7KASK90QR6JKGKAT). Every static Regex the responder runs on client SQL must
            // therefore be NonBacktracking AND carry no wall-clock timeout.
            var regexFields = typeof(PgCatalogResponder)
                .GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                .Where(f => f.FieldType == typeof(System.Text.RegularExpressions.Regex))
                .ToList();

            regexFields.Should().NotBeEmpty("the recognition patterns must exist for this fact to guard them");

            foreach (var field in regexFields)
            {
                var regex = (System.Text.RegularExpressions.Regex)field.GetValue(null)!;
                regex.Options.Should().HaveFlag(System.Text.RegularExpressions.RegexOptions.NonBacktracking,
                    $"{field.Name} runs on raw client SQL; its DoS bound must be the linear engine (invariant 1)");
                regex.MatchTimeout.Should().Be(System.Threading.Timeout.InfiniteTimeSpan,
                    $"{field.Name} is engine-bounded; a wall-clock timeout turns host load into spurious client syntax errors");
            }
        }
    }
}
