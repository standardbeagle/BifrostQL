using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BifrostQL.Core.Model
{
    public sealed class ColumnDto : ISchemaNames
    {
        public string TableCatalog { get; init; } = null!;
        public string TableSchema { get; init; } = null!;
        public string TableName { get; init; } = null!;
        public string ColumnName { get; init; } = null!;
        public string DbName => ColumnName;
        public string GraphQlName { get; init; } = null!;
        public string NormalizedName { get; init; } = null!;
        public ColumnRef ColumnRef { get; init; } = null!;
        public string DataType { get; init; } = null!;
        /// <summary>
        /// Returns the effective data type, checking for metadata type override (e.g., "type: json").
        /// Use this instead of DataType for schema generation and type mapping.
        /// </summary>
        public string EffectiveDataType => GetMetadataValue("type") ?? DataType;
        public bool IsNullable { get; init; }
        public int OrdinalPosition { get; init; }
        public bool IsIdentity { get; init; } = false;
        public bool IsPrimaryKey { get; init; } = false;
        public IDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
        public string? GetMetadataValue(string property) => Metadata.TryGetValue(property, out var v) ? v?.ToString() : null;
        public bool GetMetadataBool(string property, bool defaultValue) => (Metadata.TryGetValue(property, out var v) && v?.ToString() == null) ? defaultValue : v?.ToString() == "true";
        public bool CompareMetadata(string property, string value)
        {
            if (!Metadata.TryGetValue(property, out var v)) return false;
            return string.Equals(v?.ToString(), value, StringComparison.InvariantCultureIgnoreCase);
        }

        public override string ToString()
        {
            return $"[{TableSchema}].[{TableName}].[{ColumnName}]({DataType} {(IsNullable ? "NULL" : "NOT NULL")}){{{(IsIdentity ? "id " : "")}{(IsPrimaryKey ? "PK " : "")}}}";
        }

        public static ColumnDto FromReader(IDataReader reader, Dictionary<ColumnRef, List<ColumnConstraintDto>> constraints)
        {
            string catalog = (string)reader["TABLE_CATALOG"];
            string schema = (string)reader["TABLE_SCHEMA"];
            string table = (string)reader["TABLE_NAME"];
            string column = (string)reader["COLUMN_NAME"];
            var columnRef = new ColumnRef(catalog, schema, table, column);
            var isPrimary = constraints.TryGetValue(columnRef, out var con) && con.Any(c => c.ConstraintType == "PRIMARY KEY");
            var graphQlName = column.ToGraphQl("col");

            return new ColumnDto
            {
                TableCatalog = catalog,
                TableSchema = schema,
                TableName = table,
                ColumnName = column,
                GraphQlName = graphQlName,
                NormalizedName = NormalizeColumn(column),
                ColumnRef = columnRef,
                DataType = (string)reader["DATA_TYPE"],
                IsNullable = ((string)reader["IS_NULLABLE"]) == "YES",
                OrdinalPosition = (int)reader["ORDINAL_POSITION"],
                IsIdentity = (int)reader["IS_IDENTITY"] == 1,
                IsPrimaryKey = isPrimary,
            };
        }

        private static string NormalizeColumn(string column)
        {
            if (string.Equals("id", column, StringComparison.InvariantCultureIgnoreCase))
                return "id";
            if (column.EndsWith("id", StringComparison.InvariantCultureIgnoreCase))
            {
                var tableName = column.Substring(0, column.Length - 2);
                return DbModel.Pluralizer.Singularize(tableName);
            }
            return column;
        }
    }
}
