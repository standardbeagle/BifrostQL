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
    }
}
