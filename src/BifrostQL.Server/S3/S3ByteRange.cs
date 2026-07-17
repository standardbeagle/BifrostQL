using System.Globalization;

namespace BifrostQL.Server.S3
{
    /// <summary>How a client's <c>Range</c> header resolved against an object's size.</summary>
    internal enum S3RangeKind
    {
        /// <summary>No <c>Range</c> header: serve the whole object (200).</summary>
        None,

        /// <summary>
        /// A <c>Range</c> that is present but not honored — an unknown unit, malformed
        /// syntax, or a multi-range request (multipart byte ranges are a non-goal). Per
        /// RFC 7233 the header is ignored and the whole object is served (200), never a
        /// wrong partial body.
        /// </summary>
        Ignored,

        /// <summary>A single satisfiable range: serve those bytes (206).</summary>
        Satisfiable,

        /// <summary>A well-formed range that cannot be met (start at/after end of file): 416.</summary>
        Unsatisfiable,
    }

    /// <summary>
    /// The result of resolving a single-range <c>Range</c> header against a known
    /// object length. <see cref="Start"/>/<see cref="Length"/> are meaningful only
    /// when <see cref="Kind"/> is <see cref="S3RangeKind.Satisfiable"/>.
    /// </summary>
    internal readonly record struct S3ByteRange(S3RangeKind Kind, long Start, long Length)
    {
        public static readonly S3ByteRange None = new(S3RangeKind.None, 0, 0);
        public static readonly S3ByteRange Ignored = new(S3RangeKind.Ignored, 0, 0);
        public static readonly S3ByteRange Unsatisfiable = new(S3RangeKind.Unsatisfiable, 0, 0);
        public static S3ByteRange Satisfiable(long start, long length) => new(S3RangeKind.Satisfiable, start, length);

        /// <summary>Inclusive last byte of a satisfiable range, for the Content-Range header.</summary>
        public long EndInclusive => Start + Length - 1;
    }

    /// <summary>
    /// Parses an HTTP <c>Range</c> header into a single resolved byte range, S3-style.
    ///
    /// <para>Only a single range is honored. A multi-range request is deliberately
    /// ignored (whole object) rather than answered as <c>multipart/byteranges</c>,
    /// which is a slice-4 non-goal. Every numeric bound is parsed without an exception
    /// escaping (invariant 5): a bound that is all digits but overflows <see cref="long"/>
    /// is a valid-but-too-large number and is handled as such (an over-large start is
    /// unsatisfiable; an over-large end clamps to the last byte; an over-large suffix
    /// means the whole object) — never a <see cref="System.OverflowException"/>. A bound
    /// that is not all digits is malformed and the header is ignored.</para>
    /// </summary>
    internal static class S3RangeParser
    {
        private const string Unit = "bytes=";

        public static S3ByteRange Parse(string? headerValue, long totalLength)
        {
            if (string.IsNullOrWhiteSpace(headerValue))
                return S3ByteRange.None;

            var value = headerValue.Trim();
            if (!value.StartsWith(Unit, StringComparison.OrdinalIgnoreCase))
                return S3ByteRange.Ignored; // unknown range unit

            var spec = value[Unit.Length..];
            if (spec.Contains(','))
                return S3ByteRange.Ignored; // multi-range: not served as multipart (non-goal)

            var dash = spec.IndexOf('-');
            if (dash < 0)
                return S3ByteRange.Ignored; // malformed: a range spec must contain '-'

            var startText = spec[..dash];
            var endText = spec[(dash + 1)..];

            // Suffix range: bytes=-N -> the last N bytes.
            if (startText.Length == 0)
            {
                var suffix = ParseNumber(endText);
                if (suffix.Kind == NumberKind.Malformed)
                    return S3ByteRange.Ignored;
                if (suffix.Kind == NumberKind.Overflow)
                    return WholeObject(totalLength); // asks for more than exists -> whole object
                if (suffix.Value == 0)
                    return S3ByteRange.Unsatisfiable; // a zero-length suffix cannot be satisfied
                var suffixStart = suffix.Value >= totalLength ? 0 : totalLength - suffix.Value;
                return Build(suffixStart, totalLength - 1, totalLength);
            }

            var start = ParseNumber(startText);
            if (start.Kind == NumberKind.Malformed)
                return S3ByteRange.Ignored;
            if (start.Kind == NumberKind.Overflow)
                return S3ByteRange.Unsatisfiable; // start beyond any possible object size

            // Open-ended: bytes=a- -> from a to the last byte.
            if (endText.Length == 0)
                return Build(start.Value, totalLength - 1, totalLength);

            var end = ParseNumber(endText);
            if (end.Kind == NumberKind.Malformed)
                return S3ByteRange.Ignored;
            // An over-large end clamps to the last byte; an explicit end < start is a
            // malformed range and is ignored (RFC 7233).
            if (end.Kind == NumberKind.Ok && end.Value < start.Value)
                return S3ByteRange.Ignored;
            var endInclusive = end.Kind == NumberKind.Overflow ? totalLength - 1 : end.Value;
            return Build(start.Value, endInclusive, totalLength);
        }

        private static S3ByteRange WholeObject(long totalLength)
            => totalLength > 0 ? S3ByteRange.Satisfiable(0, totalLength) : S3ByteRange.Unsatisfiable;

        private static S3ByteRange Build(long start, long endInclusive, long totalLength)
        {
            // A range is satisfiable only if it starts inside a non-empty object; the end
            // is clamped to the last byte. A zero-byte object has no satisfiable range.
            if (totalLength <= 0 || start < 0 || start >= totalLength)
                return S3ByteRange.Unsatisfiable;

            var end = Math.Min(endInclusive, totalLength - 1);
            if (end < start)
                return S3ByteRange.Unsatisfiable;

            return S3ByteRange.Satisfiable(start, end - start + 1);
        }

        private enum NumberKind { Ok, Overflow, Malformed }

        private readonly record struct ParsedNumber(NumberKind Kind, long Value);

        private static ParsedNumber ParseNumber(string text)
        {
            if (text.Length == 0)
                return new ParsedNumber(NumberKind.Malformed, 0);

            foreach (var c in text)
            {
                if (c is < '0' or > '9')
                    return new ParsedNumber(NumberKind.Malformed, 0);
            }

            // All digits: a value that overflows long is a valid-but-too-large bound, kept
            // distinct from a malformed one so an over-large range fails deterministically
            // (unsatisfiable/clamped) instead of raising an exception on the wire.
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? new ParsedNumber(NumberKind.Ok, value)
                : new ParsedNumber(NumberKind.Overflow, 0);
        }
    }
}
