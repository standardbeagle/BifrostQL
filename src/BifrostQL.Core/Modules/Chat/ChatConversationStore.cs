using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// A page request for conversation/message reads: a positive row limit and a
    /// non-negative row offset, validated fail-fast at construction.
    /// </summary>
    public readonly record struct ChatPage
    {
        public ChatPage(int limit, int offset = 0)
        {
            if (limit < 1)
                throw new ArgumentOutOfRangeException(nameof(limit), limit, "A chat page limit must be at least 1.");
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "A chat page offset cannot be negative.");
            Limit = limit;
            Offset = offset;
        }

        public int Limit { get; }
        public int Offset { get; }
    }

    /// <summary>
    /// One page of store rows (column name → value, as the intent executor projects
    /// them) plus the unpaged total under the caller's row scope.
    /// </summary>
    public sealed class ChatPageResult
    {
        public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }
        public required int TotalCount { get; init; }
    }

    /// <summary>
    /// Persistence layer for the chat module's conversation/message pair
    /// (<see cref="ChatConfig.FromModel"/>). Every read goes through
    /// <see cref="IQueryIntentExecutor"/> and every write through
    /// <see cref="IMutationIntentExecutor"/>, so the full transformer pipelines —
    /// tenant isolation, policy, soft delete, crypto, audit, history hooks — apply by
    /// construction; this class deliberately has no SQL and no way to skip them.
    /// Conversation scoping therefore comes from the transformers (tenant-filter and
    /// friends), never from hand-rolled predicates: an unauthorized caller sees zero
    /// rows or the transformer's own typed denial, and appending to a conversation the
    /// caller cannot see fails closed as not-found.
    /// </summary>
    public sealed class ChatConversationStore
    {
        private readonly IQueryIntentExecutor _reads;
        private readonly IMutationIntentExecutor _writes;
        private readonly string? _endpoint;

        public ChatConversationStore(IQueryIntentExecutor reads, IMutationIntentExecutor writes, string? endpoint = null)
        {
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _writes = writes ?? throw new ArgumentNullException(nameof(writes));
            _endpoint = endpoint;
        }

        /// <summary>
        /// Test seam: runs between the append's visibility probe and its insert so a
        /// test can delete the conversation inside the probe-to-insert window and pin
        /// the race handling deterministically. Never set in production.
        /// </summary>
        internal Func<Task>? AfterConversationVisibilityProbe { get; set; }

        /// <summary>
        /// Inserts a conversation for the caller and returns its generated id. A title
        /// requires a configured <c>chat-title</c> column — supplying one without it is
        /// a config/caller mismatch rejected fail-fast, not a silently dropped value.
        /// </summary>
        public async Task<object?> CreateConversationAsync(
            IDictionary<string, object?> authContext, string? title = null, CancellationToken cancellationToken = default)
        {
            if (authContext is null) throw new ArgumentNullException(nameof(authContext));
            var chat = await ResolveChatAsync();

            var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (title != null)
            {
                var titleColumn = chat.ConversationsConfig.TitleColumn
                    ?? throw new InvalidOperationException(
                        $"A conversation title was supplied but '{QualifiedName(chat.ConversationsTable)}' configures no " +
                        $"'{MetadataKeys.Chat.Title}' column; map one or create the conversation without a title.");
                data[titleColumn] = title;
            }

            var result = await _writes.ExecuteAsync(new MutationIntent
            {
                Table = chat.ConversationsTable.DbName,
                Action = MutationIntentAction.Insert,
                Data = data,
                UserContext = authContext,
                Endpoint = _endpoint,
            }, cancellationToken);
            return result.Value;
        }

        /// <summary>
        /// Appends a message to a conversation the caller can see and returns the
        /// message id. The conversation is probed through the read executor first, so
        /// row-scope transformers decide visibility: a conversation outside the
        /// caller's scope — another tenant's or nonexistent alike — fails closed as
        /// not-found. A conversation deleted in the probe-to-insert window gets the
        /// SAME typed not-found on every dialect: an FK-enforcing engine's constraint
        /// violation is mapped after a re-probe confirms the conversation is gone, and
        /// engines/links without a database-level FK are covered by a post-insert
        /// re-probe that removes the just-written orphan before reporting. The
        /// created-at column is stamped server-side (UTC now, matching how audit
        /// columns are stamped); the role must be one of
        /// <see cref="ChatMessageRoles.All"/>.
        /// </summary>
        public async Task<object?> AppendMessageAsync(
            IDictionary<string, object?> authContext, object conversationId, string role, string content,
            CancellationToken cancellationToken = default)
        {
            if (authContext is null) throw new ArgumentNullException(nameof(authContext));
            if (conversationId is null) throw new ArgumentNullException(nameof(conversationId));
            if (content is null) throw new ArgumentNullException(nameof(content));
            var canonicalRole = ChatMessageRoles.Normalize(role);

            var chat = await ResolveChatAsync();
            if (!await IsConversationVisibleAsync(chat, authContext, conversationId, cancellationToken))
                throw ConversationNotFound(conversationId);

            if (AfterConversationVisibilityProbe != null)
                await AfterConversationVisibilityProbe();

            var config = chat.MessagesConfig;
            object? messageId;
            try
            {
                var result = await _writes.ExecuteAsync(new MutationIntent
                {
                    Table = chat.MessagesTable.DbName,
                    Action = MutationIntentAction.Insert,
                    Data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        [config.RoleColumn!] = canonicalRole,
                        [config.ContentColumn!] = content,
                        [config.ConversationFkColumn!] = conversationId,
                        // Server-side stamp, same source of truth as the audit
                        // transformer's timestamp columns; the store's API carries no
                        // caller-supplied timestamp to overwrite.
                        [config.CreatedAtColumn!] = DateTime.UtcNow,
                    },
                    UserContext = authContext,
                    Endpoint = _endpoint,
                }, cancellationToken);
                messageId = result.Value;
            }
            catch (Exception ex) when (FindDbException(ex) is { } dbEx)
            {
                // An FK-enforcing dialect surfaces a probe-to-insert conversation
                // delete as a constraint violation. Re-probe: gone means the write
                // raced a delete and the caller gets the typed not-found a
                // never-existed conversation gets; still visible means a genuine
                // database error that must not be masked as not-found.
                if (await IsConversationVisibleAsync(chat, authContext, conversationId, cancellationToken))
                    throw;
                throw ConversationNotFound(conversationId, dbEx);
            }

            // Engines/links without a database-level FK (SQLite before the factory's
            // Foreign Keys default, metadata-only 'join' rules) accept the insert even
            // when the conversation vanished mid-window, silently orphaning the
            // message. Re-probe and compensate: remove the orphan, report not-found.
            // A delete landing AFTER this check is indistinguishable from a delete
            // after a successful append — that bounded remainder belongs to the
            // conversation-delete path, not to this write.
            if (!await IsConversationVisibleAsync(chat, authContext, conversationId, cancellationToken))
            {
                await DeleteOrphanedMessageAsync(chat, authContext, messageId, conversationId, cancellationToken);
                throw ConversationNotFound(conversationId);
            }

            return messageId;
        }

        /// <summary>
        /// Pages the caller's conversations newest first: primary key descending,
        /// which IS creation order because ModelConfigValidator rejects chat tables
        /// whose primary key is not a monotonic integer identity
        /// (<see cref="ChatConfig.IntegerKeyColumnTypes"/>).
        /// </summary>
        public async Task<ChatPageResult> ListConversationsAsync(
            IDictionary<string, object?> authContext, ChatPage page, CancellationToken cancellationToken = default)
        {
            if (authContext is null) throw new ArgumentNullException(nameof(authContext));
            var chat = await ResolveChatAsync();

            var query = SelectAllColumnsQuery(chat.ConversationsTable, page);
            query.Sort.AddRange(chat.ConversationsTable.KeyColumns.Select(c => $"{c.GraphQlName}_desc"));

            return await ExecutePageAsync(query, authContext, cancellationToken);
        }

        /// <summary>
        /// Pages one conversation's messages chronologically with a deterministic
        /// primary-key tiebreak for same-timestamp rows (created-at asc, then each key
        /// column asc).
        /// </summary>
        public async Task<ChatPageResult> PageMessagesAsync(
            IDictionary<string, object?> authContext, object conversationId, ChatPage page,
            CancellationToken cancellationToken = default)
        {
            if (authContext is null) throw new ArgumentNullException(nameof(authContext));
            if (conversationId is null) throw new ArgumentNullException(nameof(conversationId));
            var chat = await ResolveChatAsync();
            var config = chat.MessagesConfig;

            var query = SelectAllColumnsQuery(chat.MessagesTable, page);
            query.Filter = TableFilterFactory.Equals(
                chat.MessagesTable.DbName, config.ConversationFkColumn!, conversationId);
            query.Sort.Add($"{GraphQlColumnName(chat.MessagesTable, config.CreatedAtColumn!)}_asc");
            query.Sort.AddRange(chat.MessagesTable.KeyColumns.Select(c => $"{c.GraphQlName}_asc"));

            return await ExecutePageAsync(query, authContext, cancellationToken);
        }

        /// <summary>
        /// Resolves the endpoint's chat pair from the cached model. No chat tables is
        /// a configuration error for a chat store — fail fast, never an empty surface.
        /// </summary>
        private async Task<ChatModelConfig> ResolveChatAsync()
        {
            var model = await _reads.GetModelAsync(_endpoint);
            return ChatConfig.FromModel(model)
                ?? throw new InvalidOperationException(
                    $"No chat tables are configured on this endpoint; set '{MetadataKeys.Chat.Conversations}' and " +
                    $"'{MetadataKeys.Chat.Messages}' metadata (see ChatConfig).");
        }

        /// <summary>
        /// Probes the conversation through the read executor so the row-scope
        /// transformers decide what the caller can see; zero rows — nonexistent and
        /// out-of-scope alike — reads as not visible, fail-closed.
        /// </summary>
        private async Task<bool> IsConversationVisibleAsync(
            ChatModelConfig chat, IDictionary<string, object?> authContext, object conversationId,
            CancellationToken cancellationToken)
        {
            var conversations = chat.ConversationsTable;
            var key = SingleKeyColumn(conversations, "conversations");

            var probe = NewQuery(conversations);
            probe.ScalarColumns.Add(new GqlObjectColumn(key.DbName, key.GraphQlName));
            probe.Filter = TableFilterFactory.Equals(conversations.DbName, key.ColumnName, conversationId);
            probe.Limit = 1;

            var visible = await _reads.ExecuteAsync(new QueryIntent
            {
                Query = probe,
                UserContext = authContext,
                Endpoint = _endpoint,
            }, cancellationToken);

            return visible.Rows.Count > 0;
        }

        /// <summary>
        /// Removes a message written for a conversation that vanished in the
        /// probe-to-insert window. The delete rides the same mutation pipeline as
        /// every store write (tenant-scoped, hook-observed); if it cannot address or
        /// remove the row, the orphan is reported loudly rather than left silent.
        /// </summary>
        private async Task DeleteOrphanedMessageAsync(
            ChatModelConfig chat, IDictionary<string, object?> authContext, object? messageId, object conversationId,
            CancellationToken cancellationToken)
        {
            if (messageId is null)
                throw new BifrostExecutionError(
                    $"Conversation '{conversationId}' was deleted while a message was being appended, and the " +
                    "insert returned no message id to clean up — a message row may be orphaned.");

            try
            {
                // Assert the validated single-key contract the positional PrimaryKey relies on.
                SingleKeyColumn(chat.MessagesTable, "messages");
                await _writes.ExecuteAsync(new MutationIntent
                {
                    Table = chat.MessagesTable.DbName,
                    Action = MutationIntentAction.Delete,
                    Data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
                    PrimaryKey = new[] { messageId },
                    UserContext = authContext,
                    Endpoint = _endpoint,
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new BifrostExecutionError(
                    $"Conversation '{conversationId}' was deleted while message '{messageId}' was being appended, " +
                    "and removing the orphaned message failed.", ex);
            }
        }

        private static BifrostExecutionError ConversationNotFound(object conversationId, Exception? inner = null) =>
            inner is null
                ? new BifrostExecutionError($"Conversation '{conversationId}' was not found.")
                : new BifrostExecutionError($"Conversation '{conversationId}' was not found.", inner);

        // The mutation pipeline wraps provider errors, so walk the chain for the
        // DbException that identifies a database-level failure (e.g. FK violation).
        private static DbException? FindDbException(Exception? ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is DbException dbException)
                    return dbException;
            }
            return null;
        }

        private static ColumnDto SingleKeyColumn(IDbTable table, string chatRole)
        {
            var keys = table.KeyColumns.ToArray();
            if (keys.Length != 1)
                throw new InvalidOperationException(
                    $"'{QualifiedName(table)}' must have a single-column primary key to be a chat " +
                    $"{chatRole} table (enforced by ModelConfigValidator).");
            return keys[0];
        }

        private async Task<ChatPageResult> ExecutePageAsync(
            GqlObjectQuery query, IDictionary<string, object?> authContext, CancellationToken cancellationToken)
        {
            var result = await _reads.ExecuteAsync(new QueryIntent
            {
                Query = query,
                UserContext = authContext,
                Endpoint = _endpoint,
            }, cancellationToken);

            return new ChatPageResult
            {
                Rows = result.Rows,
                TotalCount = result.TotalCount
                    ?? throw new BifrostExecutionError(
                        "The paged chat read returned no total count despite IncludeResult being set."),
            };
        }

        private static GqlObjectQuery SelectAllColumnsQuery(IDbTable table, ChatPage page)
        {
            var query = NewQuery(table);
            foreach (var column in table.Columns)
                query.ScalarColumns.Add(new GqlObjectColumn(column.DbName, column.GraphQlName));
            query.Limit = page.Limit;
            query.Offset = page.Offset;
            query.IncludeResult = true;
            return query;
        }

        private static GqlObjectQuery NewQuery(IDbTable table) => new()
        {
            DbTable = table,
            SchemaName = table.TableSchema,
            TableName = table.DbName,
            GraphQlName = table.GraphQlName,
            Path = table.GraphQlName,
        };

        // Sort tokens carry GraphQL column names (see GqlObjectQuery.RenderSortColumns);
        // the chat config stores canonical DB names, so map before building the token.
        private static string GraphQlColumnName(IDbTable table, string dbColumnName) =>
            table.ColumnLookup.TryGetValue(dbColumnName, out var column)
                ? column.GraphQlName
                : throw new InvalidOperationException(
                    $"Column '{dbColumnName}' does not exist on '{QualifiedName(table)}' " +
                    "(the chat metadata no longer matches the loaded model).");

        private static string QualifiedName(IDbTable table) => $"{table.TableSchema}.{table.DbName}";
    }
}
