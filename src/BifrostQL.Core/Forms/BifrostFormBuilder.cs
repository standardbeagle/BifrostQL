using System.Globalization;
using System.Net;
using System.Text;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Forms
{
    /// <summary>
    /// Generates HTML forms from a database model, producing accessible markup
    /// that works without JavaScript using standard form POST submission.
    /// </summary>
    public sealed class BifrostFormBuilder
    {
        private readonly IDbModel _dbModel;
        private readonly string _basePath;
        private readonly FormsMetadataConfiguration? _metadataConfiguration;
        private readonly Dictionary<string, LookupTableConfig> _lookupConfigs = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyDictionary<string, IReadOnlyList<(string value, string displayText)>>? _foreignKeyOptions;
        private string? _currentTableName;

        public BifrostFormBuilder(IDbModel dbModel, string basePath = "/bifrost",
            FormsMetadataConfiguration? metadataConfiguration = null)
        {
            _dbModel = dbModel ?? throw new ArgumentNullException(nameof(dbModel));
            _basePath = basePath.TrimEnd('/');
            _metadataConfiguration = metadataConfiguration;
            DetectLookupTables();
        }

        /// <summary>
        /// Returns the detected lookup table configurations, keyed by table name.
        /// </summary>
        public IReadOnlyDictionary<string, LookupTableConfig> LookupConfigs => _lookupConfigs;

        private void DetectLookupTables()
        {
            foreach (var table in _dbModel.Tables)
            {
                if (LookupTableDetector.IsLookupTable(table))
                {
                    _lookupConfigs[table.DbName] = LookupTableConfig.FromDetection(table);
                }
            }
        }

        /// <summary>
        /// Generates a complete HTML form for the given table and mode.
        /// For Update/Delete modes, existing values are pre-populated from <paramref name="values"/>.
        /// </summary>
        /// <param name="tableName">Database table name.</param>
        /// <param name="mode">Insert, Update, or Delete.</param>
        /// <param name="values">Current row values, keyed by column name. Required for Update and Delete modes.</param>
        /// <param name="errors">Validation errors to display, keyed by column name.</param>
        /// <param name="foreignKeyOptions">Pre-fetched options for foreign key select elements, keyed by column name.</param>
        public string GenerateForm(string tableName, FormMode mode,
            IReadOnlyDictionary<string, string?>? values = null,
            IReadOnlyList<ValidationError>? errors = null,
            IReadOnlyDictionary<string, IReadOnlyList<(string value, string displayText)>>? foreignKeyOptions = null)
        {
            var table = _dbModel.GetTableFromDbName(tableName);
            return GenerateForm(table, mode, values, errors, foreignKeyOptions);
        }

        /// <summary>
        /// Generates a complete HTML form for the given table and mode.
        /// </summary>
        public string GenerateForm(IDbTable table, FormMode mode,
            IReadOnlyDictionary<string, string?>? values = null,
            IReadOnlyList<ValidationError>? errors = null,
            IReadOnlyDictionary<string, IReadOnlyList<(string value, string displayText)>>? foreignKeyOptions = null)
        {
            _foreignKeyOptions = foreignKeyOptions;
            _currentTableName = table.DbName;
            var errorLookup = BuildErrorLookup(errors);
            var sb = new StringBuilder();
            var action = GetFormAction(table.DbName, mode, values);
            var hasFileInput = mode != FormMode.Delete && HasBinaryColumn(table, mode);

            sb.Append($"<form method=\"POST\" action=\"{Encode(action)}\" class=\"bifrost-form\"");
            if (hasFileInput)
                sb.Append(" enctype=\"multipart/form-data\"");
            sb.Append('>');

            if (mode == FormMode.Delete)
                AppendDeleteForm(sb, table, values, errorLookup);
            else
                AppendEditForm(sb, table, mode, values, errorLookup);

            AppendFormActions(sb, table.DbName, mode);
            sb.Append("</form>");

            _foreignKeyOptions = null;
            _currentTableName = null;
            return sb.ToString();
        }

        /// <summary>
        /// Generates the HTML for a single form control for the given column.
        /// </summary>
        public string GenerateFormControl(ColumnDto column, FormMode mode,
            string? value = null, IReadOnlyList<ValidationError>? fieldErrors = null)
        {
            var sb = new StringBuilder();
            var hasErrors = fieldErrors != null && fieldErrors.Count > 0;
            AppendFormGroup(sb, column, mode, value, hasErrors ? fieldErrors : null);
            return sb.ToString();
        }

        private void AppendEditForm(StringBuilder sb, IDbTable table, FormMode mode,
            IReadOnlyDictionary<string, string?>? values,
            Dictionary<string, List<ValidationError>> errorLookup)
        {
            foreach (var column in table.Columns)
            {
                if (!ShouldInclude(column, mode))
                    continue;

                string? value = null;
                values?.TryGetValue(column.ColumnName, out value);
                errorLookup.TryGetValue(column.ColumnName, out var fieldErrors);

                if (mode == FormMode.Update && column.IsPrimaryKey)
                {
                    sb.Append($"<input type=\"hidden\" name=\"{Encode(column.ColumnName)}\" value=\"{Encode(value ?? "")}\">");
                    continue;
                }

                AppendFormGroup(sb, column, mode, value, fieldErrors, table);
            }
        }

        private void AppendDeleteForm(StringBuilder sb, IDbTable table,
            IReadOnlyDictionary<string, string?>? values,
            Dictionary<string, List<ValidationError>> errorLookup)
        {
            // Hidden field for primary key(s)
            foreach (var keyColumn in table.KeyColumns)
            {
                string? keyValue = null;
                values?.TryGetValue(keyColumn.ColumnName, out keyValue);
                sb.Append($"<input type=\"hidden\" name=\"{Encode(keyColumn.ColumnName)}\" value=\"{Encode(keyValue ?? "")}\">");
            }

            // Read-only summary of the record
            sb.Append("<dl class=\"bifrost-detail\">");
            foreach (var column in table.Columns)
            {
                string? value = null;
                values?.TryGetValue(column.ColumnName, out value);
                sb.Append($"<dt>{Encode(FormatLabel(column.ColumnName))}</dt>");
                sb.Append($"<dd>{Encode(value ?? "")}</dd>");
            }
            sb.Append("</dl>");

            sb.Append("<p>Are you sure you want to delete this record?</p>");
        }

        private void AppendFormGroup(StringBuilder sb, ColumnDto column, FormMode mode,
            string? value, List<ValidationError>? fieldErrors, IDbTable? table = null)
        {
            var hasError = fieldErrors != null && fieldErrors.Count > 0;
            var errorClass = hasError ? " error" : "";
            var columnId = column.ColumnName.ToLowerInvariant().Replace(' ', '-');
            var metadata = GetColumnMetadata(column.ColumnName);

            sb.Append($"<div class=\"form-group{errorClass}\">");

            // Check for foreign key column
            if (table != null && ForeignKeyHandler.IsForeignKey(column, table))
            {
                sb.Append($"<label for=\"{Encode(columnId)}\">{Encode(FormatLabel(column.ColumnName))}</label>");

                var options = GetForeignKeyOptions(column.ColumnName);
                var uiMode = ResolveLookupUiMode(column, table, options.Count);
                sb.Append(ForeignKeyHandler.GenerateSelect(column, options, value, uiMode));

                AppendErrors(sb, columnId, fieldErrors, hasError);
                sb.Append("</div>");
                return;
            }

            // Check for enum metadata
            if (metadata?.EnumValues != null && metadata.EnumValues.Length > 0)
            {
                if (EnumHandler.ShouldUseRadio(metadata.EnumValues.Length))
                    sb.Append(EnumHandler.GenerateRadioGroup(column, FormatLabel(column.ColumnName), metadata.EnumValues, metadata.EnumDisplayNames, value));
                else
                {
                    sb.Append($"<label for=\"{Encode(columnId)}\">{Encode(FormatLabel(column.ColumnName))}</label>");
                    sb.Append(EnumHandler.GenerateEnumSelect(column, metadata.EnumValues, metadata.EnumDisplayNames, value));
                }

                AppendErrors(sb, columnId, fieldErrors, hasError);
                sb.Append("</div>");
                return;
            }

            // Check for binary column (file upload)
            if (TypeMapper.IsBinaryType(column.EffectiveDataType))
            {
                sb.Append($"<label for=\"{Encode(columnId)}\">{Encode(FormatLabel(column.ColumnName))}</label>");
                sb.Append(FileUploadHandler.GenerateFileInput(column, metadata, hasCurrentValue: value != null));

                AppendErrors(sb, columnId, fieldErrors, hasError);
                sb.Append("</div>");
                return;
            }

            var inputType = metadata?.InputType ?? TypeMapper.GetInputType(column.EffectiveDataType);

            if (TypeMapper.IsBooleanType(column.EffectiveDataType))
            {
                // Checkbox: label wraps the input
                sb.Append($"<label>");
                sb.Append($"<input type=\"checkbox\" id=\"{Encode(columnId)}\" name=\"{Encode(column.ColumnName)}\" value=\"true\"");
                if (IsTruthyValue(value))
                    sb.Append(" checked");
                if (hasError)
                    sb.Append($" aria-invalid=\"true\" aria-describedby=\"{Encode(columnId)}-error\"");
                sb.Append('>');
                sb.Append($" {Encode(FormatLabel(column.ColumnName))}");
                sb.Append("</label>");
            }
            else
            {
                sb.Append($"<label for=\"{Encode(columnId)}\">{Encode(FormatLabel(column.ColumnName))}</label>");

                if (TypeMapper.IsTextArea(column.EffectiveDataType))
                {
                    sb.Append($"<textarea id=\"{Encode(columnId)}\" name=\"{Encode(column.ColumnName)}\" rows=\"5\"");
                    AppendConstraintAttributes(sb, column, metadata);
                    AppendMetadataAttributes(sb, metadata);
                    if (hasError)
                        sb.Append($" aria-invalid=\"true\" aria-describedby=\"{Encode(columnId)}-error\"");
                    sb.Append('>');
                    sb.Append(Encode(value ?? ""));
                    sb.Append("</textarea>");
                }
                else
                {
                    sb.Append($"<input type=\"{inputType}\" id=\"{Encode(columnId)}\" name=\"{Encode(column.ColumnName)}\"");
                    if (metadata?.Step == null)
                        TypeMapper.AppendTypeAttributes(sb, column.EffectiveDataType);
                    AppendConstraintAttributes(sb, column, metadata);
                    AppendMetadataAttributes(sb, metadata);
                    if (value != null)
                        sb.Append($" value=\"{Encode(value)}\"");
                    if (hasError)
                        sb.Append($" aria-invalid=\"true\" aria-describedby=\"{Encode(columnId)}-error\"");
                    sb.Append('>');
                }
            }

            AppendErrors(sb, columnId, fieldErrors, hasError);
            sb.Append("</div>");
        }

        /// <summary>
        /// Returns pre-fetched foreign key options for the given column, or an empty list
        /// when no options have been provided. Options are supplied via the
        /// <c>foreignKeyOptions</c> parameter on GenerateForm.
        /// </summary>
        private IReadOnlyList<(string value, string displayText)> GetForeignKeyOptions(string columnName)
        {
            if (_foreignKeyOptions != null &&
                _foreignKeyOptions.TryGetValue(columnName, out var options))
                return options;
            return Array.Empty<(string, string)>();
        }

        /// <summary>
        /// Determines the lookup UI mode for a foreign key column based on whether
        /// the referenced table is a detected lookup table and the option count.
        /// Returns null (default dropdown) when the referenced table is not a lookup.
        /// </summary>
        private LookupUiMode? ResolveLookupUiMode(ColumnDto column, IDbTable table, int optionCount)
        {
            var referencedTable = ForeignKeyHandler.GetReferencedTable(column, table);
            if (referencedTable == null)
                return null;

            if (!_lookupConfigs.TryGetValue(referencedTable.DbName, out var config))
                return null;

            return LookupTableDetector.SelectUiMode(
                optionCount,
                config.DropdownThreshold,
                config.AutocompleteThreshold);
        }

        private static void AppendConstraintAttributes(StringBuilder sb, ColumnDto column, ColumnMetadata? metadata = null)
        {
            var isRequired = IsFieldRequired(column, metadata);
            if (isRequired)
            {
                sb.Append(" required");
                sb.Append(" aria-required=\"true\"");
            }
        }

        /// <summary>
        /// Determines whether a field should be marked as required.
        /// Metadata Required overrides the schema-derived state.
        /// </summary>
        internal static bool IsFieldRequired(ColumnDto column, ColumnMetadata? metadata)
        {
            if (metadata?.Required.HasValue == true)
                return metadata.Required.Value;

            return !column.IsNullable && !column.IsIdentity
                && column.GetMetadataValue("populate") == null;
        }

        private static void AppendMetadataAttributes(StringBuilder sb, ColumnMetadata? metadata)
        {
            if (metadata == null) return;

            if (metadata.Placeholder != null)
                sb.Append($" placeholder=\"{Encode(metadata.Placeholder)}\"");
            if (metadata.Pattern != null)
            {
                sb.Append($" pattern=\"{Encode(metadata.Pattern)}\"");
                if (metadata.Title != null)
                    sb.Append($" title=\"{Encode(metadata.Title)}\"");
            }
            if (metadata.Min.HasValue)
                sb.Append(" min=\"").Append(metadata.Min.Value.ToString(CultureInfo.InvariantCulture)).Append('"');
            if (metadata.Max.HasValue)
                sb.Append(" max=\"").Append(metadata.Max.Value.ToString(CultureInfo.InvariantCulture)).Append('"');
            if (metadata.Step.HasValue)
                sb.Append(" step=\"").Append(metadata.Step.Value.ToString(CultureInfo.InvariantCulture)).Append('"');
            if (metadata.MinLength.HasValue)
                sb.Append(" minlength=\"").Append(metadata.MinLength.Value).Append('"');
            if (metadata.MaxLength.HasValue)
                sb.Append(" maxlength=\"").Append(metadata.MaxLength.Value).Append('"');
        }

        private static void AppendErrors(StringBuilder sb, string columnId,
            List<ValidationError>? fieldErrors, bool hasError)
        {
            if (!hasError) return;
            foreach (var error in fieldErrors!)
            {
                sb.Append($"<span id=\"{Encode(columnId)}-error\" class=\"error-message\">{Encode(error.Message)}</span>");
            }
        }

        private ColumnMetadata? GetColumnMetadata(string columnName)
        {
            if (_metadataConfiguration == null || _currentTableName == null)
                return null;
            return _metadataConfiguration.GetMetadata(_currentTableName, columnName);
        }

        private static bool HasBinaryColumn(IDbTable table, FormMode mode)
        {
            foreach (var column in table.Columns)
            {
                if (!ShouldInclude(column, mode))
                    continue;
                if (TypeMapper.IsBinaryType(column.EffectiveDataType))
                    return true;
            }
            return false;
        }

        private void AppendFormActions(StringBuilder sb, string tableName, FormMode mode)
        {
            sb.Append("<div class=\"form-actions\">");

            var submitLabel = mode switch
            {
                FormMode.Insert => "Create",
                FormMode.Update => "Update",
                FormMode.Delete => "Delete",
                _ => "Submit",
            };

            if (mode == FormMode.Delete)
                sb.Append($"<button type=\"submit\" name=\"confirm\" value=\"yes\" class=\"btn-primary\">{submitLabel}</button>");
            else
                sb.Append($"<button type=\"submit\" class=\"btn-primary\">{submitLabel}</button>");

            sb.Append($"<a href=\"{Encode(_basePath)}/list/{Encode(tableName)}\" class=\"btn-secondary\">Cancel</a>");
            sb.Append("</div>");
        }

        private string GetFormAction(string tableName, FormMode mode, IReadOnlyDictionary<string, string?>? values)
        {
            var modeStr = mode.ToString().ToLowerInvariant();
            var baseAction = $"{_basePath}/form/{tableName}/{modeStr}";

            if (mode is FormMode.Update or FormMode.Delete && values != null)
            {
                var table = _dbModel.GetTableFromDbName(tableName);
                var keyColumn = table.KeyColumns.FirstOrDefault();
                if (keyColumn != null && values.TryGetValue(keyColumn.ColumnName, out var id) && id != null)
                    return $"{baseAction}/{id}";
            }

            return baseAction;
        }

        private static bool ShouldInclude(ColumnDto column, FormMode mode)
        {
            // Auto-populated audit columns are excluded from form rendering
            if (column.GetMetadataValue("populate") != null)
                return false;

            if (mode == FormMode.Insert)
                return !column.IsIdentity;

            return true;
        }

        private static bool IsTruthyValue(string? value)
        {
            return value is "true" or "1" or "on" or "yes" or "True" or "TRUE";
        }

        internal static string FormatLabel(string columnName)
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

        private static Dictionary<string, List<ValidationError>> BuildErrorLookup(IReadOnlyList<ValidationError>? errors)
        {
            var lookup = new Dictionary<string, List<ValidationError>>(StringComparer.OrdinalIgnoreCase);
            if (errors == null) return lookup;

            foreach (var error in errors)
            {
                if (!lookup.TryGetValue(error.FieldName, out var list))
                {
                    list = new List<ValidationError>();
                    lookup[error.FieldName] = list;
                }
                list.Add(error);
            }
            return lookup;
        }
    }
}
