using System.Net;
using System.Text;
using BifrostQL.Core.Forms;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Views
{
    /// <summary>
    /// Generates list view HTML for a table, including sortable column headers,
    /// pagination navigation, and a search/filter form. Works without JavaScript.
    /// </summary>
    public sealed class ListViewBuilder
    {
        private const int DefaultMaxColumns = 7;

        private readonly IDbModel _dbModel;
        private readonly string _basePath;
        private readonly int _maxColumns;

        public ListViewBuilder(IDbModel dbModel, string basePath = "/bifrost", int maxColumns = DefaultMaxColumns)
        {
            _dbModel = dbModel ?? throw new ArgumentNullException(nameof(dbModel));
            _basePath = basePath.TrimEnd('/');
            _maxColumns = maxColumns;
        }

        /// <summary>
        /// Generates a complete list view with table, sorting, pagination, and search.
        /// </summary>
        /// <param name="tableName">The database table name.</param>
        /// <param name="records">Row data, each row keyed by column name.</param>
        /// <param name="pagination">Current pagination state.</param>
        /// <param name="currentSort">Currently sorted column name, or null.</param>
        /// <param name="currentDir">Current sort direction ("asc" or "desc"), or null.</param>
        /// <param name="currentSearch">Current search term, or null.</param>
        public string GenerateListView(string tableName,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> records,
            PaginationInfo pagination,
            string? currentSort = null, string? currentDir = null,
            string? currentSearch = null)
        {
            var table = _dbModel.GetTableFromDbName(tableName);
            return GenerateListView(table, records, pagination, currentSort, currentDir, currentSearch);
        }

        /// <summary>
        /// Generates a complete list view with table, sorting, pagination, and search.
        /// </summary>
        public string GenerateListView(IDbTable table,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> records,
            PaginationInfo pagination,
            string? currentSort = null, string? currentDir = null,
            string? currentSearch = null)
        {
            var columns = SelectDisplayColumns(table);
            var sb = new StringBuilder();

            sb.Append("<div class=\"bifrost-list\">");
            sb.Append($"<h1>{Encode(table.DbName)}</h1>");

            sb.Append("<div class=\"list-actions\">");
            sb.Append($"<a href=\"{Encode(_basePath)}/new/{Encode(table.DbName)}\" class=\"btn-primary\">New {Encode(FormatTableTitle(table.DbName))}</a>");
            sb.Append("</div>");

            AppendSearchForm(sb, table.DbName, currentSearch, currentSort, currentDir);

            if (records.Count == 0)
            {
                sb.Append("<p class=\"empty-list\">No records found.</p>");
            }
            else
            {
                sb.Append("<table>");
                AppendTableHeader(sb, columns, currentSort, currentDir, currentSearch, pagination);
                AppendTableBody(sb, records, columns, table);
                sb.Append("</table>");
            }

            AppendPagination(sb, pagination, currentSort, currentDir, currentSearch);

            sb.Append("</div>");
            return sb.ToString();
        }

        /// <summary>
        /// Generates the table header row with sortable column links.
        /// </summary>
        public string GenerateTableHeader(IReadOnlyList<ColumnDto> columns,
            string? currentSort = null, string? currentDir = null,
            string? currentSearch = null, PaginationInfo? pagination = null)
        {
            var sb = new StringBuilder();
            AppendTableHeader(sb, columns, currentSort, currentDir, currentSearch, pagination);
            return sb.ToString();
        }

        /// <summary>
        /// Generates a single table row for a record.
        /// </summary>
        public string GenerateTableRow(IReadOnlyDictionary<string, object?> record,
            IReadOnlyList<ColumnDto> columns, IDbTable table)
        {
            var sb = new StringBuilder();
            AppendTableRow(sb, record, columns, table);
            return sb.ToString();
        }

        /// <summary>
        /// Generates the pagination navigation element.
        /// </summary>
        public string GeneratePagination(PaginationInfo pagination,
            string? currentSort = null, string? currentDir = null,
            string? currentSearch = null)
        {
            var sb = new StringBuilder();
            AppendPagination(sb, pagination, currentSort, currentDir, currentSearch);
            return sb.ToString();
        }

        /// <summary>
        /// Generates the search/filter form.
        /// </summary>
        public string GenerateSearchForm(string tableName, string? currentSearch = null,
            string? currentSort = null, string? currentDir = null)
        {
            var sb = new StringBuilder();
            AppendSearchForm(sb, tableName, currentSearch, currentSort, currentDir);
            return sb.ToString();
        }

        private void AppendSearchForm(StringBuilder sb, string tableName, string? currentSearch,
            string? currentSort, string? currentDir)
        {
            sb.Append("<form method=\"GET\" class=\"bifrost-search\">");

            // Preserve sort params as hidden fields
            if (currentSort != null)
                sb.Append($"<input type=\"hidden\" name=\"sort\" value=\"{Encode(currentSort)}\">");
            if (currentDir != null)
                sb.Append($"<input type=\"hidden\" name=\"dir\" value=\"{Encode(currentDir)}\">");

            sb.Append("<label for=\"search\">Search</label>");
            sb.Append($"<input type=\"search\" id=\"search\" name=\"search\" value=\"{Encode(currentSearch ?? "")}\" placeholder=\"Search {Encode(tableName)}...\">");
            sb.Append("<button type=\"submit\" class=\"btn-secondary\">Search</button>");

            if (!string.IsNullOrEmpty(currentSearch))
                sb.Append($"<a href=\"?\" class=\"btn-secondary\">Clear</a>");

            sb.Append("</form>");
        }

        private void AppendTableHeader(StringBuilder sb, IReadOnlyList<ColumnDto> columns,
            string? currentSort, string? currentDir, string? currentSearch,
            PaginationInfo? pagination)
        {
            sb.Append("<thead><tr>");

            foreach (var column in columns)
            {
                // Determine sort direction for this column's link
                var nextDir = "asc";
                if (string.Equals(currentSort, column.ColumnName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(currentDir, "asc", StringComparison.OrdinalIgnoreCase))
                {
                    nextDir = "desc";
                }

                var sortUrl = BuildQueryString(
                    ("sort", column.ColumnName),
                    ("dir", nextDir),
                    ("search", currentSearch),
                    ("size", pagination?.PageSize.ToString()));

                sb.Append($"<th><a href=\"?{sortUrl}\">{Encode(FormatLabel(column.ColumnName))}</a>");

                // Sort indicator
                if (string.Equals(currentSort, column.ColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    var indicator = string.Equals(currentDir, "desc", StringComparison.OrdinalIgnoreCase)
                        ? " &#9660;" : " &#9650;";
                    sb.Append(indicator);
                }

                sb.Append("</th>");
            }

            sb.Append("<th>Actions</th>");
            sb.Append("</tr></thead>");
        }

        private void AppendTableBody(StringBuilder sb,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> records,
            IReadOnlyList<ColumnDto> columns, IDbTable table)
        {
            sb.Append("<tbody>");
            foreach (var record in records)
            {
                AppendTableRow(sb, record, columns, table);
            }
            sb.Append("</tbody>");
        }

        private void AppendTableRow(StringBuilder sb, IReadOnlyDictionary<string, object?> record,
            IReadOnlyList<ColumnDto> columns, IDbTable table)
        {
            var recordId = GetPrimaryKeyValue(table, record);

            sb.Append("<tr>");
            foreach (var column in columns)
            {
                record.TryGetValue(column.ColumnName, out var value);
                sb.Append("<td>");
                sb.Append(FormatCellValue(column, value, table, recordId));
                sb.Append("</td>");
            }

            // Actions column
            sb.Append("<td>");
            if (recordId != null)
            {
                sb.Append($"<a href=\"{Encode(_basePath)}/view/{Encode(table.DbName)}/{Encode(recordId)}\">View</a> ");
                sb.Append($"<a href=\"{Encode(_basePath)}/edit/{Encode(table.DbName)}/{Encode(recordId)}\">Edit</a> ");
                sb.Append($"<a href=\"{Encode(_basePath)}/delete/{Encode(table.DbName)}/{Encode(recordId)}\">Delete</a>");
            }
            sb.Append("</td>");
            sb.Append("</tr>");
        }

        private void AppendPagination(StringBuilder sb, PaginationInfo pagination,
            string? currentSort, string? currentDir, string? currentSearch)
        {
            sb.Append("<nav class=\"pagination\">");

            if (pagination.HasPrevious)
            {
                sb.Append($"<a href=\"?{BuildPageQuery(1, pagination.PageSize, currentSort, currentDir, currentSearch)}\" class=\"btn-secondary\">First</a>");
                sb.Append($"<a href=\"?{BuildPageQuery(pagination.CurrentPage - 1, pagination.PageSize, currentSort, currentDir, currentSearch)}\" class=\"btn-secondary\">Previous</a>");
            }
            else
            {
                sb.Append("<span class=\"btn-secondary disabled\">First</span>");
                sb.Append("<span class=\"btn-secondary disabled\">Previous</span>");
            }

            sb.Append($"<span>Page {pagination.CurrentPage} of {pagination.TotalPages}</span>");

            if (pagination.HasNext)
            {
                sb.Append($"<a href=\"?{BuildPageQuery(pagination.CurrentPage + 1, pagination.PageSize, currentSort, currentDir, currentSearch)}\" class=\"btn-secondary\">Next</a>");
                sb.Append($"<a href=\"?{BuildPageQuery(pagination.TotalPages, pagination.PageSize, currentSort, currentDir, currentSearch)}\" class=\"btn-secondary\">Last</a>");
            }
            else
            {
                sb.Append("<span class=\"btn-secondary disabled\">Next</span>");
                sb.Append("<span class=\"btn-secondary disabled\">Last</span>");
            }

            sb.Append("</nav>");
        }

        private string FormatCellValue(ColumnDto column, object? value, IDbTable table, string? recordId)
        {
            if (value == null || value is DBNull)
                return "";

            var dataType = column.EffectiveDataType;

            if (TypeMapper.IsBooleanType(dataType))
                return Encode(ValueFormatter.FormatBoolean(value));

            if (TypeMapper.IsDateTimeType(dataType))
                return ValueFormatter.FormatDateTime(value);

            if (TypeMapper.IsBinaryType(dataType))
                return "(binary)";

            var text = value.ToString() ?? "";

            // First text column links to the detail view
            if (recordId != null && IsFirstTextColumn(column, table))
                return $"<a href=\"{Encode(_basePath)}/view/{Encode(table.DbName)}/{Encode(recordId)}\">{Encode(text)}</a>";

            // Truncate long text in list views
            if (text.Length > 100)
                return ValueFormatter.TruncateText(text, 100);

            return Encode(text);
        }

        private static bool IsFirstTextColumn(ColumnDto column, IDbTable table)
        {
            foreach (var col in table.Columns)
            {
                if (col.IsPrimaryKey) continue;
                if (TypeMapper.IsBinaryType(col.EffectiveDataType)) continue;
                if (TypeMapper.IsBooleanType(col.EffectiveDataType)) continue;

                return string.Equals(col.ColumnName, column.ColumnName, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private IReadOnlyList<ColumnDto> SelectDisplayColumns(IDbTable table)
        {
            var columns = new List<ColumnDto>();
            foreach (var column in table.Columns)
            {
                if (columns.Count >= _maxColumns)
                    break;
                columns.Add(column);
            }
            return columns;
        }

        private static string BuildPageQuery(int page, int pageSize, string? sort, string? dir, string? search)
        {
            return BuildQueryString(
                ("page", page.ToString()),
                ("size", pageSize.ToString()),
                ("sort", sort),
                ("dir", dir),
                ("search", search));
        }

        private static string BuildQueryString(params (string key, string? value)[] parameters)
        {
            var parts = new List<string>();
            foreach (var (key, value) in parameters)
            {
                if (string.IsNullOrEmpty(value)) continue;
                parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
            }
            return string.Join("&amp;", parts);
        }

        private static string? GetPrimaryKeyValue(IDbTable table, IReadOnlyDictionary<string, object?> record)
        {
            var keyColumn = table.KeyColumns.FirstOrDefault();
            if (keyColumn == null) return null;

            return record.TryGetValue(keyColumn.ColumnName, out var value)
                ? value?.ToString()
                : null;
        }

        private static string FormatTableTitle(string tableName)
        {
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
