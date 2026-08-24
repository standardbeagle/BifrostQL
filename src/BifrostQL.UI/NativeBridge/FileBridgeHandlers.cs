using System.Text.Json;
using Microsoft.Extensions.Logging;
using Photino.NET;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// <c>save-file</c>: the desktop shell's native download path. Shows the OS save
    /// dialog (Photino's ShowSaveFile self-marshals onto the window thread, so calling
    /// from the threadpool bridge pump is safe) and writes the payload text to the
    /// chosen path. A cancelled dialog is <c>{ saved: false }</c> — an outcome, not an
    /// error. Photino-only, like the vault handlers: the HTTP test mirror has no
    /// window, so this is deliberately not registered there.
    /// </summary>
    public sealed class FileBridgeHandlers
    {
        private readonly PhotinoWindow _window;
        private readonly ILogger? _logger;

        public FileBridgeHandlers(PhotinoWindow window, ILogger? logger)
        {
            _window = window;
            _logger = logger;
        }

        public void Register(IBridgeRegistry bridge) => bridge.Register("save-file", SaveFileAsync);

        private async Task<object?> SaveFileAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            if (payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("content", out var contentProp)
                || contentProp.ValueKind != JsonValueKind.String)
                throw new ArgumentException("save-file requires a 'content' string payload.");
            var content = contentProp.GetString()!;

            var suggestedName = payload.TryGetProperty("suggestedName", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                ? nameProp.GetString()
                : null;
            var title = payload.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String
                ? titleProp.GetString()!
                : "Save file";

            // Seed the dialog with Documents/<suggestedName>; Photino's defaultPath is a
            // path, not a bare filename, and null falls back to Documents anyway.
            var defaultPath = suggestedName is null
                ? null
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), SanitizeFileName(suggestedName));

            var path = await _window.ShowSaveFileAsync(title, defaultPath);
            if (string.IsNullOrEmpty(path))
                return new { saved = false };

            await File.WriteAllTextAsync(path, content, cancellationToken);
            _logger?.LogInformation("save-file wrote {Length} characters to {Path}", content.Length, path);
            return new { saved = true, path };
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return string.IsNullOrWhiteSpace(cleaned) ? "export.txt" : cleaned;
        }
    }
}
