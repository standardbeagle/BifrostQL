namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Controls how database schemas are represented in the GraphQL API.
    /// </summary>
    public enum SchemaDisplayMode
    {
        /// <summary>
        /// All tables appear as direct fields on the root query type. Default behavior.
        /// </summary>
        Flat,

        /// <summary>
        /// Tables in non-default schemas get their GraphQL names prefixed with the schema name.
        /// Existing behavior controlled by SchemaPrefixOptions.
        /// </summary>
        Prefix,

        /// <summary>
        /// Each database schema becomes a top-level field containing its tables.
        /// Example: query { sales { orders { ... } } hr { employees { ... } } }
        /// </summary>
        Field,
    }

    /// <summary>
    /// Configuration for the schema field display mode and schema-level access control.
    /// When Mode is Field, tables are grouped under schema-level query types.
    /// </summary>
    public sealed class SchemaFieldConfig
    {
        /// <summary>
        /// How schemas are displayed in the GraphQL API. Default is Flat.
        /// </summary>
        public SchemaDisplayMode Mode { get; init; } = SchemaDisplayMode.Flat;

        /// <summary>
        /// The default schema whose tables appear directly on the root query type
        /// when Mode is Field. Tables in the default schema are not nested under a
        /// schema field. Default is "dbo".
        /// </summary>
        public string DefaultSchema { get; init; } = "dbo";

        /// <summary>
        /// Schemas to exclude from the GraphQL API entirely.
        /// </summary>
        public IReadOnlyList<string> ExcludedSchemas { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Returns true if the schema is in the excluded list.
        /// </summary>
        public bool IsSchemaExcluded(string schemaName)
        {
            for (var i = 0; i < ExcludedSchemas.Count; i++)
            {
                if (string.Equals(ExcludedSchemas[i], schemaName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Groups tables by their database schema. Tables in the default schema
        /// are grouped under the empty string key.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<IDbTable>> GroupTablesBySchema(
            IReadOnlyCollection<IDbTable> tables)
        {
            var result = new Dictionary<string, List<IDbTable>>(StringComparer.OrdinalIgnoreCase);

            foreach (var table in tables)
            {
                var schema = table.TableSchema;
                if (IsSchemaExcluded(schema))
                    continue;

                var key = IsDefaultSchema(schema) ? "" : schema;
                if (!result.TryGetValue(key, out var list))
                {
                    list = new List<IDbTable>();
                    result[key] = list;
                }
                list.Add(table);
            }

            return result.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<IDbTable>)kvp.Value,
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Generates the GraphQL type name for a schema's query type.
        /// Example: "sales" becomes "salesSchemaQuery".
        /// </summary>
        public static string GetSchemaQueryTypeName(string schemaName)
        {
            var graphQlSchema = schemaName.ToGraphQl();
            return $"{graphQlSchema}SchemaQuery";
        }

        /// <summary>
        /// Generates the GraphQL type name for a schema's mutation type.
        /// Example: "sales" becomes "salesSchemaInput".
        /// </summary>
        public static string GetSchemaMutationTypeName(string schemaName)
        {
            var graphQlSchema = schemaName.ToGraphQl();
            return $"{graphQlSchema}SchemaInput";
        }

        /// <summary>
        /// Returns the default disabled config (Flat mode).
        /// </summary>
        public static SchemaFieldConfig Disabled { get; } = new SchemaFieldConfig();

        /// <summary>
        /// Creates SchemaFieldConfig from model metadata.
        /// Reads: schema-display (flat/prefix/field), schema-default, schema-excluded.
        /// </summary>
        public static SchemaFieldConfig FromMetadata(IDictionary<string, object?> metadata)
        {
            var modeStr = metadata.TryGetValue(MetadataKeys.Schema.Display, out var modeVal)
                ? modeVal?.ToString()
                : null;

            var mode = ParseMode(modeStr);
            if (mode == SchemaDisplayMode.Flat)
                return Disabled;

            var defaultSchema = metadata.TryGetValue(MetadataKeys.Schema.Default, out var defaultVal)
                && !string.IsNullOrWhiteSpace(defaultVal?.ToString())
                    ? defaultVal!.ToString()!
                    : "dbo";

            var excluded = ParseStringList(
                metadata.TryGetValue(MetadataKeys.Schema.Excluded, out var excVal)
                    ? excVal?.ToString()
                    : null);

            return new SchemaFieldConfig
            {
                Mode = mode,
                DefaultSchema = defaultSchema,
                ExcludedSchemas = excluded,
            };
        }

        private bool IsDefaultSchema(string schemaName)
        {
            return string.Equals(schemaName, DefaultSchema, StringComparison.OrdinalIgnoreCase);
        }

        private static SchemaDisplayMode ParseMode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return SchemaDisplayMode.Flat;

            if (string.Equals(value, "prefix", StringComparison.OrdinalIgnoreCase))
                return SchemaDisplayMode.Prefix;

            if (string.Equals(value, "field", StringComparison.OrdinalIgnoreCase))
                return SchemaDisplayMode.Field;

            return SchemaDisplayMode.Flat;
        }

        private static IReadOnlyList<string> ParseStringList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Array.Empty<string>();

            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

    }
}
