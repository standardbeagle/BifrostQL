using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// How a media connector serves its <c>chat-media-column</c>, derived from the
    /// column's database type — never declared explicitly, so the mode cannot fall
    /// out of sync with the schema.
    /// </summary>
    public enum ChatMediaMode
    {
        /// <summary>A binary-typed column: the connector serves the raw bytes.</summary>
        Binary,

        /// <summary>A string-typed column: the connector serves the value as a URL.</summary>
        Url,
    }

    /// <summary>
    /// Parsed per-table chat-connector configuration. Built from the
    /// <c>chat-connector</c> table metadata and its media/plan/tool-description
    /// mapping keys (see <see cref="MetadataKeys.ChatConnector"/>). A table with no
    /// connector keys returns <see cref="None"/>. Throws
    /// <see cref="InvalidOperationException"/> on an unknown type token, a
    /// present-but-empty value, a media/plan mapping key without its type token, or a
    /// missing required mapping — a silently ignored connector key would expose the
    /// wrong Claude tool (or none) with no error.
    /// Column mappings are canonicalized to the column's database casing through
    /// <c>ColumnLookup</c>; an entry naming no column is kept verbatim (with a null
    /// <see cref="MediaMode"/>) for <c>ModelConfigValidator</c> to report.
    /// </summary>
    public sealed class ChatConnectorConfig
    {
        /// <summary>The not-a-connector sentinel returned for tables that do not opt in.</summary>
        public static readonly ChatConnectorConfig None = new(
            explore: false, media: false, plan: false,
            mediaColumn: null, mediaMode: null, visionEnabled: false, mediaCaptionColumn: null,
            planOperations: Array.Empty<MutationType>(), toolDescription: null);

        // Mapping keys valid only alongside the media type token.
        private static readonly string[] MediaMappingKeys =
        {
            MetadataKeys.ChatConnector.MediaColumn,
            MetadataKeys.ChatConnector.MediaVision,
            MetadataKeys.ChatConnector.MediaCaption,
        };

        /// <summary>
        /// Database types accepted for a binary-mode <c>chat-media-column</c> across
        /// the supported dialects. String-typed columns (URL mode) reuse
        /// <see cref="ChatConfig.StringColumnTypes"/> — one set of names for the mode
        /// derivation here and the validator's type check, so they cannot drift.
        /// </summary>
        public static readonly IReadOnlySet<string> BinaryColumnTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "binary", "varbinary", "image",
                "blob", "tinyblob", "mediumblob", "longblob",
                "bytea",
            };

        private readonly IReadOnlySet<MutationType> _planOperations;

        private ChatConnectorConfig(
            bool explore,
            bool media,
            bool plan,
            string? mediaColumn,
            ChatMediaMode? mediaMode,
            bool visionEnabled,
            string? mediaCaptionColumn,
            IEnumerable<MutationType> planOperations,
            string? toolDescription)
        {
            Explore = explore;
            Media = media;
            Plan = plan;
            MediaColumn = mediaColumn;
            MediaMode = mediaMode;
            VisionEnabled = visionEnabled;
            MediaCaptionColumn = mediaCaptionColumn;
            PlanOperations = planOperations.ToList();
            _planOperations = new HashSet<MutationType>(PlanOperations);
            ToolDescription = toolDescription;
        }

        /// <summary>Whether the table opts into any connector type.</summary>
        public bool IsConnector => Explore || Media || Plan;

        /// <summary>Whether the table is exposed as an explore (read/query) tool.</summary>
        public bool Explore { get; }

        /// <summary>Whether the table serves a media (image/file) column.</summary>
        public bool Media { get; }

        /// <summary>Whether the table accepts gated writes from an approved plan.</summary>
        public bool Plan { get; }

        /// <summary>The media column (media connectors only), canonicalized to DB casing.</summary>
        public string? MediaColumn { get; }

        /// <summary>
        /// The serving mode derived from <see cref="MediaColumn"/>'s type: binary column
        /// = <see cref="ChatMediaMode.Binary"/>, string column = <see cref="ChatMediaMode.Url"/>.
        /// Null when the column does not resolve or has neither type — ModelConfigValidator
        /// reports both.
        /// </summary>
        public ChatMediaMode? MediaMode { get; }

        /// <summary>Whether the media content is sent to the model as vision input.</summary>
        public bool VisionEnabled { get; }

        /// <summary>Optional caption column (media connectors only), canonicalized to DB casing.</summary>
        public string? MediaCaptionColumn { get; }

        /// <summary>
        /// The write operations a plan connector may perform, in declaration order.
        /// Empty on non-plan connectors. Delete is present ONLY when listed explicitly.
        /// </summary>
        public IReadOnlyList<MutationType> PlanOperations { get; }

        /// <summary>Optional free-text feeding the generated Claude tool description.</summary>
        public string? ToolDescription { get; }

        /// <summary>Whether a plan connector may perform <paramref name="operation"/>.</summary>
        public bool AllowsPlanOperation(MutationType operation) => _planOperations.Contains(operation);

        /// <summary>Parses the connector config for a single table (see the class summary for the fail-fast rules).</summary>
        public static ChatConnectorConfig FromTable(IDbTable table)
        {
            if (table is null)
                throw new ArgumentNullException(nameof(table));

            if (!table.Metadata.ContainsKey(MetadataKeys.ChatConnector.Marker))
            {
                // No opt-in: a stray mapping key configures nothing — the author
                // believes the table is a connector and it is not.
                foreach (var key in MediaMappingKeys
                             .Append(MetadataKeys.ChatConnector.PlanOperations)
                             .Append(MetadataKeys.ChatConnector.ToolDescription))
                {
                    if (table.Metadata.ContainsKey(key))
                        throw new InvalidOperationException(
                            $"'{key}' is set without '{MetadataKeys.ChatConnector.Marker}'; the table is not " +
                            "a chat connector, so this key has no effect.");
                }

                return None;
            }

            var types = ParseTypes(table);
            var explore = types.Contains(MetadataKeys.ChatConnector.TypeExplore);
            var media = types.Contains(MetadataKeys.ChatConnector.TypeMedia);
            var plan = types.Contains(MetadataKeys.ChatConnector.TypePlan);

            var (mediaColumn, mediaMode, visionEnabled, captionColumn) = ParseMedia(table, media);
            var planOperations = ParsePlan(table, plan);
            var toolDescription = ParseToolDescription(table);

            return new ChatConnectorConfig(
                explore, media, plan,
                mediaColumn, mediaMode, visionEnabled, captionColumn,
                planOperations, toolDescription);
        }

        /// <summary>
        /// Collects every connector table in the model, in table order. Per-table parse
        /// errors (<see cref="FromTable"/>) propagate; there is no cross-table
        /// constraint — any number of tables may be connectors.
        /// </summary>
        public static IReadOnlyList<ChatConnectorBinding> FromModel(IDbModel model)
        {
            if (model is null)
                throw new ArgumentNullException(nameof(model));

            var connectors = new List<ChatConnectorBinding>();
            foreach (var table in model.Tables)
            {
                var config = FromTable(table);
                if (config.IsConnector)
                    connectors.Add(new ChatConnectorBinding(table, config));
            }
            return connectors;
        }

        private static IReadOnlySet<string> ParseTypes(IDbTable table)
        {
            var raw = table.GetMetadataValue(MetadataKeys.ChatConnector.Marker);
            var tokens = SplitList(raw);
            if (tokens.Count == 0)
                throw new InvalidOperationException(
                    $"'{MetadataKeys.ChatConnector.Marker}' is set but names no connector type. " +
                    $"Provide a comma-separated subset of {KnownTypeList()}.");

            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens)
            {
                if (!MetadataKeys.ChatConnector.KnownTypes.Contains(token))
                    throw new InvalidOperationException(
                        $"Unknown connector type '{token}' in '{MetadataKeys.ChatConnector.Marker}'. " +
                        $"Valid values: {KnownTypeList()}.");
                types.Add(token);
            }
            return types;
        }

        private static (string? Column, ChatMediaMode? Mode, bool Vision, string? Caption) ParseMedia(
            IDbTable table, bool media)
        {
            if (!media)
            {
                foreach (var key in MediaMappingKeys)
                {
                    if (table.Metadata.ContainsKey(key))
                        throw new InvalidOperationException(
                            $"'{key}' is only valid when '{MetadataKeys.ChatConnector.Marker}' includes the " +
                            $"'{MetadataKeys.ChatConnector.TypeMedia}' token; this connector serves no media.");
                }
                return (null, null, false, null);
            }

            var columnName = table.GetMetadataValue(MetadataKeys.ChatConnector.MediaColumn);
            if (string.IsNullOrWhiteSpace(columnName))
                throw new InvalidOperationException(
                    $"a '{MetadataKeys.ChatConnector.TypeMedia}' connector requires " +
                    $"'{MetadataKeys.ChatConnector.MediaColumn}' naming the image/file column it serves.");

            var (column, mode) = ResolveMediaColumn(table, columnName);
            var vision = ParseVisionFlag(table);
            var caption = OptionalColumnValue(table, MetadataKeys.ChatConnector.MediaCaption);
            return (column, mode, vision, caption);
        }

        // Canonicalizes the media column and derives its serving mode from the column
        // type. A name that resolves to no column — or to a column of neither family —
        // is kept verbatim with a null mode for ModelConfigValidator to report.
        private static (string Column, ChatMediaMode? Mode) ResolveMediaColumn(IDbTable table, string name)
        {
            var trimmed = name.Trim();
            if (!table.ColumnLookup.TryGetValue(trimmed, out var column))
                return (trimmed, null);

            var type = StringNormalizer.NormalizeType(column.DataType);
            ChatMediaMode? mode =
                BinaryColumnTypes.Contains(type) ? ChatMediaMode.Binary
                : ChatConfig.StringColumnTypes.Contains(type) ? ChatMediaMode.Url
                : null;
            return (column.ColumnName, mode);
        }

        private static bool ParseVisionFlag(IDbTable table)
        {
            if (!table.Metadata.ContainsKey(MetadataKeys.ChatConnector.MediaVision))
                return false;

            var raw = table.GetMetadataValue(MetadataKeys.ChatConnector.MediaVision);
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.ChatConnector.MediaVision}' is set but empty. Set it to " +
                    $"'{MetadataKeys.Chat.Enabled}' to send media to the model as vision input.");

            var token = raw.Trim();
            if (!string.Equals(token, MetadataKeys.Chat.Enabled, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Unknown token '{token}' in '{MetadataKeys.ChatConnector.MediaVision}'. " +
                    $"The only valid value is '{MetadataKeys.Chat.Enabled}'.");

            return true;
        }

        private static IReadOnlyList<MutationType> ParsePlan(IDbTable table, bool plan)
        {
            if (!plan)
            {
                if (table.Metadata.ContainsKey(MetadataKeys.ChatConnector.PlanOperations))
                    throw new InvalidOperationException(
                        $"'{MetadataKeys.ChatConnector.PlanOperations}' is only valid when " +
                        $"'{MetadataKeys.ChatConnector.Marker}' includes the " +
                        $"'{MetadataKeys.ChatConnector.TypePlan}' token; this connector accepts no writes.");
                return Array.Empty<MutationType>();
            }

            var raw = table.GetMetadataValue(MetadataKeys.ChatConnector.PlanOperations);
            var tokens = SplitList(raw);

            // A plan connector allowing no operation gates nothing: the author opted
            // into writes and got a tool that can never write. Same fail-open shape
            // the history 'names no valid operation' guard rejects.
            if (tokens.Count == 0)
                throw new InvalidOperationException(
                    $"a '{MetadataKeys.ChatConnector.TypePlan}' connector requires " +
                    $"'{MetadataKeys.ChatConnector.PlanOperations}' naming at least one of " +
                    $"{OperationNameList()}. Note that '{nameof(MutationType.Delete).ToLowerInvariant()}' " +
                    "is never implied; it must be listed explicitly.");

            var operations = new List<MutationType>();
            var seen = new HashSet<MutationType>();
            foreach (var token in tokens)
            {
                if (!Enum.TryParse<MutationType>(token, ignoreCase: true, out var operation))
                    throw new InvalidOperationException(
                        $"Unknown operation '{token}' in '{MetadataKeys.ChatConnector.PlanOperations}'. " +
                        $"Valid values: {OperationNameList()}.");

                if (seen.Add(operation))
                    operations.Add(operation);
            }
            return operations;
        }

        private static string? ParseToolDescription(IDbTable table)
        {
            if (!table.Metadata.ContainsKey(MetadataKeys.ChatConnector.ToolDescription))
                return null;

            var raw = table.GetMetadataValue(MetadataKeys.ChatConnector.ToolDescription);
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.ChatConnector.ToolDescription}' is set but empty; a blank tool " +
                    "description is worse than the schema-derived default. Remove the key or describe the tool.");

            return raw.Trim();
        }

        // Reads an optional column-mapping key: null when absent, error when blank,
        // canonicalized to the column's database casing when present.
        private static string? OptionalColumnValue(IDbTable table, string key)
        {
            if (!table.Metadata.ContainsKey(key))
                return null;

            var raw = table.GetMetadataValue(key);
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException(
                    $"'{key}' is set but names no column; remove the key or name a column.");

            var trimmed = raw.Trim();
            return table.ColumnLookup.TryGetValue(trimmed, out var column) ? column.ColumnName : trimmed;
        }

        private static string KnownTypeList() =>
            string.Join(", ", new[]
            {
                MetadataKeys.ChatConnector.TypeExplore,
                MetadataKeys.ChatConnector.TypeMedia,
                MetadataKeys.ChatConnector.TypePlan,
            });

        private static string OperationNameList() =>
            string.Join(", ", Enum.GetNames<MutationType>().Select(n => n.ToLowerInvariant()));

        private static List<string> SplitList(string? raw) =>
            (raw ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
    }

    /// <summary>A connector table paired with its parsed config (see <see cref="ChatConnectorConfig.FromModel"/>).</summary>
    public sealed class ChatConnectorBinding
    {
        internal ChatConnectorBinding(IDbTable table, ChatConnectorConfig config)
        {
            Table = table;
            Config = config;
        }

        /// <summary>The connector table.</summary>
        public IDbTable Table { get; }

        /// <summary>The table's parsed connector config.</summary>
        public ChatConnectorConfig Config { get; }
    }
}
