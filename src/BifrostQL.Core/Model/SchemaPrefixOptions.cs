namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Format for schema prefix applied to GraphQL type names.
    /// </summary>
    public enum SchemaPrefixFormat
    {
        /// <summary>
        /// Prefix with underscore separator: sales_orders
        /// </summary>
        Underscore,

        /// <summary>
        /// Prefix with camelCase joining: salesOrders
        /// </summary>
        CamelCase,
    }

    /// <summary>
    /// Configuration for applying database schema prefixes to GraphQL type names.
    /// When enabled, tables in non-default schemas get their GraphQL names prefixed
    /// with the schema name for disambiguation. Tables in the default schema remain
    /// unprefixed for backward compatibility.
    /// </summary>
    public sealed class SchemaPrefixOptions
    {
        /// <summary>
        /// Whether schema prefixing is enabled. Default is false for backward compatibility.
        /// </summary>
        public bool Enabled { get; init; }

        /// <summary>
        /// The default schema whose tables will not be prefixed. Default is "dbo".
        /// </summary>
        public string DefaultSchema { get; init; } = "dbo";

        /// <summary>
        /// The format used for the schema prefix. Default is Underscore.
        /// </summary>
        public SchemaPrefixFormat PrefixFormat { get; init; } = SchemaPrefixFormat.Underscore;

        /// <summary>
        /// Returns the disabled (default) options instance.
        /// </summary>
        public static SchemaPrefixOptions Disabled { get; } = new SchemaPrefixOptions { Enabled = false };

        /// <summary>
        /// Applies the schema prefix to a GraphQL name based on the table schema.
        /// Returns the original name if prefixing is disabled or the table is in the default schema.
        /// </summary>
        public string ApplyPrefix(string graphQlName, string tableSchema)
        {
            if (!Enabled)
                return graphQlName;

            if (string.Equals(tableSchema, DefaultSchema, StringComparison.OrdinalIgnoreCase))
                return graphQlName;

            var schemaPrefix = tableSchema.ToGraphQl();

            return PrefixFormat switch
            {
                SchemaPrefixFormat.Underscore => $"{schemaPrefix}_{graphQlName}",
                SchemaPrefixFormat.CamelCase => $"{schemaPrefix}{char.ToUpper(graphQlName[0])}{graphQlName.Substring(1)}",
                _ => $"{schemaPrefix}_{graphQlName}",
            };
        }

        /// <summary>
        /// Creates SchemaPrefixOptions from model metadata dictionary.
        /// Reads: schema-prefix (enabled/disabled), schema-prefix-default, schema-prefix-format.
        /// </summary>
        public static SchemaPrefixOptions FromMetadata(IDictionary<string, object?> metadata)
        {
            var enabled = metadata.TryGetValue("schema-prefix", out var enabledVal)
                && string.Equals(enabledVal?.ToString(), "enabled", StringComparison.OrdinalIgnoreCase);

            if (!enabled)
                return Disabled;

            var defaultSchema = metadata.TryGetValue("schema-prefix-default", out var defaultVal)
                && !string.IsNullOrWhiteSpace(defaultVal?.ToString())
                    ? defaultVal!.ToString()!
                    : "dbo";

            var format = SchemaPrefixFormat.Underscore;
            if (metadata.TryGetValue("schema-prefix-format", out var formatVal)
                && string.Equals(formatVal?.ToString(), "camelcase", StringComparison.OrdinalIgnoreCase))
            {
                format = SchemaPrefixFormat.CamelCase;
            }

            return new SchemaPrefixOptions
            {
                Enabled = true,
                DefaultSchema = defaultSchema,
                PrefixFormat = format,
            };
        }
    }
}
