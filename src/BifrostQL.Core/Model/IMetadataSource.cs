using BifrostQL.Core.Schema;

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
        // Internal (not private): ModelConfigValidator cross-checks these against
        // the exact-case keys actually present in a table's Metadata dictionary.
        // That dictionary uses the framework-default (case-SENSITIVE) comparer —
        // see DbTable.Metadata / DbModelLoader — while this allow-list check is
        // OrdinalIgnoreCase, so a case-typo'd key (e.g. "Soft-Delete" instead of
        // "soft-delete") previously produced no "unknown key" warning yet was
        // never found by the case-sensitive transformer lookups that read
        // Metadata directly, silently disabling the feature. ModelConfigValidator
        // uses this set to fail fast on that mismatch instead.
        internal static readonly HashSet<string> KnownTableKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            MetadataKeys.Security.TenantFilter,
            MetadataKeys.Security.AutoFilter,
            MetadataKeys.SoftDelete.Column,
            MetadataKeys.SoftDelete.DeletedBy,
            MetadataKeys.SoftDelete.DeleteType,
            MetadataKeys.SoftDelete.HardDeleteRole,
            // Optimistic-concurrency version token (integrity — a miscased key must be
            // flagged, not silently disable lost-update protection).
            MetadataKeys.Concurrency.Token,
            MetadataKeys.Ui.Visibility,
            MetadataKeys.Ui.Hidden,
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
            // Server-side authorization policy (security — miscased keys must be
            // flagged, not silently fail-open).
            MetadataKeys.Policy.Actions,
            MetadataKeys.Policy.ReadDeny,
            MetadataKeys.Policy.ReadDenyRoles,
            MetadataKeys.Policy.WriteDeny,
            MetadataKeys.Policy.RowScope,
            MetadataKeys.Policy.RowScopeRoles,
            // EAV configuration (table-level).
            MetadataKeys.Eav.Parent,
            MetadataKeys.Eav.ForeignKey,
            MetadataKeys.Eav.Key,
            MetadataKeys.Eav.Value,
            // Storage bucket config blob.
            MetadataKeys.Storage.Config,
            // Enum configuration.
            EnumTableConfig.MetadataKey,  // "enum": marks a lookup table as an enum source
            MetadataKeys.Enum.Values,
            MetadataKeys.Enum.Labels,
            MetadataKeys.Enum.Ref,
            // Polymorphic child-table declarations.
            MetadataKeys.Relationships.PolymorphicTypeCol,
            MetadataKeys.Relationships.PolymorphicIdCol,
            MetadataKeys.Relationships.PolymorphicMap,
            // Change Data Capture / outbound events (table-level opt-in). A miscased
            // key must be flagged, not silently drop the table from event emission.
            MetadataKeys.Cdc.EmitEvents,
            MetadataKeys.Cdc.EventSink,
            MetadataKeys.Cdc.EventPayload,
            // Temporal change history (table-level opt-in). A miscased key must be
            // flagged, not silently leave the table with no change trail.
            MetadataKeys.History.Enabled,
            MetadataKeys.History.Table,
            MetadataKeys.History.Columns,
            // Chat module (table-level opt-in + column mappings). A miscased or
            // typo'd key must be flagged, not silently leave the chat surface
            // half-configured.
            MetadataKeys.Chat.Conversations,
            MetadataKeys.Chat.Title,
            MetadataKeys.Chat.Messages,
            MetadataKeys.Chat.Role,
            MetadataKeys.Chat.Content,
            MetadataKeys.Chat.ConversationFk,
            MetadataKeys.Chat.CreatedAt,
        };

        // Internal for the same reason as KnownTableKeys above (case-casing
        // cross-check against ColumnDto.Metadata, which is also case-sensitive).
        internal static readonly HashSet<string> KnownColumnKeys = new(StringComparer.OrdinalIgnoreCase)
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
            // File-column tags and inline storage config.
            MetadataKeys.FileStorage.File,
            MetadataKeys.FileStorage.Storage,
            MetadataKeys.FileStorage.MaxSize,
            MetadataKeys.FileStorage.ContentTypeColumn,
            MetadataKeys.FileStorage.FileNameColumn,
            MetadataKeys.FileStorage.Accept,
            MetadataKeys.Storage.Config,
            // Enum configuration (column-level).
            MetadataKeys.Enum.Values,
            MetadataKeys.Enum.Labels,
            MetadataKeys.Enum.Ref,
            // Display / presentation hints.
            MetadataKeys.Ui.Label,
            MetadataKeys.Ui.Hidden,
            MetadataKeys.Ui.ReadOnly,
            MetadataKeys.Ui.DisplayFormat,
            MetadataKeys.DataType.Format,
            MetadataKeys.DataType.Default,
            MetadataKeys.DataType.Title,
            MetadataKeys.DataType.PhpSerialized,
            // Field-level encryption / masking (column-level). Security keys — a
            // miscased key must be flagged, not silently leave a column unencrypted.
            MetadataKeys.Crypto.Encrypt,
            MetadataKeys.Crypto.KeyRef,
            MetadataKeys.Crypto.Mask,
            MetadataKeys.Crypto.UnmaskRole,
            MetadataKeys.Crypto.BlindIndex,
        };

        // Internal for the same reason as KnownTableKeys above (case-casing
        // cross-check against the model-level Metadata dictionary).
        internal static readonly HashSet<string> KnownDatabaseKeys = new(StringComparer.OrdinalIgnoreCase)
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
            MetadataKeys.RawSql.Role,
            MetadataKeys.RawSql.Timeout,
            MetadataKeys.RawSql.MaxRows,
            MetadataKeys.Schema.Prefix,
            MetadataKeys.Schema.PrefixDefault,
            MetadataKeys.Schema.PrefixFormat,
            MetadataKeys.Schema.Display,
            MetadataKeys.Schema.Default,
            MetadataKeys.Schema.Excluded,
            MetadataKeys.Schema.Permissions,
            MetadataKeys.StoredProcedures.Include,
            MetadataKeys.StoredProcedures.Exclude,
            MetadataKeys.StoredProcedures.Role,
            MetadataKeys.AppSchema.PrefixGroups,
            MetadataKeys.Security.TenantContextKey,
            MetadataKeys.Security.AutoFilterBypassRole,
            MetadataKeys.AppSchema.AutoDetect,
            MetadataKeys.AppSchema.App,
            MetadataKeys.AppSchema.Detected,
            MetadataKeys.AppSchema.DetectionConfidence,
            // Generic-table (_table) query surface configuration (database-level).
            GenericTableConfig.MetadataKey,
            GenericTableConfig.RoleMetadataKey,
            GenericTableConfig.MaxRowsMetadataKey,
            GenericTableConfig.AllowedTablesMetadataKey,
            GenericTableConfig.DeniedTablesMetadataKey,
            // Change Data Capture / outbound events (model-level). outbox-table names
            // the transactional outbox; webhook-secret signs drained deliveries.
            MetadataKeys.Cdc.OutboxTable,
            MetadataKeys.Cdc.WebhookSecret,
            // Temporal change history (model-level). Shared default history table for
            // every history-enabled table that does not override it.
            MetadataKeys.History.Table,
        };

        /// <summary>
        /// Prefix marking a metadata key as an intentional consumer/extension key.
        /// BifrostQL never interprets these — they are an escape hatch for host- or
        /// consumer-specific metadata — so they pass the model-load unknown-key gate
        /// (<see cref="ModelConfigValidator"/>) untouched. Every key without this prefix
        /// that is not in a built-in allow-list is treated as a typo and rejected, closing
        /// the class of bug where a mistyped key (e.g. "soft-delte") silently no-ops.
        /// </summary>
        public const string ConsumerExtensionPrefix = "x-";

        /// <summary>
        /// Whether <paramref name="key"/> is a consumer extension key (see
        /// <see cref="ConsumerExtensionPrefix"/>), matched case-insensitively.
        /// </summary>
        public static bool IsConsumerExtensionKey(string key)
            => !string.IsNullOrEmpty(key) && key.StartsWith(ConsumerExtensionPrefix, StringComparison.OrdinalIgnoreCase);

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
