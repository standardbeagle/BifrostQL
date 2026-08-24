using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// Generates a CREATE TABLE statement for one USER table from the loaded model,
    /// in the active connection's dialect. Pure and static (like
    /// <c>BuilderSchemaProjection</c>) so it unit-tests against a loaded model with no
    /// bridge plumbing. Deliberately better than the Tool's ExportSql sketch: dialect
    /// identifier escaping (never hard-coded brackets), length/precision rendered from
    /// the column facts, and a TABLE-level PRIMARY KEY so composite keys come out
    /// correct. Identity is emitted as a trailing comment rather than engine syntax —
    /// the engines disagree (IDENTITY / AUTO_INCREMENT / GENERATED / AUTOINCREMENT)
    /// and a wrong guess produces DDL that fails to run; a comment never lies.
    /// </summary>
    public static class TableDdlGenerator
    {
        public static string Generate(IDbTable table, ISqlDialect dialect)
        {
            var sb = new StringBuilder();
            sb.Append("CREATE TABLE ").Append(dialect.TableReference(table.TableSchema, table.DbName)).AppendLine(" (");

            var lines = new List<string>();
            foreach (var column in table.Columns.OrderBy(c => c.OrdinalPosition))
            {
                var line = new StringBuilder("    ");
                line.Append(dialect.EscapeIdentifier(column.DbName));
                line.Append(' ').Append(RenderType(column));
                line.Append(column.IsNullable ? " NULL" : " NOT NULL");
                if (column.IsIdentity)
                    line.Append(" /* identity */");
                if (column.IsComputed)
                    line.Append(" /* computed */");
                lines.Add(line.ToString());
            }

            var keyColumns = table.KeyColumns.ToList();
            if (keyColumns.Count > 0)
            {
                lines.Add("    PRIMARY KEY (" +
                    string.Join(", ", keyColumns.Select(k => dialect.EscapeIdentifier(k.ColumnName))) + ")");
            }

            sb.AppendLine(string.Join(",\n", lines));
            sb.Append(");");
            return sb.ToString();
        }

        /// <summary>
        /// The column's declared type with its real length/precision facts: `varchar(80)`,
        /// `decimal(18,2)`, `nvarchar(max)` (-1 length). A type whose declaration already
        /// carries parentheses is passed through untouched.
        /// </summary>
        private static string RenderType(ColumnDto column)
        {
            var type = column.DataType;
            if (string.IsNullOrWhiteSpace(type))
                return "sql_variant";
            if (type.Contains('('))
                return type;

            if (column.CharacterMaxLength is { } length && IsLengthType(type))
                return length < 0 ? $"{type}(max)" : $"{type}({length})";
            if (column.NumericPrecision is { } precision && IsPrecisionType(type))
                return column.NumericScale is { } scale && scale > 0
                    ? $"{type}({precision},{scale})"
                    : $"{type}({precision})";
            return type;
        }

        private static bool IsLengthType(string type)
        {
            var t = type.Trim().ToLowerInvariant();
            return t is "char" or "nchar" or "varchar" or "nvarchar" or "binary" or "varbinary"
                or "character" or "character varying" or "bit varying";
        }

        private static bool IsPrecisionType(string type)
        {
            var t = type.Trim().ToLowerInvariant();
            return t is "decimal" or "numeric";
        }
    }
}
