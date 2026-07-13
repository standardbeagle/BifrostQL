using System;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// Result caps for the built-in chat connectors. Tool results are fed back to
    /// the model verbatim, so an unbounded read would flood the context window;
    /// both caps report what they trimmed inside the payload — never silently.
    /// Registered as a default singleton by the server so a host overrides caps by
    /// registering its own instance before <c>AddBifrostQL</c>.
    /// </summary>
    public sealed class ChatConnectorOptions
    {
        public const int DefaultExploreRowCap = 50;
        public const int DefaultExplorePayloadCharCap = 20_000;

        /// <summary>
        /// ~3.5 MB of raw bytes: base64 expands by 4/3, keeping the encoded image
        /// under the Anthropic API's ~5 MB per-image limit with headroom.
        /// </summary>
        public const int DefaultMediaVisionByteCap = 3_500_000;

        private readonly int _exploreRowCap = DefaultExploreRowCap;
        private readonly int _explorePayloadCharCap = DefaultExplorePayloadCharCap;
        private readonly int _mediaVisionByteCap = DefaultMediaVisionByteCap;

        /// <summary>Maximum rows one explore tool call returns (default 50).</summary>
        public int ExploreRowCap
        {
            get => _exploreRowCap;
            init => _exploreRowCap = value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(ExploreRowCap), value, "The explore row cap must be at least 1.");
        }

        /// <summary>
        /// Maximum characters one explore tool result payload may occupy (default
        /// ~20000). Rows are dropped from the end until the payload fits, and the
        /// omission is reported in the payload's note.
        /// </summary>
        public int ExplorePayloadCharCap
        {
            get => _explorePayloadCharCap;
            init => _explorePayloadCharCap = value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(ExplorePayloadCharCap), value, "The explore payload cap must be at least 1 character.");
        }

        /// <summary>
        /// Maximum raw (pre-base64) byte size of an image a media connector attaches
        /// to a tool result as vision input (default ~3.5 MB). An over-cap image is a
        /// model-visible <see cref="ChatToolInputException"/>, never a silent drop.
        /// </summary>
        public int MediaVisionByteCap
        {
            get => _mediaVisionByteCap;
            init => _mediaVisionByteCap = value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(MediaVisionByteCap), value, "The media vision byte cap must be at least 1.");
        }
    }
}
