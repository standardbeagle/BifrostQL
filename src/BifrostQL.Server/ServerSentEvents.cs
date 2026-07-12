using System.Text;

namespace BifrostQL.Server
{
    /// <summary>
    /// Formats one named server-sent event per the SSE wire format: an
    /// <c>event:</c> line, one <c>data:</c> line per payload line, and the blank
    /// terminator line. Multi-line payloads are split across <c>data:</c> lines
    /// (the SSE way to carry embedded newlines) so a payload can never smuggle a
    /// premature event terminator onto the wire.
    /// </summary>
    public static class ServerSentEvents
    {
        public static string Format(string eventName, string data)
        {
            if (string.IsNullOrWhiteSpace(eventName))
                throw new ArgumentException("An SSE event name is required.", nameof(eventName));
            if (eventName.AsSpan().IndexOfAny('\r', '\n') >= 0)
                throw new ArgumentException("An SSE event name cannot contain line breaks.", nameof(eventName));
            if (data is null)
                throw new ArgumentNullException(nameof(data));

            var sb = new StringBuilder(eventName.Length + data.Length + 16);
            sb.Append("event: ").Append(eventName).Append('\n');
            foreach (var line in data.ReplaceLineEndings("\n").Split('\n'))
                sb.Append("data: ").Append(line).Append('\n');
            sb.Append('\n');
            return sb.ToString();
        }
    }
}
