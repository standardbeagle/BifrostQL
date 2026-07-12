using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// What a table's chat metadata declares it to be.
    /// </summary>
    public enum ChatTableKind
    {
        /// <summary>Not a chat table.</summary>
        None,

        /// <summary>The conversations table (<c>chat-conversations: enabled</c>).</summary>
        Conversations,

        /// <summary>The messages table (<c>chat-messages: enabled</c> + column mappings).</summary>
        Messages,
    }

    /// <summary>
    /// Parsed per-table chat configuration. Built from the <c>chat-*</c> table
    /// metadata (see <see cref="MetadataKeys.Chat"/>). A table with no chat keys
    /// returns <see cref="None"/>. Throws <see cref="InvalidOperationException"/> on
    /// an unknown opt-in token, a present-but-empty value, a mapping key on the wrong
    /// table, or an incomplete messages mapping — a silently ignored chat key would
    /// leave the author believing a chat schema exists when it does not.
    /// Column mappings are canonicalized to the column's database casing through
    /// <c>ColumnLookup</c>; an entry naming no column is kept verbatim for
    /// <c>ModelConfigValidator</c> to report.
    /// </summary>
    public sealed class ChatConfig
    {
        /// <summary>The not-a-chat-table sentinel returned for tables that do not opt in.</summary>
        public static readonly ChatConfig None = new(ChatTableKind.None, null, null, null, null, null);

        // The column mappings the messages table must carry, all four together.
        private static readonly string[] MessageMappingKeys =
        {
            MetadataKeys.Chat.Role,
            MetadataKeys.Chat.Content,
            MetadataKeys.Chat.ConversationFk,
            MetadataKeys.Chat.CreatedAt,
        };

        /// <summary>
        /// Database types accepted for the string-typed chat columns
        /// (<c>chat-role</c>, <c>chat-content</c>) across the supported dialects.
        /// </summary>
        public static readonly IReadOnlySet<string> StringColumnTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "varchar", "nvarchar", "char", "nchar", "text", "ntext",
                "tinytext", "mediumtext", "longtext", "clob", "citext",
                "character", "character varying",
            };

        /// <summary>
        /// Database types accepted for the chat tables' primary-key columns across
        /// the supported dialects. The chat surface ORDERS by these keys —
        /// conversations list newest first by key descending, and message paging
        /// breaks created-at ties by key ascending — so only monotonic integer keys
        /// qualify; a GUID key would order randomly and silently break the contract.
        /// </summary>
        public static readonly IReadOnlySet<string> IntegerKeyColumnTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "int", "integer", "bigint", "smallint", "tinyint", "mediumint",
                "int2", "int4", "int8",
                "serial", "bigserial", "smallserial",
            };

        /// <summary>
        /// Database types accepted for the date/time-typed <c>chat-created-at</c>
        /// column across the supported dialects.
        /// </summary>
        public static readonly IReadOnlySet<string> DateTimeColumnTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "datetime", "datetime2", "smalldatetime", "datetimeoffset", "date",
                "timestamp", "timestamptz",
                "timestamp with time zone", "timestamp without time zone",
            };

        private ChatConfig(
            ChatTableKind kind,
            string? titleColumn,
            string? roleColumn,
            string? contentColumn,
            string? conversationFkColumn,
            string? createdAtColumn)
        {
            Kind = kind;
            TitleColumn = titleColumn;
            RoleColumn = roleColumn;
            ContentColumn = contentColumn;
            ConversationFkColumn = conversationFkColumn;
            CreatedAtColumn = createdAtColumn;
        }

        /// <summary>Which chat table this table is declared to be.</summary>
        public ChatTableKind Kind { get; }

        /// <summary>Optional conversation title column (conversations table only).</summary>
        public string? TitleColumn { get; }

        /// <summary>Message role column (messages table only).</summary>
        public string? RoleColumn { get; }

        /// <summary>Message content column (messages table only).</summary>
        public string? ContentColumn { get; }

        /// <summary>Column referencing the conversations table's PK (messages table only).</summary>
        public string? ConversationFkColumn { get; }

        /// <summary>Message timestamp column (messages table only).</summary>
        public string? CreatedAtColumn { get; }

        /// <summary>Parses the chat config for a single table (see the class summary for the fail-fast rules).</summary>
        public static ChatConfig FromTable(IDbTable table)
        {
            if (table is null)
                throw new ArgumentNullException(nameof(table));

            var isConversations = table.Metadata.ContainsKey(MetadataKeys.Chat.Conversations);
            var isMessages = table.Metadata.ContainsKey(MetadataKeys.Chat.Messages);

            if (isConversations && isMessages)
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Chat.Conversations}' and '{MetadataKeys.Chat.Messages}' are both set; " +
                    "one table cannot be both sides of the chat pair.");

            if (isConversations)
                return ParseConversations(table);

            if (isMessages)
                return ParseMessages(table);

            // No opt-in: a stray mapping key configures nothing — the author believes
            // the table is a chat table and it is not.
            RejectStrayKey(table, MetadataKeys.Chat.Title, MetadataKeys.Chat.Conversations);
            foreach (var key in MessageMappingKeys)
                RejectStrayKey(table, key, MetadataKeys.Chat.Messages);

            return None;
        }

        /// <summary>
        /// Resolves the model's chat pair, or null when no table opts in. Fail-fast:
        /// exactly one conversations table paired with exactly one messages table is
        /// allowed per model — multiples and a half-configured pair are rejected, and
        /// per-table parse errors (<see cref="FromTable"/>) propagate.
        /// </summary>
        public static ChatModelConfig? FromModel(IDbModel model)
        {
            if (model is null)
                throw new ArgumentNullException(nameof(model));

            var conversations = new List<(IDbTable Table, ChatConfig Config)>();
            var messages = new List<(IDbTable Table, ChatConfig Config)>();

            foreach (var table in model.Tables)
            {
                var config = FromTable(table);
                switch (config.Kind)
                {
                    case ChatTableKind.Conversations:
                        conversations.Add((table, config));
                        break;
                    case ChatTableKind.Messages:
                        messages.Add((table, config));
                        break;
                }
            }

            if (conversations.Count == 0 && messages.Count == 0)
                return null;

            if (conversations.Count > 1)
                throw new InvalidOperationException(
                    $"Multiple tables set '{MetadataKeys.Chat.Conversations}': " +
                    $"{JoinNames(conversations)}. Exactly one conversations table is allowed per model.");

            if (messages.Count > 1)
                throw new InvalidOperationException(
                    $"Multiple tables set '{MetadataKeys.Chat.Messages}': " +
                    $"{JoinNames(messages)}. Exactly one messages table is allowed per model.");

            if (messages.Count == 0)
                throw new InvalidOperationException(
                    $"'{QualifiedName(conversations[0].Table)}' sets '{MetadataKeys.Chat.Conversations}' but no table sets " +
                    $"'{MetadataKeys.Chat.Messages}'; the chat module requires exactly one conversations table " +
                    "paired with exactly one messages table.");

            if (conversations.Count == 0)
                throw new InvalidOperationException(
                    $"'{QualifiedName(messages[0].Table)}' sets '{MetadataKeys.Chat.Messages}' but no table sets " +
                    $"'{MetadataKeys.Chat.Conversations}'; the chat module requires exactly one conversations table " +
                    "paired with exactly one messages table.");

            return new ChatModelConfig(
                conversations[0].Table, conversations[0].Config,
                messages[0].Table, messages[0].Config);
        }

        private static ChatConfig ParseConversations(IDbTable table)
        {
            RequireEnabledToken(table, MetadataKeys.Chat.Conversations);

            foreach (var key in MessageMappingKeys)
            {
                if (table.Metadata.ContainsKey(key))
                    throw new InvalidOperationException(
                        $"'{key}' is only valid on the messages table ('{MetadataKeys.Chat.Messages}'); " +
                        $"this table sets '{MetadataKeys.Chat.Conversations}'.");
            }

            var title = OptionalColumnValue(table, MetadataKeys.Chat.Title);
            return new ChatConfig(ChatTableKind.Conversations, title, null, null, null, null);
        }

        private static ChatConfig ParseMessages(IDbTable table)
        {
            RequireEnabledToken(table, MetadataKeys.Chat.Messages);

            if (table.Metadata.ContainsKey(MetadataKeys.Chat.Title))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Chat.Title}' is only valid on the conversations table " +
                    $"('{MetadataKeys.Chat.Conversations}'); this table sets '{MetadataKeys.Chat.Messages}'.");

            // Report every missing/blank mapping at once so the author fixes the
            // config in one pass — a partial mapping is the EAV partial-config shape.
            var missing = MessageMappingKeys
                .Where(key => string.IsNullOrWhiteSpace(table.GetMetadataValue(key)))
                .ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    $"incomplete '{MetadataKeys.Chat.Messages}' mapping; missing {string.Join(", ", missing)} " +
                    $"(all of {string.Join("/", MessageMappingKeys)} are required together).");

            return new ChatConfig(
                ChatTableKind.Messages,
                titleColumn: null,
                roleColumn: Canonicalize(table, table.GetMetadataValue(MetadataKeys.Chat.Role)!),
                contentColumn: Canonicalize(table, table.GetMetadataValue(MetadataKeys.Chat.Content)!),
                conversationFkColumn: Canonicalize(table, table.GetMetadataValue(MetadataKeys.Chat.ConversationFk)!),
                createdAtColumn: Canonicalize(table, table.GetMetadataValue(MetadataKeys.Chat.CreatedAt)!));
        }

        private static void RequireEnabledToken(IDbTable table, string key)
        {
            var raw = table.GetMetadataValue(key);
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException(
                    $"'{key}' is set but empty. Set it to '{MetadataKeys.Chat.Enabled}' to opt the table " +
                    "into the chat module.");

            var token = raw.Trim();
            if (!string.Equals(token, MetadataKeys.Chat.Enabled, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Unknown token '{token}' in '{key}'. The only valid value is '{MetadataKeys.Chat.Enabled}'.");
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

            return Canonicalize(table, raw);
        }

        private static void RejectStrayKey(IDbTable table, string key, string requiredOptIn)
        {
            if (table.Metadata.ContainsKey(key))
                throw new InvalidOperationException(
                    $"'{key}' is set without '{requiredOptIn}'; the table is not a chat table, " +
                    "so this key has no effect.");
        }

        // Canonicalizes a mapped column name to the column's database casing so
        // downstream consumers emit matching SQL identifiers (quoted verbatim on
        // Postgres). A name that resolves to no column is kept verbatim for
        // ModelConfigValidator to report.
        private static string Canonicalize(IDbTable table, string name)
        {
            var trimmed = name.Trim();
            return table.ColumnLookup.TryGetValue(trimmed, out var column) ? column.ColumnName : trimmed;
        }

        private static string QualifiedName(IDbTable table) => $"{table.TableSchema}.{table.DbName}";

        private static string JoinNames(IEnumerable<(IDbTable Table, ChatConfig Config)> tables) =>
            string.Join(", ", tables.Select(t => QualifiedName(t.Table)).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// The model's resolved chat pair: exactly one conversations table and exactly
    /// one messages table (see <see cref="ChatConfig.FromModel"/>).
    /// </summary>
    public sealed class ChatModelConfig
    {
        internal ChatModelConfig(
            IDbTable conversationsTable,
            ChatConfig conversationsConfig,
            IDbTable messagesTable,
            ChatConfig messagesConfig)
        {
            ConversationsTable = conversationsTable;
            ConversationsConfig = conversationsConfig;
            MessagesTable = messagesTable;
            MessagesConfig = messagesConfig;
        }

        /// <summary>The conversations table.</summary>
        public IDbTable ConversationsTable { get; }

        /// <summary>The conversations table's parsed config.</summary>
        public ChatConfig ConversationsConfig { get; }

        /// <summary>The messages table.</summary>
        public IDbTable MessagesTable { get; }

        /// <summary>The messages table's parsed config.</summary>
        public ChatConfig MessagesConfig { get; }
    }
}
