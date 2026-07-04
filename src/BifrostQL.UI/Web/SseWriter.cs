using System.Text;
using System.Text.Json;

namespace BifrostQL.UI.Web
{
    /// <summary>
    /// Server-sent-events helpers for the quickstart database-creation stream.
    /// Formats progress events as <c>text/event-stream</c> frames and writes an
    /// <see cref="IAsyncEnumerable{T}"/> of pre-formatted frames to the response.
    /// </summary>
    public static class SseWriter
    {
        /// <summary>Formats a single SSE progress event as a <c>data:</c> frame.</summary>
        public static string Event(string stage, int percent, string message,
            bool error = false, string? connectionString = null, string? provider = null)
        {
            var obj = new Dictionary<string, object>
            {
                ["stage"] = stage,
                ["percent"] = percent,
                ["message"] = message
            };
            if (error) obj["error"] = true;
            if (connectionString != null) obj["connectionString"] = connectionString;
            if (provider != null) obj["provider"] = provider;
            return $"data: {JsonSerializer.Serialize(obj)}\n\n";
        }

        /// <summary>Writes an async stream of pre-formatted SSE strings as a <c>text/event-stream</c> response.</summary>
        public static IResult WriteStream(IAsyncEnumerable<string> events)
        {
            return Results.Stream(async stream =>
            {
                await foreach (var evt in events)
                {
                    var bytes = Encoding.UTF8.GetBytes(evt);
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                }
            }, contentType: "text/event-stream");
        }
    }
}
