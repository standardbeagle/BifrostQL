using System.Text.Json;

namespace BifrostQL.Mcp
{
    /// <summary>
    /// Opaque continuation token for <c>bifrost_query</c>. Deliberately simple:
    /// a base64-encoded JSON snapshot of the whole query (table, offset, limit,
    /// sort tokens, detail, field list, raw filter JSON) rather than a server-side
    /// page handle. That makes a cursor a complete continuation — an agent can
    /// resume with the cursor alone — at the cost of offset-based paging semantics
    /// (concurrent writes can shift rows between pages), which is acceptable for
    /// an interactive agent surface. Not encrypted or signed: the content is only
    /// the caller's own arguments, and every decoded value goes back through the
    /// same validation as freshly supplied arguments.
    /// </summary>
    internal sealed record QueryCursor
    {
        /// <summary>
        /// Shared rejection text for any cursor that fails decoding OR carries
        /// out-of-range field values: both mean the token is not one the server
        /// issued, so both get the same guidance.
        /// </summary>
        internal const string InvalidCursorMessage =
            "Invalid cursor. Pass the exact nextCursor value returned by a previous bifrost_query call, " +
            "or omit page.cursor to start a new query.";

        public int Version { get; init; } = 1;
        public required string Table { get; init; }
        public int Offset { get; init; }
        public int Limit { get; init; }
        public IReadOnlyList<string> Sort { get; init; } = Array.Empty<string>();
        public string Detail { get; init; } = "summary";
        public IReadOnlyList<string>? Fields { get; init; }
        /// <summary>Raw filter JSON exactly as the original call supplied it; re-parsed and re-validated on decode.</summary>
        public string? FilterJson { get; init; }

        public string Encode() =>
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));

        public static QueryCursor Decode(string cursor)
        {
            try
            {
                var decoded = JsonSerializer.Deserialize<QueryCursor>(Convert.FromBase64String(cursor));
                if (decoded is null || string.IsNullOrWhiteSpace(decoded.Table) || decoded.Version != 1)
                    throw new JsonException("Cursor payload is incomplete.");
                return decoded;
            }
            catch (Exception e) when (e is FormatException or JsonException)
            {
                throw new ToolPromptException(InvalidCursorMessage);
            }
        }
    }
}
