using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// Builds a connector's <c>{"rows":[...],"note":"..."}</c> payload under a character
    /// cap, shared by the explore and media connectors. Each row is serialized once and the
    /// longest prefix that fits is chosen by summing lengths, so trimming costs O(n) instead
    /// of re-serializing the whole payload after every single-row removal. Every trim is
    /// reported in the note: a silently truncated result would read as the complete answer.
    /// </summary>
    internal static class ChatRowPayload
    {
        private const string RowsOpen = "{\"rows\":[";
        private const string RowsClose = "]";
        private const string NoteKey = ",\"note\":";
        private const string Close = "}";

        /// <summary>
        /// Returns the payload text and how many leading rows it carries. When even zero rows
        /// with their note exceed <paramref name="charCap"/>, the zero-row payload is returned.
        /// </summary>
        /// <param name="rows">The rows to place, in order.</param>
        /// <param name="charCap">Largest payload, in characters.</param>
        /// <param name="hasMore">Whether the query proved more rows exist past the row cap.</param>
        /// <param name="lowerBound">The row count the "at least" note names when <paramref name="hasMore"/>.</param>
        /// <param name="omittedHint">Advice appended to the payload-trim note.</param>
        public static (string Text, int Kept) Fit(
            IReadOnlyList<JsonNode?> rows, int charCap, bool hasMore, int lowerBound, string omittedHint)
        {
            var serialized = new string[rows.Count];
            for (var i = 0; i < rows.Count; i++)
                serialized[i] = rows[i]?.ToJsonString() ?? "null";

            // prefix[k] = characters of the first k rows plus their separating commas.
            var prefix = new long[rows.Count + 1];
            for (var i = 0; i < rows.Count; i++)
                prefix[i + 1] = prefix[i] + serialized[i].Length + (i == 0 ? 0 : 1);

            for (var kept = rows.Count; kept >= 0; kept--)
            {
                var note = EncodedNote(hasMore, kept, lowerBound, rows.Count - kept, omittedHint);
                var length = RowsOpen.Length + prefix[kept] + RowsClose.Length
                    + (note == null ? 0 : NoteKey.Length + note.Length) + Close.Length;
                if (length <= charCap || kept == 0)
                    return (Compose(serialized, kept, note), kept);
            }
            throw new InvalidOperationException("unreachable: kept == 0 always returns");
        }

        private static string? EncodedNote(bool hasMore, int shown, int lowerBound, int omitted, string omittedHint)
        {
            var parts = new List<string>();
            if (hasMore)
                parts.Add($"showing {shown} of at least {lowerBound} rows; narrow with filters");
            if (omitted > 0)
                parts.Add($"{omitted} rows omitted to fit the payload cap; {omittedHint}");
            return parts.Count == 0 ? null : JsonSerializer.Serialize(string.Join(". ", parts));
        }

        private static string Compose(string[] serialized, int kept, string? note)
        {
            var builder = new StringBuilder(RowsOpen);
            for (var i = 0; i < kept; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(serialized[i]);
            }
            builder.Append(RowsClose);
            if (note != null)
                builder.Append(NoteKey).Append(note);
            return builder.Append(Close).ToString();
        }
    }
}
