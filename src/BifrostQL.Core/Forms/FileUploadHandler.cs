using System.Net;
using System.Text;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Forms
{
    /// <summary>
    /// Generates file upload input elements for binary database columns.
    /// Binary type detection is handled by <see cref="TypeMapper.IsBinaryType"/>.
    /// </summary>
    public static class FileUploadHandler
    {
        private const string DefaultAccept = "image/*";

        /// <summary>
        /// Generates a file input element for a binary column.
        /// In update mode with an existing value, a help text is added indicating
        /// that leaving the field empty keeps the current file.
        /// </summary>
        public static string GenerateFileInput(ColumnDto column, ColumnMetadata? metadata = null,
            bool hasCurrentValue = false)
        {
            var sb = new StringBuilder();
            var columnId = column.ColumnName.ToLowerInvariant().Replace(' ', '-');
            var accept = metadata?.Accept ?? DefaultAccept;

            sb.Append($"<input type=\"file\" id=\"{Encode(columnId)}\" name=\"{Encode(column.ColumnName)}\"");
            sb.Append($" accept=\"{Encode(accept)}\"");
            sb.Append('>');

            if (hasCurrentValue)
                sb.Append("<p class=\"help-text\">Leave empty to keep current file</p>");

            return sb.ToString();
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
