using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

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
        
        /// <summary>
        /// The detected prefix for this column, if any (e.g., "user_" for "user_id").
        /// Set during prefix detection phase.
        /// </summary>
        public string? DetectedPrefix { get; init; }
        
        /// <summary>
        /// The column name with the detected prefix stripped.
        /// Returns the original column name if no prefix was detected.
        /// </summary>
        public string NameWithoutPrefix => DetectedPrefix != null 
            ? ColumnPrefixDetector.StripPrefix(ColumnName, DetectedPrefix) 
            : ColumnName;
        
        /// <summary>
        /// Suggests a GraphQL name with prefix stripped for cleaner schema.
        /// </summary>
        public string SuggestStrippedGraphQlName(string? prefixToStrip = null)
        {
            var effectivePrefix = prefixToStrip ?? DetectedPrefix;
            if (effectivePrefix == null)
                return GraphQlName;
            
            var stripped = ColumnPrefixDetector.StripPrefix(ColumnName, effectivePrefix);
            return stripped.ToGraphQl("col");
        }
        
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
        public bool IsComputed { get; init; } = false;
        public bool IsPrimaryKey { get; init; } = false;
        public bool IsUnique { get; init; } = false;
        public IDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
        public string? GetMetadataValue(string property) => Metadata.TryGetValue(property, out var v) ? v?.ToString() : null;
        public bool GetMetadataBool(string property, bool defaultValue) => (!Metadata.TryGetValue(property, out var v) || v?.ToString() == null) ? defaultValue : v.ToString() == "true";
        public bool CompareMetadata(string property, string value)
        {
            if (!Metadata.TryGetValue(property, out var v)) return false;
            return string.Equals(v?.ToString(), value, StringComparison.InvariantCultureIgnoreCase);
        }

        public override string ToString()
        {
            return $"[{TableSchema}].[{TableName}].[{ColumnName}]({DataType} {(IsNullable ? "NULL" : "NOT NULL")}){{{(IsIdentity ? "id " : "")}{(IsPrimaryKey ? "PK " : "")}{(IsUnique ? "UQ " : "")}{(IsComputed ? "computed " : "")}}}";
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
                OrdinalPosition = Convert.ToInt32(reader["ORDINAL_POSITION"]),
                IsIdentity = Convert.ToInt32(reader["IS_IDENTITY"]) == 1,
                IsPrimaryKey = isPrimary,
                IsUnique = constraints.TryGetValue(columnRef, out var uniqueCons) && uniqueCons.Any(c => c.ConstraintType == "UNIQUE"),
            };
        }

        /// <summary>
        /// Creates a copy of this ColumnDto with a new GraphQL name.
        /// Used for deduplicating GraphQL names within a table.
        /// </summary>
        public ColumnDto WithGraphQlName(string newGraphQlName)
        {
            return new ColumnDto
            {
                TableCatalog = TableCatalog,
                TableSchema = TableSchema,
                TableName = TableName,
                ColumnName = ColumnName,
                GraphQlName = newGraphQlName,
                NormalizedName = NormalizedName,
                ColumnRef = ColumnRef,
                DataType = DataType,
                IsNullable = IsNullable,
                OrdinalPosition = OrdinalPosition,
                IsIdentity = IsIdentity,
                IsPrimaryKey = IsPrimaryKey,
                IsUnique = IsUnique,
                Metadata = Metadata,
            };
        }

        /// <summary>
        /// Makes GraphQL names unique within a collection of columns by appending numeric suffixes to duplicates.
        /// Returns a new list with deduplicated column DTOs.
        /// </summary>
        public static List<ColumnDto> DeduplicateGraphQlNames(IEnumerable<ColumnDto> columns)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ColumnDto>();
            
            foreach (var column in columns)
            {
                var baseName = column.GraphQlName;
                if (seen.TryGetValue(baseName, out var count))
                {
                    count++;
                    seen[baseName] = count;
                    result.Add(column.WithGraphQlName($"{baseName}_{count}"));
                }
                else
                {
                    seen[baseName] = 0;
                    result.Add(column);
                }
            }
            
            return result;
        }

        internal static string NormalizeColumn(string column)
        {
            if (string.Equals("id", column, StringComparison.InvariantCultureIgnoreCase))
                return "id";
            if (column.EndsWith("id", StringComparison.InvariantCultureIgnoreCase))
            {
                var tableName = column.Substring(0, column.Length - 2).TrimEnd('_', '-');
                return DbModel.Pluralizer.Singularize(tableName);
            }
            return column;
        }
    }
}
