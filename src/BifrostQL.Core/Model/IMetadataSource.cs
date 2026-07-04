namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Provides table-level metadata from an external source (config file, database table, etc.)
    /// Each source returns a dictionary keyed by qualified table name (e.g., "dbo.Users")
    /// containing that table's metadata key-value pairs.
    /// </summary>
    public interface IMetadataSource
    {
        /// <summary>
        /// Loads table metadata from this source.
        /// Keys are qualified table names (e.g., "dbo.Users" or ":root" for database-level metadata).
        /// Values are the metadata dictionaries for each table.
        /// </summary>
        Task<IDictionary<string, IDictionary<string, object?>>> LoadTableMetadataAsync();

        /// <summary>
        /// Priority of this source. Higher values override lower values during merge.
        /// File-based sources should use low values (e.g., 0), database sources higher (e.g., 100).
        /// </summary>
        int Priority { get; }
    }

    /// <summary>
    /// Merges metadata from multiple sources in priority order.
    /// Sources with higher priority override values from lower-priority sources.
    /// </summary>
    public sealed class CompositeMetadataSource : IMetadataSource
    {
        private readonly IReadOnlyList<IMetadataSource> _sources;

        public int Priority => _sources.Count > 0 ? _sources.Max(s => s.Priority) : 0;

        public CompositeMetadataSource(IReadOnlyList<IMetadataSource> sources)
        {
            ArgumentNullException.ThrowIfNull(sources, nameof(sources));
            _sources = sources.OrderBy(s => s.Priority).ToList();
        }

        public async Task<IDictionary<string, IDictionary<string, object?>>> LoadTableMetadataAsync()
        {
            var merged = new Dictionary<string, IDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in _sources)
            {
                var sourceData = await source.LoadTableMetadataAsync();
                MergeInto(merged, sourceData);
            }

            return merged;
        }

        internal static void MergeInto(
            IDictionary<string, IDictionary<string, object?>> target,
            IDictionary<string, IDictionary<string, object?>> source)
        {
            foreach (var (tableName, sourceMetadata) in source)
            {
                if (!target.TryGetValue(tableName, out var targetMetadata))
                {
                    targetMetadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    target[tableName] = targetMetadata;
                }

                foreach (var (key, value) in sourceMetadata)
                {
                    targetMetadata[key] = value;
                }
            }
        }
    }

    /// <summary>
    /// Validates metadata dictionaries against known keys and expected value types.
    /// </summary>
    public static class MetadataValidator
    {
        private static readonly HashSet<string> KnownTableKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            MetadataKeys.Security.TenantFilter,
            MetadataKeys.Security.AutoFilter,
            MetadataKeys.SoftDelete.Column,
            MetadataKeys.SoftDelete.DeletedBy,
            MetadataKeys.SoftDelete.DeleteType,
            MetadataKeys.Ui.Visibility,
            MetadataKeys.Relationships.ManyToMany,
            MetadataKeys.Ui.Label,
            MetadataKeys.FileStorage.Folder,
            MetadataKeys.Relationships.Join,
            MetadataKeys.Batch.MaxSize,
            MetadataKeys.DataType.Type,
            MetadataKeys.StateMachine.StateColumn,
            MetadataKeys.StateMachine.InitialState,
            MetadataKeys.StateMachine.States,
            MetadataKeys.StateMachine.Transitions,
            MetadataKeys.Computed.Sql,
            MetadataKeys.Computed.Provider,
            MetadataKeys.Validation.Server,
            MetadataKeys.Validation.Plugin,
        };

        private static readonly HashSet<string> KnownColumnKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            MetadataKeys.AutoPopulate.Marker,
            MetadataKeys.DataType.Type,
            MetadataKeys.Relationships.Join,
            MetadataKeys.Validation.Min,
            MetadataKeys.Validation.Max,
            MetadataKeys.Validation.Step,
            MetadataKeys.Validation.MinLength,
            MetadataKeys.Validation.MaxLength,
            MetadataKeys.Validation.Pattern,
            MetadataKeys.Validation.PatternMessage,
            MetadataKeys.Validation.InputType,
            MetadataKeys.Validation.Required,
            MetadataKeys.Validation.Server,
            MetadataKeys.Validation.Plugin,
        };

        private static readonly HashSet<string> KnownDatabaseKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            MetadataKeys.Audit.Table,
            MetadataKeys.Audit.LegacyUserKey,
            MetadataKeys.Audit.UserKey,
            MetadataKeys.Relationships.AutoJoin,
            MetadataKeys.Model.DePluralize,
            MetadataKeys.Relationships.ForeignJoins,
            MetadataKeys.Model.DefaultLimit,
            MetadataKeys.Relationships.DynamicJoins,
            MetadataKeys.RawSql.Enabled,
            MetadataKeys.Schema.Prefix,
            MetadataKeys.Schema.PrefixDefault,
            MetadataKeys.Schema.PrefixFormat,
            MetadataKeys.Schema.Display,
            MetadataKeys.Schema.Default,
            MetadataKeys.Schema.Excluded,
            MetadataKeys.Schema.Permissions,
            MetadataKeys.StoredProcedures.Include,
            MetadataKeys.StoredProcedures.Exclude,
            MetadataKeys.AppSchema.PrefixGroups,
            MetadataKeys.Security.TenantContextKey,
            MetadataKeys.Security.AutoFilterBypassRole,
            MetadataKeys.AppSchema.AutoDetect,
            MetadataKeys.AppSchema.App,
            MetadataKeys.AppSchema.Detected,
        };

        /// <summary>
        /// Validates a metadata dictionary and returns any warnings about unknown keys.
        /// </summary>
        public static IReadOnlyList<string> ValidateTableMetadata(string tableName, IDictionary<string, object?> metadata)
        {
            var warnings = new List<string>();
            foreach (var key in metadata.Keys)
            {
                if (!KnownTableKeys.Contains(key))
                    warnings.Add($"Unknown table metadata key '{key}' on table '{tableName}'");
            }
            return warnings;
        }

        /// <summary>
        /// Validates column metadata and returns any warnings about unknown keys.
        /// </summary>
        public static IReadOnlyList<string> ValidateColumnMetadata(string tableName, string columnName, IDictionary<string, object?> metadata)
        {
            var warnings = new List<string>();
            foreach (var key in metadata.Keys)
            {
                if (!KnownColumnKeys.Contains(key))
                    warnings.Add($"Unknown column metadata key '{key}' on column '{tableName}.{columnName}'");
            }
            return warnings;
        }

        /// <summary>
        /// Validates database-level metadata and returns any warnings about unknown keys.
        /// </summary>
        public static IReadOnlyList<string> ValidateDatabaseMetadata(IDictionary<string, object?> metadata)
        {
            var warnings = new List<string>();
            foreach (var key in metadata.Keys)
            {
                if (!KnownDatabaseKeys.Contains(key))
                    warnings.Add($"Unknown database metadata key '{key}'");
            }
            return warnings;
        }
    }
}
