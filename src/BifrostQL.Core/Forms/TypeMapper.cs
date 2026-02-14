using System.Text;

namespace BifrostQL.Core.Forms
{
    /// <summary>
    /// Maps SQL database column types to HTML5 input types and attributes.
    /// </summary>
    public static class TypeMapper
    {
        /// <summary>
        /// Returns the HTML5 input type string for the given database data type.
        /// Callers should pass <see cref="Model.ColumnDto.EffectiveDataType"/> so metadata overrides are respected.
        /// </summary>
        public static string GetInputType(string dataType)
        {
            return NormalizeType(dataType) switch
            {
                "int" or "bigint" or "smallint" or "tinyint" => "number",
                "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "number",
                "bit" or "boolean" => "checkbox",
                "date" => "date",
                "datetime" or "datetime2" or "timestamp" or "smalldatetime" => "datetime-local",
                "datetimeoffset" => "datetime-local",
                "time" => "time",
                "uniqueidentifier" or "uuid" => "text",
                "text" or "ntext" => "textarea",
                _ => "text",
            };
        }

        /// <summary>
        /// Returns true when the column should render as a textarea instead of an input element.
        /// </summary>
        public static bool IsTextArea(string dataType)
        {
            var normalized = NormalizeType(dataType);
            return normalized is "text" or "ntext";
        }

        /// <summary>
        /// Appends HTML attributes (step, pattern, etc.) appropriate for the data type.
        /// The caller owns the surrounding element; this writes only attribute key=value pairs.
        /// </summary>
        public static void AppendTypeAttributes(StringBuilder sb, string dataType)
        {
            switch (NormalizeType(dataType))
            {
                case "int" or "bigint" or "smallint" or "tinyint":
                    sb.Append(" step=\"1\"");
                    break;
                case "decimal" or "numeric" or "money" or "smallmoney":
                    sb.Append(" step=\"0.01\"");
                    break;
                case "float" or "real":
                    sb.Append(" step=\"any\"");
                    break;
                case "uniqueidentifier" or "uuid":
                    sb.Append(" pattern=\"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\"");
                    break;
            }
        }

        /// <summary>
        /// Returns true when the database type represents a numeric value.
        /// </summary>
        public static bool IsNumericType(string dataType)
        {
            return NormalizeType(dataType) is
                "int" or "bigint" or "smallint" or "tinyint" or
                "decimal" or "numeric" or "money" or "smallmoney" or
                "float" or "real";
        }

        /// <summary>
        /// Returns true when the database type represents a date or time value.
        /// </summary>
        public static bool IsDateTimeType(string dataType)
        {
            return NormalizeType(dataType) is
                "date" or "datetime" or "datetime2" or "smalldatetime" or
                "datetimeoffset" or "time" or "timestamp";
        }

        /// <summary>
        /// Returns true when the database type represents a boolean value.
        /// </summary>
        public static bool IsBooleanType(string dataType)
        {
            return NormalizeType(dataType) is "bit" or "boolean";
        }

        /// <summary>
        /// Returns true when the database type represents a binary value
        /// (varbinary, binary, image, blob).
        /// </summary>
        public static bool IsBinaryType(string dataType)
        {
            return NormalizeType(dataType) is "varbinary" or "binary" or "image" or "blob";
        }

        private static string NormalizeType(string dataType)
        {
            return dataType.ToLowerInvariant().Trim();
        }
    }
}
