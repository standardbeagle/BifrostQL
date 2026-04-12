using System.Net;
using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Storage;

namespace BifrostQL.Core.Forms
{
    /// <summary>
    /// Generates file upload input elements for binary database columns and file storage columns.
    /// Binary type detection is handled by <see cref="TypeMapper.IsBinaryType"/>.
    /// File storage columns are identified by the "file" metadata tag.
    /// </summary>
    public static class FileUploadHandler
    {
        private const string DefaultAccept = "image/*";

        /// <summary>
        /// Generates a file input element for a file column.
        /// In update mode with an existing value, a help text is added indicating
        /// that leaving the field empty keeps the current file.
        /// </summary>
        public static string GenerateFileInput(ColumnDto column, ColumnMetadata? metadata = null,
            bool hasCurrentValue = false)
        {
            var sb = new StringBuilder();
            var columnId = column.ColumnName.ToLowerInvariant().Replace(' ', '-');
            
            // Determine accept attribute: metadata.Accept > file config > default
            var accept = metadata?.Accept;
            if (string.IsNullOrEmpty(accept))
            {
                var fileConfig = metadata?.GetFileConfig() ?? FileColumnConfig.FromMetadata(column.GetMetadataValue("file"));
                accept = fileConfig?.GetAcceptAttribute() ?? DefaultAccept;
            }

            sb.Append($"<input type=\"file\" id=\"{Encode(columnId)}\" name=\"{Encode(column.ColumnName)}\"");
            sb.Append($" accept=\"{Encode(accept)}\"");
            
            // Add data attributes for file storage configuration
            var fileStorageConfig = metadata?.FileStorage ?? column.GetMetadataValue("file");
            var storageConfig = column.GetMetadataValue("storage");
            if (!string.IsNullOrWhiteSpace(fileStorageConfig))
            {
                sb.Append($" data-file-storage=\"{Encode(fileStorageConfig)}\"");
            }
            if (!string.IsNullOrWhiteSpace(storageConfig))
            {
                sb.Append($" data-storage=\"{Encode(storageConfig)}\"");
            }
            
            sb.Append('>');

            if (hasCurrentValue)
                sb.Append("<p class=\"help-text\">Leave empty to keep current file</p>");

            return sb.ToString();
        }

        /// <summary>
        /// Checks if a column should be rendered as a file input based on metadata or data type.
        /// </summary>
        public static bool IsFileColumn(ColumnDto column, ColumnMetadata? metadata = null)
        {
            // Check explicit file metadata
            if (metadata?.FileStorage != null || column.GetMetadataValue("file") != null)
                return true;

            // Check for storage metadata (implies file storage)
            if (metadata?.StorageBucket != null || column.GetMetadataValue("storage") != null)
                return true;

            // Check for binary data type
            if (TypeMapper.IsBinaryType(column.EffectiveDataType))
                return true;

            // Check for explicit input type
            if (metadata?.InputType == "file")
                return true;

            return false;
        }

        /// <summary>
        /// Gets the accept attribute for a file column.
        /// </summary>
        public static string GetAcceptAttribute(ColumnDto column, ColumnMetadata? metadata = null)
        {
            // First check metadata Accept
            if (!string.IsNullOrEmpty(metadata?.Accept))
                return metadata.Accept;

            // Then check file config
            var fileConfig = metadata?.GetFileConfig() ?? FileColumnConfig.FromMetadata(column.GetMetadataValue("file"));
            var configAccept = fileConfig?.GetAcceptAttribute();
            if (!string.IsNullOrEmpty(configAccept))
                return configAccept;

            // Default for binary columns
            if (TypeMapper.IsBinaryType(column.EffectiveDataType))
                return DefaultAccept;

            return "*/*";
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
