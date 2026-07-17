using BifrostQL.Server.S3;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// The single-range <c>Range</c> parser: the boundary cases GetObject must get
    /// right — full/open/suffix ranges, unsatisfiable and multi-range and overflow
    /// inputs, and a zero-byte object — resolved without any exception escaping.
    /// </summary>
    public sealed class S3RangeParserTests
    {
        [Fact]
        public void No_header_is_None()
        {
            var r = S3RangeParser.Parse(null, 100);
            r.Kind.Should().Be(S3RangeKind.None);
        }

        [Theory]
        [InlineData("bytes=0-9", 100, 0, 10)]
        [InlineData("bytes=10-19", 100, 10, 10)]
        [InlineData("bytes=0-0", 100, 0, 1)]          // first byte only
        [InlineData("bytes=99-99", 100, 99, 1)]        // last byte only
        [InlineData("bytes=0-99", 100, 0, 100)]        // whole object as an explicit range
        public void Closed_ranges_resolve_to_start_and_length(string header, long total, long start, long length)
        {
            var r = S3RangeParser.Parse(header, total);
            r.Kind.Should().Be(S3RangeKind.Satisfiable);
            r.Start.Should().Be(start);
            r.Length.Should().Be(length);
            r.EndInclusive.Should().Be(start + length - 1);
        }

        [Fact]
        public void End_beyond_last_byte_clamps_to_end_of_object()
        {
            var r = S3RangeParser.Parse("bytes=90-1000", 100);
            r.Kind.Should().Be(S3RangeKind.Satisfiable);
            r.Start.Should().Be(90);
            r.Length.Should().Be(10); // 90..99
        }

        [Fact]
        public void Open_ended_range_runs_to_end_of_object()
        {
            var r = S3RangeParser.Parse("bytes=95-", 100);
            r.Kind.Should().Be(S3RangeKind.Satisfiable);
            r.Start.Should().Be(95);
            r.Length.Should().Be(5);
        }

        [Theory]
        [InlineData("bytes=-10", 100, 90, 10)]  // last 10 bytes
        [InlineData("bytes=-200", 100, 0, 100)] // suffix larger than the object -> whole object
        public void Suffix_ranges_count_from_the_end(string header, long total, long start, long length)
        {
            var r = S3RangeParser.Parse(header, total);
            r.Kind.Should().Be(S3RangeKind.Satisfiable);
            r.Start.Should().Be(start);
            r.Length.Should().Be(length);
        }

        [Theory]
        [InlineData("bytes=100-200", 100)] // start at end-of-file
        [InlineData("bytes=150-", 100)]    // start past end-of-file
        [InlineData("bytes=-0", 100)]      // zero-length suffix
        public void Unsatisfiable_ranges_report_unsatisfiable(string header, long total)
        {
            S3RangeParser.Parse(header, total).Kind.Should().Be(S3RangeKind.Unsatisfiable);
        }

        [Theory]
        [InlineData("items=0-9")]     // unknown unit
        [InlineData("bytes=abc")]     // no '-'
        [InlineData("bytes=abc-def")] // non-numeric bounds
        [InlineData("bytes=10-5")]    // end before start
        [InlineData("bytes=0-9,20-29")] // multi-range: not served as multipart (non-goal)
        public void Malformed_or_multi_range_is_ignored(string header)
        {
            S3RangeParser.Parse(header, 100).Kind.Should().Be(S3RangeKind.Ignored);
        }

        [Fact]
        public void Overflow_start_is_unsatisfiable_never_throws()
        {
            // 29 nines overflows Int64; it must resolve to a clean unsatisfiable result,
            // not an OverflowException escaping onto the wire (invariant 5).
            var r = S3RangeParser.Parse("bytes=99999999999999999999999999999-", 100);
            r.Kind.Should().Be(S3RangeKind.Unsatisfiable);
        }

        [Fact]
        public void Overflow_end_clamps_to_end_of_object()
        {
            var r = S3RangeParser.Parse("bytes=0-99999999999999999999999999999", 100);
            r.Kind.Should().Be(S3RangeKind.Satisfiable);
            r.Start.Should().Be(0);
            r.Length.Should().Be(100);
        }

        [Fact]
        public void Overflow_suffix_returns_whole_object()
        {
            var r = S3RangeParser.Parse("bytes=-99999999999999999999999999999", 100);
            r.Kind.Should().Be(S3RangeKind.Satisfiable);
            r.Start.Should().Be(0);
            r.Length.Should().Be(100);
        }

        [Theory]
        [InlineData("bytes=0-0")]
        [InlineData("bytes=-1")]
        [InlineData("bytes=0-")]
        public void Any_range_on_a_zero_byte_object_is_unsatisfiable(string header)
        {
            S3RangeParser.Parse(header, 0).Kind.Should().Be(S3RangeKind.Unsatisfiable);
        }
    }
}
