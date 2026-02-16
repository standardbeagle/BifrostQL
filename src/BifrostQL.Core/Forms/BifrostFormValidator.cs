using System.Globalization;
using System.Text.RegularExpressions;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Forms
{
    /// <summary>
    /// Validates form submission data against the database schema constraints
    /// and optional metadata-driven validation rules.
    /// </summary>
    public sealed class BifrostFormValidator
    {
        /// <summary>
        /// Validates a dictionary of form field values against the table schema.
        /// Skips IDENTITY columns (auto-generated) and primary keys during insert validation.
        /// </summary>
        public ValidationResult Validate(IReadOnlyDictionary<string, string?> formValues, IDbTable table, FormMode mode)
        {
            return Validate(formValues, table, mode, metadataConfig: null);
        }

        /// <summary>
        /// Validates a dictionary of form field values against the table schema
        /// and metadata-driven validation rules.
        /// When metadata is provided, rules such as MinLength, MaxLength, Min, Max,
        /// Pattern, and input type (email/url) are enforced, matching the HTML5
        /// attributes emitted by BifrostFormBuilder.
        /// </summary>
        public ValidationResult Validate(IReadOnlyDictionary<string, string?> formValues, IDbTable table, FormMode mode,
            FormsMetadataConfiguration? metadataConfig)
        {
            var errors = new List<ValidationError>();

            foreach (var column in table.Columns)
            {
                if (ShouldSkipValidation(column, mode))
                    continue;

                formValues.TryGetValue(column.ColumnName, out var value);
                var metadata = metadataConfig?.GetMetadata(table.DbName, column.ColumnName);

                if (!ValidateRequired(column, value, mode, metadata, errors))
                    continue;

                if (string.IsNullOrEmpty(value))
                    continue;

                ValidateType(column, value, errors);
                ValidateMetadata(column, value, metadata, errors);
            }

            return new ValidationResult(errors);
        }

        private static bool ShouldSkipValidation(ColumnDto column, FormMode mode)
        {
            if (column.IsIdentity)
                return true;

            if (mode == FormMode.Delete)
                return !column.IsPrimaryKey;

            // Skip primary keys during Insert/Update (they're typically auto-generated or set via WHERE clause)
            if (mode != FormMode.Delete && column.IsPrimaryKey)
                return true;

            return false;
        }

        /// <summary>
        /// Validates required constraint. Returns true if validation should continue
        /// to further checks, false if the field is missing/empty and already errored.
        /// </summary>
        private static bool ValidateRequired(ColumnDto column, string? value, FormMode mode,
            ColumnMetadata? metadata, List<ValidationError> errors)
        {
            if (mode == FormMode.Delete)
                return true;

            var isRequired = BifrostFormBuilder.IsFieldRequired(column, metadata);

            if (isRequired && string.IsNullOrEmpty(value))
            {
                errors.Add(new ValidationError(column.ColumnName, $"{FormatLabel(column.ColumnName)} is required"));
                return false;
            }

            return true;
        }

        private static void ValidateType(ColumnDto column, string value, List<ValidationError> errors)
        {
            var dataType = column.EffectiveDataType;

            if (TypeMapper.IsNumericType(dataType))
            {
                if (!decimal.TryParse(value, out _))
                    errors.Add(new ValidationError(column.ColumnName, $"{column.ColumnName} must be a valid number"));
            }
            else if (TypeMapper.IsBooleanType(dataType))
            {
                if (!IsBooleanValue(value))
                    errors.Add(new ValidationError(column.ColumnName, $"{column.ColumnName} must be true or false"));
            }
            else if (TypeMapper.IsDateTimeType(dataType))
            {
                if (!DateTime.TryParse(value, out _) && !DateTimeOffset.TryParse(value, out _))
                    errors.Add(new ValidationError(column.ColumnName, $"{column.ColumnName} must be a valid date/time"));
            }
        }

        private static void ValidateMetadata(ColumnDto column, string value,
            ColumnMetadata? metadata, List<ValidationError> errors)
        {
            if (metadata == null)
                return;

            var label = FormatLabel(column.ColumnName);

            if (metadata.MinLength.HasValue && value.Length < metadata.MinLength.Value)
                errors.Add(new ValidationError(column.ColumnName,
                    $"{label} must be at least {metadata.MinLength.Value} characters"));

            if (metadata.MaxLength.HasValue && value.Length > metadata.MaxLength.Value)
                errors.Add(new ValidationError(column.ColumnName,
                    $"{label} must be at most {metadata.MaxLength.Value} characters"));

            if (TypeMapper.IsNumericType(column.EffectiveDataType) && decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var numValue))
            {
                if (metadata.Min.HasValue && numValue < (decimal)metadata.Min.Value)
                    errors.Add(new ValidationError(column.ColumnName,
                        $"{label} must be at least {metadata.Min.Value.ToString(CultureInfo.InvariantCulture)}"));

                if (metadata.Max.HasValue && numValue > (decimal)metadata.Max.Value)
                    errors.Add(new ValidationError(column.ColumnName,
                        $"{label} must be at most {metadata.Max.Value.ToString(CultureInfo.InvariantCulture)}"));
            }

            if (metadata.Pattern != null)
            {
                try
                {
                    // HTML5 pattern attribute auto-anchors with ^(?:...)$, so replicate that behavior
                    var anchoredPattern = metadata.Pattern;
                    if (!anchoredPattern.StartsWith("^"))
                        anchoredPattern = "^(?:" + anchoredPattern + ")$";

                    if (!Regex.IsMatch(value, anchoredPattern))
                    {
                        var message = metadata.Title ?? $"{label} format is invalid";
                        errors.Add(new ValidationError(column.ColumnName, message));
                    }
                }
                catch (ArgumentException)
                {
                    errors.Add(new ValidationError(column.ColumnName, $"{label} has an invalid validation pattern"));
                }
            }

            if (metadata.InputType == "email" && !IsValidEmail(value))
                errors.Add(new ValidationError(column.ColumnName, "Invalid email address"));

            if (metadata.InputType == "url" && !IsValidUrl(value))
                errors.Add(new ValidationError(column.ColumnName, "Invalid URL"));
        }

        private static bool IsValidEmail(string value)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(value);
                return addr.Address == value;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool IsValidUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static bool IsBooleanValue(string value)
        {
            return value is "true" or "false" or "1" or "0" or "on" or "off" or "yes" or "no";
        }

        private static string FormatLabel(string columnName)
            => BifrostFormBuilder.FormatLabel(columnName);
    }

    /// <summary>
    /// Represents the result of form validation, containing any errors found.
    /// </summary>
    public sealed class ValidationResult
    {
        public ValidationResult(IReadOnlyList<ValidationError> errors)
        {
            Errors = errors;
        }

        public IReadOnlyList<ValidationError> Errors { get; }
        public bool IsValid => Errors.Count == 0;
    }

    /// <summary>
    /// Represents a single validation error for a specific form field.
    /// </summary>
    public sealed class ValidationError
    {
        public ValidationError(string fieldName, string message)
        {
            FieldName = fieldName;
            Message = message;
        }

        public string FieldName { get; }
        public string Message { get; }
    }
}
