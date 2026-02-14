using System.Net;
using System.Text;
using BifrostQL.Core.Forms;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Views
{
    /// <summary>
    /// Generates read-only detail view HTML for a single database record.
    /// Renders all columns as a definition list with type-appropriate formatting.
    /// </summary>
    public sealed class DetailViewBuilder
    {
        private readonly IDbModel _dbModel;
        private readonly string _basePath;

        public DetailViewBuilder(IDbModel dbModel, string basePath = "/bifrost")
        {
            _dbModel = dbModel ?? throw new ArgumentNullException(nameof(dbModel));
            _basePath = basePath.TrimEnd('/');
        }

        /// <summary>
        /// Generates a complete detail view for a single record.
        /// </summary>
        /// <param name="tableName">The database table name.</param>
        /// <param name="recordData">Column values keyed by column name.</param>
        public string GenerateDetailView(string tableName, IReadOnlyDictionary<string, object?> recordData)
        {
            var table = _dbModel.GetTableFromDbName(tableName);
            return GenerateDetailView(table, recordData);
        }

        /// <summary>
        /// Generates a complete detail view for a single record.
        /// </summary>
        public string GenerateDetailView(IDbTable table, IReadOnlyDictionary<string, object?> recordData)
        {
            var sb = new StringBuilder();
            sb.Append("<div class=\"bifrost-detail\">");
            sb.Append($"<h1>{Encode(FormatTableTitle(table.DbName))} Details</h1>");

            sb.Append("<dl>");
            foreach (var column in table.Columns)
            {
                recordData.TryGetValue(column.ColumnName, out var value);
                AppendFieldRow(sb, column, value, table);
            }
            sb.Append("</dl>");

            var recordId = GetPrimaryKeyValue(table, recordData);
            AppendActions(sb, table.DbName, recordId);

            sb.Append("</div>");
            return sb.ToString();
        }

        /// <summary>
        /// Generates a single dt/dd pair for a column value.
        /// </summary>
        public string GenerateFieldRow(ColumnDto column, object? value, IDbTable table)
        {
            var sb = new StringBuilder();
            AppendFieldRow(sb, column, value, table);
            return sb.ToString();
        }

        /// <summary>
        /// Formats a column value for display, returning HTML markup.
        /// </summary>
        public string FormatValue(ColumnDto column, object? value, IDbTable table)
        {
            if (value == null || value is DBNull)
                return ValueFormatter.FormatNull();

            // Foreign key: render as link to related detail page
            if (ForeignKeyHandler.IsForeignKey(column, table))
            {
                var referencedTable = ForeignKeyHandler.GetReferencedTable(column, table);
                if (referencedTable != null)
                {
                    var displayValue = Encode(value.ToString() ?? "");
                    return $"<a href=\"{Encode(_basePath)}/view/{Encode(referencedTable.DbName)}/{Encode(value.ToString() ?? "")}\">{displayValue}</a>";
                }
            }

            var dataType = column.EffectiveDataType;

            if (TypeMapper.IsBooleanType(dataType))
                return Encode(ValueFormatter.FormatBoolean(value));

            if (TypeMapper.IsDateTimeType(dataType))
                return ValueFormatter.FormatDateTime(value);

            if (TypeMapper.IsBinaryType(dataType))
                return "<span class=\"binary-value\">(binary data)</span>";

            // Email column (via metadata override)
            var metadataType = column.GetMetadataValue("type");
            if (string.Equals(metadataType, "email", StringComparison.OrdinalIgnoreCase))
            {
                var email = value.ToString() ?? "";
                return $"<a href=\"mailto:{Encode(email)}\">{Encode(email)}</a>";
            }

            // URL column (via metadata override)
            if (string.Equals(metadataType, "url", StringComparison.OrdinalIgnoreCase))
            {
                var url = value.ToString() ?? "";
                return $"<a href=\"{Encode(url)}\">{Encode(url)}</a>";
            }

            return Encode(value.ToString() ?? "");
        }

        private void AppendFieldRow(StringBuilder sb, ColumnDto column, object? value, IDbTable table)
        {
            var label = FormatLabel(column.ColumnName);
            sb.Append($"<dt>{Encode(label)}</dt>");
            sb.Append($"<dd>{FormatValue(column, value, table)}</dd>");
        }

        private void AppendActions(StringBuilder sb, string tableName, string? recordId)
        {
            sb.Append("<div class=\"actions\">");

            if (recordId != null)
            {
                sb.Append($"<a href=\"{Encode(_basePath)}/edit/{Encode(tableName)}/{Encode(recordId)}\" class=\"btn-primary\">Edit</a>");
                sb.Append($"<a href=\"{Encode(_basePath)}/delete/{Encode(tableName)}/{Encode(recordId)}\" class=\"btn-danger\">Delete</a>");
            }

            sb.Append($"<a href=\"{Encode(_basePath)}/list/{Encode(tableName)}\" class=\"btn-secondary\">Back to List</a>");
            sb.Append("</div>");
        }

        private static string? GetPrimaryKeyValue(IDbTable table, IReadOnlyDictionary<string, object?> recordData)
        {
            var keyColumn = table.KeyColumns.FirstOrDefault();
            if (keyColumn == null) return null;

            return recordData.TryGetValue(keyColumn.ColumnName, out var value)
                ? value?.ToString()
                : null;
        }

        private static string FormatTableTitle(string tableName)
        {
            // Simple singularization: remove trailing 's' for display
            if (tableName.Length > 1 && tableName.EndsWith("s", StringComparison.Ordinal)
                && !tableName.EndsWith("ss", StringComparison.Ordinal))
                return tableName.Substring(0, tableName.Length - 1);
            return tableName;
        }

        private static string FormatLabel(string columnName)
        {
            var sb = new StringBuilder(columnName.Length + 4);
            for (var i = 0; i < columnName.Length; i++)
            {
                var c = columnName[i];
                if (c == '_' || c == '-')
                {
                    sb.Append(' ');
                    continue;
                }
                if (i == 0)
                {
                    sb.Append(char.ToUpperInvariant(c));
                    continue;
                }
                if (char.IsUpper(c) && i > 0 && !char.IsUpper(columnName[i - 1]))
                    sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
