using System.Net;
using System.Text;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Forms
{
    /// <summary>
    /// Generates radio button groups or select dropdowns for enum columns.
    /// Radio buttons are used for small option sets (up to 4 values),
    /// and select dropdowns for larger sets.
    /// </summary>
    public static class EnumHandler
    {
        private const int RadioThreshold = 4;

        /// <summary>
        /// Returns true when the number of enum options is small enough
        /// for radio buttons (4 or fewer).
        /// </summary>
        public static bool ShouldUseRadio(int enumCount) => enumCount is > 0 and <= RadioThreshold;

        /// <summary>
        /// Generates a fieldset containing radio buttons for an enum column.
        /// The <paramref name="label"/> text is used as the fieldset legend.
        /// </summary>
        public static string GenerateRadioGroup(ColumnDto column, string label, string[] enumValues,
            IReadOnlyDictionary<string, string>? displayNames = null, string? currentValue = null)
        {
            var sb = new StringBuilder();
            var columnName = column.ColumnName;
            var isRequired = !column.IsNullable && !column.IsIdentity
                && column.GetMetadataValue("populate") == null;

            sb.Append("<fieldset>");
            sb.Append($"<legend>{Encode(label)}</legend>");

            for (var i = 0; i < enumValues.Length; i++)
            {
                var value = enumValues[i];
                var display = GetDisplayName(value, displayNames);
                var isChecked = currentValue != null && string.Equals(value, currentValue, StringComparison.Ordinal);

                sb.Append($"<label><input type=\"radio\" name=\"{Encode(columnName)}\" value=\"{Encode(value)}\"");

                // HTML5: required on the first radio makes the group required
                if (isRequired && i == 0)
                    sb.Append(" required");

                if (isChecked)
                    sb.Append(" checked");

                sb.Append($"> {Encode(display)}</label>");
            }

            sb.Append("</fieldset>");
            return sb.ToString();
        }

        /// <summary>
        /// Generates a select dropdown for an enum column.
        /// </summary>
        public static string GenerateEnumSelect(ColumnDto column, string[] enumValues,
            IReadOnlyDictionary<string, string>? displayNames = null, string? currentValue = null)
        {
            var sb = new StringBuilder();
            var columnId = column.ColumnName.ToLowerInvariant().Replace(' ', '-');
            var isRequired = !column.IsNullable && !column.IsIdentity
                && column.GetMetadataValue("populate") == null;

            sb.Append($"<select id=\"{Encode(columnId)}\" name=\"{Encode(column.ColumnName)}\"");
            if (isRequired)
            {
                sb.Append(" required");
                sb.Append(" aria-required=\"true\"");
            }
            sb.Append('>');

            sb.Append("<option value=\"\">-- Select --</option>");

            foreach (var value in enumValues)
            {
                var display = GetDisplayName(value, displayNames);
                sb.Append($"<option value=\"{Encode(value)}\"");
                if (currentValue != null && string.Equals(value, currentValue, StringComparison.Ordinal))
                    sb.Append(" selected");
                sb.Append($">{Encode(display)}</option>");
            }

            sb.Append("</select>");
            return sb.ToString();
        }

        private static string GetDisplayName(string value, IReadOnlyDictionary<string, string>? displayNames)
        {
            if (displayNames != null && displayNames.TryGetValue(value, out var display))
                return display;
            return value;
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
