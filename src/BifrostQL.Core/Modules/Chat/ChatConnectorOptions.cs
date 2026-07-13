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

        public const int DefaultPlanRowCap = 20;

        /// <summary>Default time a plan proposal waits for the user before denying itself.</summary>
        public static readonly TimeSpan DefaultPlanConfirmationTimeout = TimeSpan.FromMinutes(5);

        private readonly int _exploreRowCap = DefaultExploreRowCap;
        private readonly int _explorePayloadCharCap = DefaultExplorePayloadCharCap;
        private readonly int _mediaVisionByteCap = DefaultMediaVisionByteCap;
        private readonly int _planRowCap = DefaultPlanRowCap;
        private readonly TimeSpan _planConfirmationTimeout = DefaultPlanConfirmationTimeout;

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

        /// <summary>
        /// Maximum rows one plan tool call may propose (default 20). An over-cap
        /// proposal is a model-visible error naming the cap, never a silent trim —
        /// a write proposal must be exactly what the user confirms.
        /// </summary>
        public int PlanRowCap
        {
            get => _planRowCap;
            init => _planRowCap = value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(PlanRowCap), value, "The plan row cap must be at least 1.");
        }

        /// <summary>
        /// How long a plan proposal waits for the user's confirm/deny before denying
        /// itself (default 5 minutes). On timeout the model receives a declined tool
        /// result and continues; nothing is written.
        /// </summary>
        public TimeSpan PlanConfirmationTimeout
        {
            get => _planConfirmationTimeout;
            init => _planConfirmationTimeout = value > TimeSpan.Zero
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(PlanConfirmationTimeout), value, "The plan confirmation timeout must be positive.");
        }
    }
}
