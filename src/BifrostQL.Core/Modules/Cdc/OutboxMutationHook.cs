using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.Cdc
{
    /// <summary>
    /// The transactional-outbox writer. Runs as an <see cref="IInTransactionMutationHook"/>
    /// — immediately AFTER the data write but INSIDE the same transaction — so its
    /// INSERT into the outbox table commits or rolls back atomically with the data
    /// change (exactly-once, no dual-write) AND can name the database-generated
    /// identity returned by an INSERT. A background dispatcher (a later CDC sub-task)
    /// drains the outbox to webhooks/queues.
    ///
    /// The hook is registered unconditionally and no-ops for every table that does
    /// not opt in via <c>emit-events</c>, so it costs one metadata read per mutation
    /// on non-CDC tables. When it DOES write, a failure to insert the event throws,
    /// which rolls the whole mutation back — correct: if the event cannot be
    /// recorded, the data change it describes must not commit.
    /// </summary>
    public sealed class OutboxMutationHook : IInTransactionMutationHook
    {
        public async ValueTask AfterWriteInTransactionAsync(MutationObserverContext context)
        {
            var config = CdcEventConfig.FromTable(context.Table);
            if (!config.EmitsEvents || !config.Emits(context.MutationType))
                return; // Table does not emit events for this operation.

            // An UPDATE/DELETE that affected zero rows changed nothing (an out-of-scope
            // tenant/policy no-op, or a predicate that matched nothing). Emitting an
            // event for it would fabricate a change that never happened. INSERT's result
            // is the generated identity, not a row count, so it is never treated as zero.
            if ((context.MutationType == MutationType.Update || context.MutationType == MutationType.Delete)
                && AffectedZeroRows(context.Result))
                return;

            // The in-transaction phase always supplies the connection, model and dialect.
            // Their absence means this hook was invoked outside a mutation transaction —
            // a wiring bug — so fail closed rather than silently dropping the event.
            // Transaction MAY be null: the TreeSync path manages its transaction with
            // SQL BEGIN/COMMIT keywords rather than a DbTransaction object, and the write
            // still runs on that same connection-level transaction.
            if (context.Connection is null || context.Model is null || context.Dialect is null)
                throw new BifrostExecutionError(
                    "CDC outbox writer was invoked without an open transaction; the event could not be written.");

            var outboxName = context.Model.GetMetadataValue(MetadataKeys.Cdc.OutboxTable);
            if (string.IsNullOrWhiteSpace(outboxName))
                throw new BifrostExecutionError(
                    $"Table '{context.Table.TableSchema}.{context.Table.DbName}' emits events but no " +
                    $"model-level '{MetadataKeys.Cdc.OutboxTable}' is configured.");

            var outbox = ModelTableReference.Find(context.Model, outboxName);
            if (outbox is null)
                throw new BifrostExecutionError(
                    $"Configured outbox table '{outboxName}' was not found in the model.");

            var payload = BuildPayload(context.Table, context.MutationType, config.PayloadMode, context.Data, context.Result);

            // Fail closed rather than emit an event that cannot identify its row. Every
            // payload mode must carry the full primary key (client-supplied on
            // update/delete, sourced from the generated identity on insert). A missing
            // or null key column means we could not capture the key — abort the whole
            // mutation instead of writing a keyless event a consumer cannot act on.
            var missingKeys = context.Table.KeyColumns
                .Where(k => !payload.TryGetValue(k.ColumnName, out var v) || v is null)
                .Select(k => k.ColumnName)
                .ToArray();
            if (missingKeys.Length > 0)
                throw new BifrostExecutionError(
                    $"CDC event for '{context.Table.TableSchema}.{context.Table.DbName}' is missing key " +
                    $"column value(s): {string.Join(", ", missingKeys)}. Refusing to emit a keyless event.");

            var eventRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["aggregate"] = $"{context.Table.TableSchema}.{context.Table.DbName}",
                ["op"] = context.MutationType.ToString().ToLowerInvariant(),
                ["payload"] = JsonSerializer.Serialize(payload),
                ["tenant"] = ResolveTenant(context.Model, context.UserContext),
                ["created_at"] = DateTime.UtcNow,
                ["attempts"] = 0,
                ["dead"] = false,
                // id is an identity column and dispatched_at starts NULL, so both are
                // omitted from the INSERT column list.
            };

            var tableRef = context.Dialect.TableReference(outbox.TableSchema, outbox.DbName);
            var sql = MutationCommandExecutor.BuildInsertInto(context.Dialect, outbox, tableRef, eventRow.Keys) + ";";

            await MutationCommandExecutor.ExecuteNonQuery(
                context.Connection, context.Transaction, sql, eventRow);
        }

        // The write result of an UPDATE/DELETE is the affected-row count. Treat a
        // numeric zero as "nothing changed". A non-numeric or null result is treated
        // as non-zero (do not silently suppress an event we cannot classify).
        private static bool AffectedZeroRows(object? result)
            => result is not null
               && (result is int i ? i == 0
                   : result is long l ? l == 0
                   : result is IConvertible c && Convert.ToInt64(c) == 0);

        /// <summary>
        /// Selects the columns captured into the event payload per the configured mode.
        /// The mutation data is keyed by DB column name. For UPDATE/DELETE the client
        /// supplies the primary key, so it is already in the data; for an INSERT with a
        /// database-generated key, the key is not in the data and is taken from
        /// <paramref name="result"/> (the identity the insert returned) so every event
        /// can identify its row.
        /// <list type="bullet">
        ///   <item><c>keys</c> — primary-key columns only.</item>
        ///   <item><c>changed</c> — the columns this mutation writes (its data set),
        ///     plus the key. This is the documented changed-field set.</item>
        ///   <item><c>full</c> — the same written columns plus the key; a complete
        ///     post-image re-read of DB-defaulted columns is a later refinement (only
        ///     the write inputs are available at this seam — a full row for INSERT, the
        ///     changed columns for UPDATE).</item>
        /// </list>
        /// </summary>
        private static IReadOnlyDictionary<string, object?> BuildPayload(
            IDbTable table, MutationType mutationType, CdcPayloadMode mode,
            IDictionary<string, object?> data, object? result)
        {
            var keyColumns = table.KeyColumns.ToList();
            var keyNames = new HashSet<string>(
                keyColumns.Select(k => k.ColumnName), StringComparer.OrdinalIgnoreCase);

            // Start from the written columns, then ensure a single generated INSERT key
            // is present (the client did not supply it; it comes from the identity result).
            var enriched = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
            if (mutationType == MutationType.Insert && keyColumns.Count == 1 && result is not null)
            {
                var keyName = keyColumns[0].ColumnName;
                if (!enriched.TryGetValue(keyName, out var existing) || existing is null)
                    enriched[keyName] = result;
            }

            if (mode == CdcPayloadMode.Keys)
                return enriched
                    .Where(kv => keyNames.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            // full and changed both serialize the written columns plus the key.
            return enriched;
        }

        private static string? ResolveTenant(IDbModel model, IDictionary<string, object?> userContext)
        {
            var key = model.GetMetadataValue(MetadataKeys.Security.TenantContextKey);
            if (string.IsNullOrWhiteSpace(key))
                key = MetadataKeys.Auth.DefaultTenantContextKey;

            return userContext.TryGetValue(key, out var value) ? value?.ToString() : null;
        }
    }
}
