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

        /// <summary>
        /// Per-render state threaded through the form-building helpers instead of
        /// being stashed on the (shared, non-reentrant) builder instance: the table
        /// whose column metadata applies and the pre-fetched foreign-key options.
        /// Passing it explicitly keeps GenerateForm re-entrant and thread-safe.
        /// </summary>
        private sealed class FormRenderContext
        {
            public string? CurrentTableName { get; init; }
            public IReadOnlyDictionary<string, IReadOnlyList<(string value, string displayText)>>? ForeignKeyOptions { get; init; }
        }

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
            var renderContext = new FormRenderContext
            {
                CurrentTableName = table.DbName,
                ForeignKeyOptions = foreignKeyOptions,
            };
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
                AppendEditForm(sb, table, mode, values, errorLookup, renderContext);

            AppendFormActions(sb, table.DbName, mode);
            sb.Append("</form>");

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
            // Standalone control rendering carries no table/FK-option context, matching
            // the previous behavior where the instance fields were null on this path.
            AppendFormGroup(sb, column, mode, value, hasErrors ? fieldErrors!.ToList() : null, new FormRenderContext());
            return sb.ToString();
        }

        private void AppendEditForm(StringBuilder sb, IDbTable table, FormMode mode,
            IReadOnlyDictionary<string, string?>? values,
            Dictionary<string, List<ValidationError>> errorLookup,
            FormRenderContext renderContext)
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

                AppendFormGroup(sb, column, mode, value, fieldErrors, renderContext, table);
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

        /// <summary>
        /// The kind of HTML control a column resolves to, in priority order. Resolved
        /// once, then dispatched to the matching per-kind renderer — replacing the old
        /// guard-and-return chain so the shared form-group scaffolding (wrapper div,
        /// error markup, closing div) lives in one place.
        /// </summary>
        private enum ControlKind { ForeignKey, Enum, File, Boolean, TextArea, Input }

        private static ControlKind ResolveControlKind(ColumnDto column, ColumnMetadata? metadata, IDbTable? table)
        {
            if (table != null && ForeignKeyHandler.IsForeignKey(column, table))
                return ControlKind.ForeignKey;
            if (metadata?.EnumValues != null && metadata.EnumValues.Length > 0)
                return ControlKind.Enum;
            if (FileUploadHandler.IsFileColumn(column, metadata))
                return ControlKind.File;
            if (TypeMapper.IsBooleanType(column.EffectiveDataType))
                return ControlKind.Boolean;
            if (TypeMapper.IsTextArea(column.EffectiveDataType))
                return ControlKind.TextArea;
            return ControlKind.Input;
        }

        private void AppendFormGroup(StringBuilder sb, ColumnDto column, FormMode mode,
            string? value, List<ValidationError>? fieldErrors, FormRenderContext renderContext, IDbTable? table = null)
        {
            var hasError = fieldErrors != null && fieldErrors.Count > 0;
            var errorClass = hasError ? " error" : "";
            var columnId = column.ColumnName.ToLowerInvariant().Replace(' ', '-');
            var metadata = MergeWithSchemaRules(column, GetColumnMetadata(renderContext, column.ColumnName));

            sb.Append($"<div class=\"form-group{errorClass}\">");

            switch (ResolveControlKind(column, metadata, table))
            {
                case ControlKind.ForeignKey:
                    AppendForeignKeyControl(sb, column, table!, columnId, value, renderContext);
                    break;
                case ControlKind.Enum:
                    AppendEnumControl(sb, column, columnId, metadata!, value);
                    break;
                case ControlKind.File:
                    AppendFileControl(sb, column, columnId, metadata, value);
                    break;
                case ControlKind.Boolean:
                    AppendBooleanControl(sb, column, columnId, value, hasError);
                    break;
                case ControlKind.TextArea:
                    AppendTextAreaControl(sb, column, columnId, metadata, value, hasError);
                    break;
                default:
                    AppendInputControl(sb, column, columnId, metadata, value, hasError);
                    break;
            }

            AppendErrors(sb, columnId, fieldErrors, hasError);
            sb.Append("</div>");
        }

        private void AppendForeignKeyControl(StringBuilder sb, ColumnDto column, IDbTable table,
            string columnId, string? value, FormRenderContext renderContext)
        {
            sb.Append($"<label for=\"{Encode(columnId)}\">{Encode(FormatLabel(column.ColumnName))}</label>");

            var options = GetForeignKeyOptions(renderContext, column.ColumnName);
            var uiMode = ResolveLookupUiMode(column, table, options.Count);
            sb.Append(ForeignKeyHandler.GenerateSelect(column, options, value, uiMode));
        }

        private static void AppendEnumControl(StringBuilder sb, ColumnDto column, string columnId,
            ColumnMetadata metadata, string? value)
        {
            if (EnumHandler.ShouldUseRadio(metadata.EnumValues!.Length))
                sb.Append(EnumHandler.GenerateRadioGroup(column, FormatLabel(column.ColumnName), metadata.EnumValues, metadata.EnumDisplayNames, value));
            else
            {
                sb.Append($"<label for=\"{Encode(columnId)}\">{Encode(FormatLabel(column.ColumnName))}</label>");
                sb.Append(EnumHandler.GenerateEnumSelect(column, metadata.EnumValues, metadata.EnumDisplayNames, value));
            }
        }

        private static void AppendFileControl(StringBuilder sb, ColumnDto column, string columnId,
            ColumnMetadata? metadata, string? value)
        {
            sb.Append($"<label for=\"{Encode(columnId)}\">{Encode(FormatLabel(column.ColumnName))}</label>");
            sb.Append(FileUploadHandler.GenerateFileInput(column, metadata, hasCurrentValue: value != null));
        }

        private static void AppendBooleanControl(StringBuilder sb, ColumnDto column, string columnId,
            string? value, bool hasError)
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

        private static void AppendTextAreaControl(StringBuilder sb, ColumnDto column, string columnId,
            ColumnMetadata? metadata, string? value, bool hasError)
        {
            sb.Append($"<label for=\"{Encode(columnId)}\">{Encode(FormatLabel(column.ColumnName))}</label>");
            sb.Append($"<textarea id=\"{Encode(columnId)}\" name=\"{Encode(column.ColumnName)}\" rows=\"5\"");
            AppendConstraintAttributes(sb, column, metadata);
            AppendMetadataAttributes(sb, metadata);
            if (hasError)
                sb.Append($" aria-invalid=\"true\" aria-describedby=\"{Encode(columnId)}-error\"");
            sb.Append('>');
            sb.Append(Encode(value ?? ""));
            sb.Append("</textarea>");
        }

        private static void AppendInputControl(StringBuilder sb, ColumnDto column, string columnId,
            ColumnMetadata? metadata, string? value, bool hasError)
        {
            sb.Append($"<label for=\"{Encode(columnId)}\">{Encode(FormatLabel(column.ColumnName))}</label>");

            var inputType = metadata?.InputType ?? TypeMapper.GetInputType(column.EffectiveDataType);
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

        /// <summary>
        /// Returns pre-fetched foreign key options for the given column, or an empty list
        /// when no options have been provided. Options are supplied via the
        /// <c>foreignKeyOptions</c> parameter on GenerateForm.
        /// </summary>
        private static IReadOnlyList<(string value, string displayText)> GetForeignKeyOptions(
            FormRenderContext renderContext, string columnName)
        {
            if (renderContext.ForeignKeyOptions != null &&
                renderContext.ForeignKeyOptions.TryGetValue(columnName, out var options))
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
        /// <summary>
        /// Folds the column's schema-declared validation rules (min/max/length/
        /// pattern/required metadata, varchar length) into the code-configured
        /// form metadata. Code configuration wins per field; schema metadata
        /// fills the gaps, so a rule declared once in connection metadata is
        /// enforced server-side, advertised in _dbSchema, and emitted as HTML5
        /// validation attributes here without further configuration.
        /// Date-valued min/max bounds are carried as ISO strings (MinDate/MaxDate),
        /// formatted to match the resolved input type — yyyy-MM-dd for a date input,
        /// yyyy-MM-ddTHH:mm for a datetime-local input — so the HTML min/max attribute
        /// is one the browser actually enforces.
        /// </summary>
        internal static ColumnMetadata? MergeWithSchemaRules(ColumnDto column, ColumnMetadata? configured)
        {
            var rules = Modules.Validation.ValidationRules.ForColumn(column);
            var hasRules = rules.Pattern != null || rules.Min != null || rules.Max != null
                || rules.Step != null || rules.MinLength.HasValue || rules.MaxLength.HasValue
                || rules.RequiredExplicit || rules.InputType != null;
            if (!hasRules)
                return configured;

            // Resolve the input type the same way BuildField does, so a date bound is
            // formatted for the control it will actually render in. <input type=date>
            // wants yyyy-MM-dd; <input type=datetime-local> wants yyyy-MM-ddTHH:mm and
            // silently ignores a date-only bound.
            var effectiveInputType = configured?.InputType ?? rules.InputType
                ?? TypeMapper.GetInputType(column.EffectiveDataType);
            var dateFormat = effectiveInputType == "datetime-local" ? "yyyy-MM-ddTHH:mm" : "yyyy-MM-dd";

            return new ColumnMetadata
            {
                InputType = configured?.InputType ?? rules.InputType,
                Placeholder = configured?.Placeholder,
                Pattern = configured?.Pattern ?? rules.Pattern,
                Title = configured?.Title ?? rules.PatternMessage,
                Min = configured?.Min ?? ParseDouble(rules.Min),
                Max = configured?.Max ?? ParseDouble(rules.Max),
                // Date bounds don't parse as doubles, so carry them as ISO strings
                // formatted for the resolved input type — emitted as HTML min=/max= below.
                MinDate = configured?.MinDate ?? (rules.TryMinDate(out var minDate) ? minDate.ToString(dateFormat, CultureInfo.InvariantCulture) : null),
                MaxDate = configured?.MaxDate ?? (rules.TryMaxDate(out var maxDate) ? maxDate.ToString(dateFormat, CultureInfo.InvariantCulture) : null),
                Step = configured?.Step ?? ParseDouble(rules.Step),
                MinLength = configured?.MinLength ?? rules.MinLength,
                MaxLength = configured?.MaxLength ?? rules.MaxLength,
                Required = configured?.Required ?? (rules.RequiredExplicit ? true : null),
                EnumValues = configured?.EnumValues,
                EnumDisplayNames = configured?.EnumDisplayNames,
                Accept = configured?.Accept,
                FileStorage = configured?.FileStorage,
                StorageBucket = configured?.StorageBucket,
            };
        }

        private static double? ParseDouble(string? raw) =>
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

        internal static bool IsFieldRequired(ColumnDto column, ColumnMetadata? metadata)
        {
            if (metadata?.Required.HasValue == true)
                return metadata.Required.Value;

            return !column.IsNullable && !column.IsIdentity
                && column.GetMetadataValue(MetadataKeys.AutoPopulate.Marker) == null;
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
            // Numeric bound wins when present; otherwise emit the date bound (date
            // columns have no numeric Min/Max). One column is numeric or date, not both.
            if (metadata.Min.HasValue)
                sb.Append(" min=\"").Append(metadata.Min.Value.ToString(CultureInfo.InvariantCulture)).Append('"');
            else if (metadata.MinDate != null)
                sb.Append(" min=\"").Append(Encode(metadata.MinDate)).Append('"');
            if (metadata.Max.HasValue)
                sb.Append(" max=\"").Append(metadata.Max.Value.ToString(CultureInfo.InvariantCulture)).Append('"');
            else if (metadata.MaxDate != null)
                sb.Append(" max=\"").Append(Encode(metadata.MaxDate)).Append('"');
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

        private ColumnMetadata? GetColumnMetadata(FormRenderContext renderContext, string columnName)
        {
            if (_metadataConfiguration == null || renderContext.CurrentTableName == null)
                return null;
            return _metadataConfiguration.GetMetadata(renderContext.CurrentTableName, columnName);
        }

        private static bool HasBinaryColumn(IDbTable table, FormMode mode)
        {
            foreach (var column in table.Columns)
            {
                if (!ShouldInclude(column, mode))
                    continue;
                if (FileUploadHandler.IsFileColumn(column))
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
            if (column.GetMetadataValue(MetadataKeys.AutoPopulate.Marker) != null)
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
