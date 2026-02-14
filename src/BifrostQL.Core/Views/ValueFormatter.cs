using System.Globalization;
using System.Net;

namespace BifrostQL.Core.Views
{
    /// <summary>
    /// Formats database values for display in HTML views.
    /// All output is HTML-encoded unless a method explicitly returns markup.
    /// </summary>
    public static class ValueFormatter
    {
        /// <summary>
        /// Formats a DateTime or DateTimeOffset value as a <c>&lt;time&gt;</c> element
        /// with an ISO 8601 datetime attribute and a human-readable display.
        /// Returns the encoded string representation for non-date values.
        /// </summary>
        public static string FormatDateTime(object value)
        {
            if (value is DateTime dt)
            {
                var iso = dt.ToString("o", CultureInfo.InvariantCulture);
                var display = dt.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
                return $"<time datetime=\"{Encode(iso)}\">{Encode(display)}</time>";
            }

            if (value is DateTimeOffset dto)
            {
                var iso = dto.ToString("o", CultureInfo.InvariantCulture);
                var display = dto.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
                return $"<time datetime=\"{Encode(iso)}\">{Encode(display)}</time>";
            }

            // Attempt to parse a string representation
            if (value is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                var iso = parsed.ToString("o", CultureInfo.InvariantCulture);
                var display = parsed.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
                return $"<time datetime=\"{Encode(iso)}\">{Encode(display)}</time>";
            }

            return Encode(value.ToString() ?? "");
        }

        /// <summary>
        /// Formats a boolean value as "Yes" or "No".
        /// </summary>
        public static string FormatBoolean(object value)
        {
            if (value is bool b)
                return b ? "Yes" : "No";

            var s = value.ToString() ?? "";
            return s is "true" or "1" or "True" or "TRUE" ? "Yes" : "No";
        }

        /// <summary>
        /// Returns the display string for a null database value.
        /// </summary>
        public static string FormatNull()
        {
            return "<span class=\"null-value\">(null)</span>";
        }

        /// <summary>
        /// Truncates text to the specified maximum length, appending an ellipsis
        /// when truncation occurs. The result is HTML-encoded.
        /// </summary>
        public static string TruncateText(string value, int maxLength)
        {
            if (maxLength <= 0)
                return Encode(value);

            if (value.Length <= maxLength)
                return Encode(value);

            return Encode(value.Substring(0, maxLength)) + "&hellip;";
        }

        /// <summary>
        /// Formats a byte count as a human-readable file size (e.g., "1.5 MB").
        /// </summary>
        public static string FormatFileSize(long bytes)
        {
            if (bytes < 0) return "0 B";

            string[] units = { "B", "KB", "MB", "GB" };
            var size = (double)bytes;
            var unitIndex = 0;

            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return unitIndex == 0
                ? $"{bytes} B"
                : $"{size:0.#} {units[unitIndex]}";
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
