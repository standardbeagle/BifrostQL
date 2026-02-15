using System.Net;
using System.Text;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Forms
{
    /// <summary>
    /// Detects foreign key relationships from the database model and generates
    /// HTML select elements for FK columns. Options are populated with data
    /// from the referenced table.
    /// </summary>
    public sealed class ForeignKeyHandler
    {
        private static readonly string[] DisplayColumnPriority = { "name", "title", "description" };

        /// <summary>
        /// Returns true when the column participates in a single-link relationship
        /// as the child (foreign key) side.
        /// </summary>
        public static bool IsForeignKey(ColumnDto column, IDbTable table)
        {
            foreach (var link in table.SingleLinks.Values)
            {
                if (string.Equals(link.ChildId.ColumnName, column.ColumnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the referenced (parent) table for a foreign key column,
        /// or null if the column is not a foreign key.
        /// </summary>
        public static IDbTable? GetReferencedTable(ColumnDto column, IDbTable table)
        {
            foreach (var link in table.SingleLinks.Values)
            {
                if (string.Equals(link.ChildId.ColumnName, column.ColumnName, StringComparison.OrdinalIgnoreCase))
                    return link.ParentTable;
            }
            return null;
        }

        /// <summary>
        /// Returns the primary key column name of the referenced table for a foreign key column,
        /// or null if the column is not a foreign key.
        /// </summary>
        public static string? GetReferencedKeyColumn(ColumnDto column, IDbTable table)
        {
            foreach (var link in table.SingleLinks.Values)
            {
                if (string.Equals(link.ChildId.ColumnName, column.ColumnName, StringComparison.OrdinalIgnoreCase))
                    return link.ParentId.ColumnName;
            }
            return null;
        }

        /// <summary>
        /// Selects the best column to use as the display label in a select dropdown.
        /// Priority: columns named "name", "title", "description", then the first
        /// varchar/nvarchar column, and finally the primary key column.
        /// </summary>
        public static string GetDisplayColumn(IDbTable referencedTable)
        {
            var columns = referencedTable.Columns.ToList();

            // Priority 1: well-known display column names
            foreach (var preferred in DisplayColumnPriority)
            {
                var match = columns.FirstOrDefault(c =>
                    string.Equals(c.ColumnName, preferred, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match.ColumnName;
            }

            // Priority 2: first varchar/nvarchar column that isn't a primary key
            var firstVarchar = columns.FirstOrDefault(c =>
                !c.IsPrimaryKey &&
                IsTextColumn(c.DataType));
            if (firstVarchar != null)
                return firstVarchar.ColumnName;

            // Priority 3: primary key column
            var pk = columns.FirstOrDefault(c => c.IsPrimaryKey);
            return pk?.ColumnName ?? columns.First().ColumnName;
        }

        /// <summary>
        /// Selects the display column using lookup column roles when available.
        /// Falls back to standard heuristic detection if roles have no label column.
        /// </summary>
        public static string GetDisplayColumn(IDbTable referencedTable, LookupColumnRoles? roles)
        {
            if (roles?.LabelColumn != null)
                return roles.LabelColumn;
            return GetDisplayColumn(referencedTable);
        }

        /// <summary>
        /// Generates a complete HTML select element for a foreign key column.
        /// Options are provided as key-value pairs (value, display text).
        /// </summary>
        /// <param name="column">The FK column on the current table.</param>
        /// <param name="options">Available options as (value, displayText) pairs.</param>
        /// <param name="currentValue">The currently selected value, used to mark the selected option.</param>
        /// <param name="uiMode">Optional UI mode override for lookup-aware rendering.</param>
        public static string GenerateSelect(ColumnDto column, IReadOnlyList<(string value, string displayText)> options,
            string? currentValue = null, LookupUiMode? uiMode = null)
        {
            var sb = new StringBuilder();
            var columnId = column.ColumnName.ToLowerInvariant().Replace(' ', '-');

            sb.Append($"<select id=\"{Encode(columnId)}\" name=\"{Encode(column.ColumnName)}\"");

            if (uiMode == LookupUiMode.Autocomplete)
                sb.Append(" data-autocomplete=\"true\"");

            if (!column.IsNullable && !column.IsIdentity)
            {
                if (column.GetMetadataValue("populate") == null)
                {
                    sb.Append(" required");
                    sb.Append(" aria-required=\"true\"");
                }
            }

            sb.Append('>');

            // Empty placeholder option
            sb.Append("<option value=\"\">-- Select --</option>");

            foreach (var (value, displayText) in options)
            {
                sb.Append($"<option value=\"{Encode(value)}\"");
                if (currentValue != null && string.Equals(value, currentValue, StringComparison.Ordinal))
                    sb.Append(" selected");
                sb.Append($">{Encode(displayText)}</option>");
            }

            sb.Append("</select>");
            return sb.ToString();
        }

        private static bool IsTextColumn(string dataType)
        {
            var normalized = dataType.ToLowerInvariant().Trim();
            return normalized is "varchar" or "nvarchar" or "char" or "nchar";
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
