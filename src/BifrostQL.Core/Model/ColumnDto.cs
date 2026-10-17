using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Model
{
    public sealed class ColumnDto : ISchemaNames
    {
        public string TableCatalog { get; init; } = null!;
        public string TableSchema { get; init; } = null!;
        public string TableName { get; init; } = null!;
        public string ColumnName { get; init; } = null!;
        public string DbName => ColumnName;
        // Explicit backing fields only for the two members a copy may replace, so
        // CopyWith can clone every other member structurally (MemberwiseClone)
        // instead of hand-listing properties that drift when one is added.
        private string _graphQlName = null!;
        private IDictionary<string, object?> _metadata = new Dictionary<string, object?>();

        public string GraphQlName { get => _graphQlName; init => _graphQlName = value; }
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
        public string EffectiveDataType => GetMetadataValue(MetadataKeys.DataType.Type) ?? DataType;

        /// <summary>
        /// Declared maximum length in characters (character types) or bytes (binary
        /// types), from INFORMATION_SCHEMA CHARACTER_MAXIMUM_LENGTH or the SQLite
        /// declared type. Null for unbounded (MAX/text/blob) and non-length types.
        /// </summary>
        public int? CharacterMaxLength { get; init; }

        /// <summary>
        /// Declared precision for exact numeric types (decimal/numeric), from
        /// INFORMATION_SCHEMA NUMERIC_PRECISION or the SQLite declared type. Null
        /// for non-numeric columns. Deliberately not captured for approximate or
        /// integer types, whose "precision" is a storage fact, not a write bound.
        /// </summary>
        public int? NumericPrecision { get; init; }

        /// <summary>Declared scale paired with <see cref="NumericPrecision"/>.</summary>
        public int? NumericScale { get; init; }

        public bool IsNullable { get; init; }
        public int OrdinalPosition { get; init; }
        public bool IsIdentity { get; init; } = false;
        /// <summary>
        /// The database computes this column's value (SQL Server computed column,
        /// Postgres/MySQL/SQLite generated column). It is never a write input.
        /// </summary>
        public bool IsComputed { get; init; } = false;
        /// <summary>
        /// The database supplies a value when an INSERT omits this column: a
        /// declared DEFAULT, a Postgres identity, a MySQL auto-increment or
        /// DEFAULT_GENERATED, or a SQL Server rowversion. A NOT NULL column with a
        /// default is optional in the insert input type.
        /// </summary>
        public bool HasDefault { get; init; } = false;

        /// <summary>
        /// True when an INSERT must carry a client value for this column: it is NOT
        /// NULL and the database cannot fill it (no default, not identity, not
        /// computed). The one column-level requiredness rule, read by both the GraphQL
        /// insert input (<c>TableSchemaGenerator.GetMutationParameterType</c>) and the
        /// form/meta hint (<c>ValidationRules.RequiredImplied</c>) so they cannot
        /// drift. Metadata overlays (auto-populate, tenant pinning, write grants) are
        /// applied by each caller on top.
        /// </summary>
        public bool RequiresInsertValue => !IsNullable && !HasDefault && !IsIdentity && !IsComputed;
        public bool IsPrimaryKey { get; init; } = false;
        public bool IsUnique { get; init; } = false;
        public IDictionary<string, object?> Metadata { get => _metadata; init => _metadata = value; }
        public string? GetMetadataValue(string property) => Metadata.TryGetValue(property, out var v) ? v?.ToString() : null;
        public bool GetMetadataBool(string property, bool defaultValue) => Utils.MetadataSwitch.Parse(GetMetadataValue(property), defaultValue);
        public bool CompareMetadata(string property, string value)
        {
            if (!Metadata.TryGetValue(property, out var v)) return false;
            return string.Equals(v?.ToString(), value, StringComparison.InvariantCultureIgnoreCase);
        }

        public override string ToString()
        {
            return $"[{TableSchema}].[{TableName}].[{ColumnName}]({DataType} {(IsNullable ? "NULL" : "NOT NULL")}){{{(IsIdentity ? "id " : "")}{(IsPrimaryKey ? "PK " : "")}{(IsUnique ? "UQ " : "")}{(IsComputed ? "computed " : "")}{(HasDefault ? "default " : "")}}}";
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
                 DataType = ResolveDataType(reader),
                // INFORMATION_SCHEMA facts the readers already select. SQL Server
                // reports -1 for MAX types and MySQL reports lengths beyond
                // int.MaxValue for LONGTEXT/LONGBLOB; both mean "unbounded" → null.
                CharacterMaxLength = ReadBoundedInt(reader, "CHARACTER_MAXIMUM_LENGTH", minimum: 1),
                NumericPrecision = IsExactNumeric((string)reader["DATA_TYPE"])
                    ? ReadBoundedInt(reader, "NUMERIC_PRECISION", minimum: 1) : null,
                // Scale 0 is a real declaration (DECIMAL(10,0) stores whole numbers).
                NumericScale = IsExactNumeric((string)reader["DATA_TYPE"])
                    ? ReadBoundedInt(reader, "NUMERIC_SCALE", minimum: 0) : null,
                IsNullable = ((string)reader["IS_NULLABLE"]) == "YES",
                OrdinalPosition = Convert.ToInt32(reader["ORDINAL_POSITION"]),
                IsIdentity = Convert.ToInt32(reader["IS_IDENTITY"]) == 1,
                // Each reader derives these two flags in SQL from its own
                // catalog (see its columns query); both columns are required.
                IsComputed = Convert.ToInt32(reader["IS_COMPUTED"]) == 1,
                HasDefault = Convert.ToInt32(reader["HAS_DEFAULT"]) == 1,
                IsPrimaryKey = isPrimary,
                IsUnique = constraints.TryGetValue(columnRef, out var uniqueCons) && uniqueCons.Any(c => c.ConstraintType == "UNIQUE"),
            };
        }

        private static string ResolveDataType(IDataReader reader)
        {
            var dataType = (string)reader["DATA_TYPE"];
            if (!string.Equals(dataType, "ARRAY", StringComparison.OrdinalIgnoreCase)
                || !HasColumn(reader, "UDT_NAME") || reader.IsDBNull(reader.GetOrdinal("UDT_NAME")))
                return dataType;

            var udtName = reader["UDT_NAME"]?.ToString();
            if (udtName is not { Length: > 1 } || udtName[0] != '_')
                return dataType;

            return udtName[1..].ToLowerInvariant() switch
            {
                "text" => "text[]",
                "int2" => "smallint[]",
                "int4" => "integer[]",
                "int8" => "bigint[]",
                "bool" => "boolean[]",
                "float4" => "real[]",
                "float8" => "double precision[]",
                "numeric" => "numeric[]",
                "uuid" => "uuid[]",
                _ => dataType,
            };
        }

        private static bool HasColumn(IDataReader reader, string name)
        {
            for (var i = 0; i < reader.FieldCount; i++)
                if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// Exact numeric types whose declared precision/scale bound what a write may
        /// hold. Approximate (float/real/double) and integer types also report a
        /// NUMERIC_PRECISION in INFORMATION_SCHEMA, but there it describes storage,
        /// not a declarable bound, so it is deliberately not captured.
        /// </summary>
        public static bool IsExactNumeric(string dataType)
        {
            var normalized = StringNormalizer.NormalizeType(dataType);
            var paren = normalized.IndexOf('(');
            if (paren >= 0)
                normalized = normalized[..paren].TrimEnd();
            return normalized is "decimal" or "numeric" or "dec" or "money" or "smallmoney";
        }

        private static int? ReadBoundedInt(IDataReader reader, string columnName, int minimum)
        {
            var ordinal = reader.GetOrdinal(columnName);
            if (reader.IsDBNull(ordinal))
                return null;
            var raw = Convert.ToInt64(reader.GetValue(ordinal));
            // SQL Server reports -1 for MAX types; MySQL reports LONGTEXT/LONGBLOB
            // lengths beyond int.MaxValue. Both mean "no declared bound".
            if (raw < minimum || raw > int.MaxValue)
                return null;
            return (int)raw;
        }

        /// <summary>
        /// Creates a copy of this ColumnDto with a new GraphQL name.
        /// Used for deduplicating GraphQL names within a table; the copy shares
        /// this column's metadata dictionary.
        /// </summary>
        public ColumnDto WithGraphQlName(string newGraphQlName) => CopyWith(newGraphQlName, Metadata);

        /// <summary>
        /// Copy for one model build: every member is preserved and the metadata
        /// dictionary is a fresh copy, because <c>DbModel.FromTables</c> applies
        /// metadata to its input in place and each build must own its state.
        /// </summary>
        internal ColumnDto CloneForBuild() => CopyWith(GraphQlName, new Dictionary<string, object?>(Metadata));

        /// <summary>
        /// The one column copy routine. Structural, so a member added to this
        /// type is carried by every copy without a second property list to update.
        /// </summary>
        private ColumnDto CopyWith(string graphQlName, IDictionary<string, object?> metadata)
        {
            var copy = (ColumnDto)MemberwiseClone();
            copy._graphQlName = graphQlName;
            copy._metadata = metadata;
            return copy;
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
