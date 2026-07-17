using BifrostQL.Server.S3;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// The RFC 7232 conditional-header precedence for GetObject/HeadObject: If-Match
    /// and If-Unmodified-Since gate with 412, If-None-Match and If-Modified-Since
    /// short-circuit with 304, higher-precedence headers win, and unparseable or
    /// ETag-less inputs behave deterministically.
    /// </summary>
    public sealed class S3ConditionalRequestTests
    {
        private const string ETag = "9a0364b9e99bb480dd25e1f0284c8555";
        private static readonly DateTime LastModified = new(2026, 07, 16, 08, 30, 00, DateTimeKind.Utc);
        private static readonly string Before = new DateTimeOffset(LastModified.AddDays(-1)).ToString("R");
        private static readonly string After = new DateTimeOffset(LastModified.AddDays(1)).ToString("R");
        private static readonly string Exactly = new DateTimeOffset(LastModified).ToString("R");

        private static S3PreconditionOutcome Eval(
            string? ifMatch = null, string? ifNoneMatch = null,
            string? ifModifiedSince = null, string? ifUnmodifiedSince = null,
            string? etag = ETag)
            => S3ConditionalRequest.Evaluate(ifMatch, ifNoneMatch, ifModifiedSince, ifUnmodifiedSince, etag, LastModified);

        [Fact]
        public void No_conditionals_proceed()
            => Eval().Should().Be(S3PreconditionOutcome.Proceed);

        // ---- If-Match (412 gate) --------------------------------------------------

        [Fact]
        public void If_match_matching_etag_proceeds()
            => Eval(ifMatch: $"\"{ETag}\"").Should().Be(S3PreconditionOutcome.Proceed);

        [Fact]
        public void If_match_wildcard_proceeds_because_object_exists()
            => Eval(ifMatch: "*").Should().Be(S3PreconditionOutcome.Proceed);

        [Fact]
        public void If_match_other_etag_fails_precondition()
            => Eval(ifMatch: "\"deadbeef\"").Should().Be(S3PreconditionOutcome.PreconditionFailed);

        [Fact]
        public void If_match_against_object_without_etag_fails_precondition()
            => Eval(ifMatch: $"\"{ETag}\"", etag: null).Should().Be(S3PreconditionOutcome.PreconditionFailed);

        // ---- If-None-Match (304 short-circuit) ------------------------------------

        [Fact]
        public void If_none_match_matching_etag_is_not_modified()
            => Eval(ifNoneMatch: $"\"{ETag}\"").Should().Be(S3PreconditionOutcome.NotModified);

        [Fact]
        public void If_none_match_wildcard_is_not_modified_because_object_exists()
            => Eval(ifNoneMatch: "*").Should().Be(S3PreconditionOutcome.NotModified);

        [Fact]
        public void If_none_match_other_etag_proceeds()
            => Eval(ifNoneMatch: "\"deadbeef\"").Should().Be(S3PreconditionOutcome.Proceed);

        [Fact]
        public void If_none_match_weak_tag_matches_opaque_value()
            => Eval(ifNoneMatch: $"W/\"{ETag}\"").Should().Be(S3PreconditionOutcome.NotModified);

        // ---- date conditionals ----------------------------------------------------

        [Fact]
        public void If_modified_since_older_date_proceeds()
            => Eval(ifModifiedSince: Before).Should().Be(S3PreconditionOutcome.Proceed);

        [Fact]
        public void If_modified_since_same_second_is_not_modified()
            => Eval(ifModifiedSince: Exactly).Should().Be(S3PreconditionOutcome.NotModified);

        [Fact]
        public void If_modified_since_newer_date_is_not_modified()
            => Eval(ifModifiedSince: After).Should().Be(S3PreconditionOutcome.NotModified);

        [Fact]
        public void If_unmodified_since_older_date_fails_precondition()
            => Eval(ifUnmodifiedSince: Before).Should().Be(S3PreconditionOutcome.PreconditionFailed);

        [Fact]
        public void If_unmodified_since_newer_date_proceeds()
            => Eval(ifUnmodifiedSince: After).Should().Be(S3PreconditionOutcome.Proceed);

        [Fact]
        public void Unparseable_date_is_ignored()
            => Eval(ifModifiedSince: "not-a-date").Should().Be(S3PreconditionOutcome.Proceed);

        // ---- precedence -----------------------------------------------------------

        [Fact]
        public void If_match_supersedes_if_unmodified_since()
        {
            // If-Match matches (proceed past the 412 gate) even though If-Unmodified-Since
            // alone would fail — the matching validator wins per RFC precedence.
            Eval(ifMatch: $"\"{ETag}\"", ifUnmodifiedSince: Before)
                .Should().Be(S3PreconditionOutcome.Proceed);
        }

        [Fact]
        public void If_none_match_supersedes_if_modified_since()
        {
            // If-None-Match does not match (proceed) even though If-Modified-Since alone
            // would say not-modified — If-None-Match takes priority when both are present.
            Eval(ifNoneMatch: "\"deadbeef\"", ifModifiedSince: After)
                .Should().Be(S3PreconditionOutcome.Proceed);
        }

        [Fact]
        public void Failed_if_match_beats_a_matching_if_none_match()
        {
            // The 412 gate runs first; a would-be-304 If-None-Match never gets to answer.
            Eval(ifMatch: "\"deadbeef\"", ifNoneMatch: $"\"{ETag}\"")
                .Should().Be(S3PreconditionOutcome.PreconditionFailed);
        }
    }
}
