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
    /// Permission rule restricting access to a database schema by role.
    /// </summary>
    public sealed class SchemaPermission
    {
        /// <summary>
        /// The database schema this permission applies to.
        /// </summary>
        public string SchemaName { get; init; } = null!;

        /// <summary>
        /// Roles allowed to access this schema. Empty means all roles have access.
        /// </summary>
        public IReadOnlyList<string> AllowedRoles { get; init; } = Array.Empty<string>();
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
        /// Per-schema permission rules. Schemas not listed are accessible by all roles.
        /// </summary>
        public IReadOnlyList<SchemaPermission> Permissions { get; init; } = Array.Empty<SchemaPermission>();

        /// <summary>
        /// Returns true if the given schema is accessible by any of the provided roles.
        /// A schema is allowed if it is not excluded and either has no permission rule
        /// or one of the provided roles matches the allowed roles.
        /// </summary>
        public bool IsSchemaAllowed(string schemaName, IReadOnlyList<string>? roles)
        {
            if (IsSchemaExcluded(schemaName))
                return false;

            var permission = FindPermission(schemaName);
            if (permission == null || permission.AllowedRoles.Count == 0)
                return true;

            if (roles == null || roles.Count == 0)
                return false;

            for (var i = 0; i < roles.Count; i++)
            {
                for (var j = 0; j < permission.AllowedRoles.Count; j++)
                {
                    if (string.Equals(roles[i], permission.AllowedRoles[j], StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

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
        /// Reads: schema-display (flat/prefix/field), schema-default, schema-excluded, schema-permissions.
        /// </summary>
        public static SchemaFieldConfig FromMetadata(IDictionary<string, object?> metadata)
        {
            var modeStr = metadata.TryGetValue("schema-display", out var modeVal)
                ? modeVal?.ToString()
                : null;

            var mode = ParseMode(modeStr);
            if (mode == SchemaDisplayMode.Flat)
                return Disabled;

            var defaultSchema = metadata.TryGetValue("schema-default", out var defaultVal)
                && !string.IsNullOrWhiteSpace(defaultVal?.ToString())
                    ? defaultVal!.ToString()!
                    : "dbo";

            var excluded = ParseStringList(
                metadata.TryGetValue("schema-excluded", out var excVal)
                    ? excVal?.ToString()
                    : null);

            var permissions = ParsePermissions(
                metadata.TryGetValue("schema-permissions", out var permVal)
                    ? permVal?.ToString()
                    : null);

            return new SchemaFieldConfig
            {
                Mode = mode,
                DefaultSchema = defaultSchema,
                ExcludedSchemas = excluded,
                Permissions = permissions,
            };
        }

        private bool IsDefaultSchema(string schemaName)
        {
            return string.Equals(schemaName, DefaultSchema, StringComparison.OrdinalIgnoreCase);
        }

        private SchemaPermission? FindPermission(string schemaName)
        {
            for (var i = 0; i < Permissions.Count; i++)
            {
                if (string.Equals(Permissions[i].SchemaName, schemaName, StringComparison.OrdinalIgnoreCase))
                    return Permissions[i];
            }
            return null;
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

        /// <summary>
        /// Parses permissions from a semicolon-delimited string.
        /// Format: "schema1:role1,role2;schema2:role3"
        /// </summary>
        private static IReadOnlyList<SchemaPermission> ParsePermissions(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Array.Empty<SchemaPermission>();

            var entries = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = new List<SchemaPermission>(entries.Length);

            foreach (var entry in entries)
            {
                var parts = entry.Split(':', 2, StringSplitOptions.TrimEntries);
                if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0]))
                    continue;

                var roles = parts.Length > 1
                    ? ParseStringList(parts[1])
                    : Array.Empty<string>();

                result.Add(new SchemaPermission
                {
                    SchemaName = parts[0],
                    AllowedRoles = roles,
                });
            }

            return result;
        }
    }
}
