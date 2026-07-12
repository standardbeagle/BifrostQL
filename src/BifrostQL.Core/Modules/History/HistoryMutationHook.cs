using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.History
{
    /// <summary>
    /// The temporal change-history writer. It runs in BOTH in-transaction hook phases of a
    /// mutation, because a before/after trail needs one fact from each:
    ///
    /// <list type="bullet">
    ///   <item><b>Before the write</b> (<see cref="IBeforeCommitMutationHook"/>) it reads the
    ///     current row — the only moment the pre-image still exists — and parks it in the
    ///     mutation's <see cref="MutationObserverContext.MutationState"/>.</item>
    ///   <item><b>After the write, still inside the transaction</b>
    ///     (<see cref="IInTransactionMutationHook"/>) it knows the write's result: whether a
    ///     row actually changed, and the database-generated key of an INSERT. Only here can
    ///     it decide to record — and the history row it writes commits or rolls back with
    ///     the change it describes, so the trail can never disagree with the data.</item>
    /// </list>
    ///
    /// The after-image is READ BACK rather than assembled from the write inputs, so DB
    /// defaults, triggers, and computed columns appear in the trail as they were actually
    /// stored — a history that shows what the author intended rather than what the database
    /// holds is worse than none.
    ///
    /// The hook is registered unconditionally and no-ops for every table that does not opt
    /// in via <c>history</c>, costing one metadata read per mutation elsewhere.
    /// </summary>
    public sealed class HistoryMutationHook : IBeforeCommitMutationHook, IInTransactionMutationHook
    {
        // Key under which the pre-write row is parked for this mutation's after-write phase.
        internal const string BeforeImageKey = "bifrost.history.before-image";

        // The pre-write row, or null when no row matched the key. Wrapped in a type (rather
        // than storing the row dictionary directly) so "captured, and there was no row" is
        // distinguishable from "never captured" — the latter must fail closed.
        private sealed record BeforeImage(IReadOnlyDictionary<string, object?>? Row);

        /// <summary>
        /// Pre-write phase: capture the before-image of the row an UPDATE or DELETE is about
        /// to change. An INSERT has no before-image, so it captures nothing.
        /// </summary>
        public async ValueTask<IReadOnlyList<string>> BeforeCommitAsync(MutationObserverContext context)
        {
            var config = HistoryConfig.FromTable(context.Table);
            if (!config.RecordsHistory || !MayRecord(config, context.MutationType))
                return Array.Empty<string>();

            if (context.MutationType == MutationType.Insert)
                return Array.Empty<string>(); // Nothing exists to read yet.

            if (context.Connection is null || context.Dialect is null)
                throw new BifrostExecutionError(
                    "The change-history writer was invoked without an open transaction; the " +
                    "before-image could not be captured.");

            // History is recorded per row, so the mutation must name the row: a predicate-only
            // update/delete (e.g. delete where status='archived') can match an unbounded set,
            // and the writer cannot record a trail for rows it cannot enumerate. Abort the
            // mutation with a clear message rather than commit a change with no history.
            var keyData = TryBuildKeyData(context.Table, context.Data);
            if (keyData is null)
            {
                // The upsert path always carries the full primary key, so a keyless write
                // is a plain predicate-scoped update/delete. When that operation itself is
                // not recorded (the table is only here for a possible upsert insert-flip),
                // there is nothing to capture — and nothing to veto.
                if (!config.Records(context.MutationType))
                    return Array.Empty<string>();

                return new[]
                {
                    $"Table '{Qualify(context.Table)}' records change history, so a " +
                    $"{context.MutationType.ToString().ToLowerInvariant()} must be scoped by its full primary key " +
                    $"({string.Join(", ", context.Table.KeyColumns.Select(k => k.ColumnName))}); the row's " +
                    "before-image cannot otherwise be captured.",
                };
            }

            // forUpdate: the capture read takes the dialect's update lock so a concurrent
            // transaction cannot commit a change to this row between the pre-image read and
            // the write it precedes — without it (under READ COMMITTED) the trail could
            // attribute the other writer's changes to this actor, with a 'before' that
            // disagrees with the actual pre-write state.
            var row = await MutationCommandExecutor.LoadRowByKey(
                context.Connection, context.Transaction, context.Dialect, context.Table,
                ReadColumns(context.Table, config), keyData, forUpdate: true);

            context.MutationState[BeforeImageKey] = new BeforeImage(row);
            return Array.Empty<string>();
        }

        /// <summary>
        /// Post-write phase: pair the before-image with the stored after-image and write one
        /// history row. A throw here rolls the mutation back — if the change cannot be
        /// recorded, the change itself must not commit.
        /// </summary>
        public async ValueTask AfterWriteInTransactionAsync(MutationObserverContext context)
        {
            var config = HistoryConfig.FromTable(context.Table);
            if (!config.RecordsHistory || !MayRecord(config, context.MutationType))
                return;

            // An UPDATE/DELETE that affected zero rows changed nothing — an out-of-scope
            // tenant/policy no-op, or a predicate that matched no row. Recording it would
            // fabricate a change that never happened, so the before-image read above is
            // simply discarded. An INSERT's result is the generated identity, not a count.
            if (IsUpdateOrDelete(context.MutationType) && AffectedZeroRows(context.Result))
                return;

            if (context.Connection is null || context.Model is null || context.Dialect is null)
                throw new BifrostExecutionError(
                    "The change-history writer was invoked without an open transaction; the change " +
                    "could not be recorded.");

            var historyTable = ResolveHistoryTable(context.Model, context.Table, config);
            var trackedColumns = TrackedColumns(context.Table, config);

            // The operation as RECORDED, which is not always the operation as declared: an
            // upsert is driven through the pipeline as an update, and when it inserts a new
            // row there was no before-image to capture. Nothing existed before and a row
            // exists now — that is an insert, and recording it as an update with an empty
            // before-image would misstate what happened.
            var operation = context.MutationType;
            IReadOnlyDictionary<string, object?>? before = null;
            if (operation != MutationType.Insert)
            {
                // A keyless plain update on a table that records only inserts skipped
                // capture in the pre-write phase — a keyless write can never be an upsert,
                // so its actual operation is an update this table does not record.
                if (operation == MutationType.Update
                    && !config.Records(MutationType.Update)
                    && !context.MutationState.ContainsKey(BeforeImageKey))
                    return;

                var captured = RequireCapturedBeforeImage(context);
                if (captured.Row is null)
                {
                    if (operation == MutationType.Delete)
                        throw new BifrostExecutionError(
                            $"The change-history writer found no row to capture before a delete of " +
                            $"'{Qualify(context.Table)}' that then affected a row. Refusing to record a change " +
                            "with an empty before-image.");

                    operation = MutationType.Insert;
                }
                else
                {
                    before = captured.Row;
                }
            }

            // The insert-flip above decided what ACTUALLY happened; record only what the
            // table opted into. An upsert-that-inserted on an update-only table (or the
            // reverse) reached this point solely to make that determination.
            if (!config.Records(operation))
                return;

            var keyData = ResolveKeyData(context.Table, context.Data, context.Result, operation);

            // Read the stored post-image so DB defaults / triggers are recorded as stored.
            // A DELETE has none. The row must exist: the write we just ran affected it.
            IReadOnlyDictionary<string, object?>? after = null;
            if (operation != MutationType.Delete)
            {
                // No forUpdate here: this read-back runs after the write, inside the same
                // transaction — the write already holds an exclusive lock on the row, so an
                // update-lock hint would be redundant.
                after = await MutationCommandExecutor.LoadRowByKey(
                    context.Connection, context.Transaction, context.Dialect, context.Table,
                    ReadColumns(context.Table, config), keyData);

                if (after is null)
                    throw new BifrostExecutionError(
                        $"The change-history writer could not read back the row it just wrote in " +
                        $"'{Qualify(context.Table)}'; refusing to record a change whose result cannot be read.");
            }

            var changedColumns = ChangedColumns(operation, trackedColumns, before, after);

            // An update that moved no TRACKED column is not a change this table records — the
            // point of history-columns is to keep that noise out of the trail.
            if (operation == MutationType.Update && changedColumns.Count == 0)
                return;

            var historyRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataKeys.History.Column.Entity] = Qualify(context.Table),
                [MetadataKeys.History.Column.EntityId] = JsonSerializer.Serialize(keyData),
                [MetadataKeys.History.Column.Op] = operation.ToString().ToLowerInvariant(),
                [MetadataKeys.History.Column.Actor] = AuditMutationTransformer.ResolveActor(context.Model, context.UserContext)?.ToString(),
                [MetadataKeys.History.Column.ChangedAt] = DateTime.UtcNow,
                [MetadataKeys.History.Column.Before] = before is null ? null : JsonSerializer.Serialize(Project(before, trackedColumns)),
                [MetadataKeys.History.Column.After] = after is null ? null : JsonSerializer.Serialize(Project(after, trackedColumns)),
                [MetadataKeys.History.Column.ChangedColumns] = JsonSerializer.Serialize(changedColumns),
                // id is an identity column, so it is omitted from the INSERT column list.
            };

            // A tenant-filtered table materializes its tenant scope value into the trail
            // row's own column (validated to exist at model load), because history reads
            // are authorized by plain column predicates against the trail itself. The
            // value is the row's stored scope: the after-image on insert/update, the
            // before-image on delete — copied even when history-columns narrows the
            // tenant column out of the recorded images.
            var scopeColumn = HistoryConfig.ResolveTenantScopeColumn(context.Table);
            if (scopeColumn is not null)
                historyRow[scopeColumn] = Lookup(
                    (operation == MutationType.Delete ? before : after)!, scopeColumn);

            var tableRef = context.Dialect.TableReference(historyTable.TableSchema, historyTable.DbName);
            var sql = MutationCommandExecutor.BuildInsertInto(
                context.Dialect, historyTable, tableRef, historyRow.Keys) + ";";

            await MutationCommandExecutor.ExecuteNonQuery(
                context.Connection, context.Transaction, sql, historyRow);
        }

        /// <summary>
        /// Whether the declared operation MIGHT need recording. An upsert is driven
        /// through the pipeline as an Update but may turn out to be an insert (the
        /// insert-flip in the post-write phase), so an update on an insert-recording
        /// table must still reach capture; the actual operation is re-checked against
        /// the config once the flip has decided what really happened.
        /// </summary>
        private static bool MayRecord(HistoryConfig config, MutationType declared)
            => config.Records(declared)
               || (declared == MutationType.Update && config.Records(MutationType.Insert));

        /// <summary>
        /// The capture this mutation's pre-write phase left behind — which may legitimately
        /// hold no row (an upsert that inserted). Its ABSENCE, on the other hand, means the
        /// write path never ran that phase at all, so recording would invent a before-image
        /// the writer never read. Fail closed: a trail with a fabricated pre-image is worse
        /// than a rejected write.
        /// </summary>
        private static BeforeImage RequireCapturedBeforeImage(MutationObserverContext context)
        {
            if (!context.MutationState.TryGetValue(BeforeImageKey, out var captured)
                || captured is not BeforeImage image)
                throw new BifrostExecutionError(
                    $"No before-image was captured for the {context.MutationType.ToString().ToLowerInvariant()} of " +
                    $"'{Qualify(context.Table)}', which records change history. Refusing to record a change without " +
                    "the row it replaced.");

            return image;
        }

        /// <summary>
        /// The columns whose changes are recorded: the <c>history-columns</c> allow-list, or
        /// every column of the table when the key is omitted. Names are DB column names.
        /// </summary>
        private static IReadOnlyList<string> TrackedColumns(IDbTable table, HistoryConfig config)
            => config.TracksAllColumns
                ? table.Columns.Select(c => c.ColumnName).ToList()
                : config.TrackedColumns;

        // Read the tracked columns plus the key — so the images carry the row's identity
        // even when history-columns narrows away the key column — plus the tenant scope
        // column, so the trail row can materialize the row's tenant value even when
        // history-columns narrows the tenant column out of the images.
        private static IReadOnlyCollection<string> ReadColumns(IDbTable table, HistoryConfig config)
        {
            var columns = new List<string>(TrackedColumns(table, config));
            var seen = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
            foreach (var key in table.KeyColumns.Select(k => k.ColumnName))
            {
                if (seen.Add(key))
                    columns.Add(key);
            }

            var scopeColumn = HistoryConfig.ResolveTenantScopeColumn(table);
            if (scopeColumn is not null && seen.Add(scopeColumn))
                columns.Add(scopeColumn);

            return columns;
        }

        /// <summary>
        /// The tracked columns that actually moved. An INSERT reports everything it stored,
        /// a DELETE everything it removed, and an UPDATE only the columns whose value
        /// differs between the two stored images.
        /// </summary>
        private static IReadOnlyList<string> ChangedColumns(
            MutationType mutationType,
            IReadOnlyList<string> trackedColumns,
            IReadOnlyDictionary<string, object?>? before,
            IReadOnlyDictionary<string, object?>? after)
        {
            if (mutationType == MutationType.Insert)
                return trackedColumns.Where(c => after!.ContainsKey(c)).ToList();

            if (mutationType == MutationType.Delete)
                return trackedColumns.Where(c => before!.ContainsKey(c)).ToList();

            return trackedColumns
                .Where(c => !ValuesEqual(Lookup(before!, c), Lookup(after!, c)))
                .ToList();
        }

        private static object? Lookup(IReadOnlyDictionary<string, object?> row, string column)
            => row.TryGetValue(column, out var value) ? value : null;

        // Both images are read from the same table through the same reader, so a column's
        // values share a CLR type and Equals compares them faithfully. Byte arrays are the
        // exception — reference equality would report every binary column as changed.
        private static bool ValuesEqual(object? left, object? right)
        {
            if (left is null || right is null)
                return left is null && right is null;

            if (left is byte[] leftBytes && right is byte[] rightBytes)
                return leftBytes.AsSpan().SequenceEqual(rightBytes);

            return Equals(left, right);
        }

        private static IReadOnlyDictionary<string, object?> Project(
            IReadOnlyDictionary<string, object?> row, IReadOnlyList<string> columns)
            => columns
                .Where(row.ContainsKey)
                .ToDictionary(c => c, c => row[c], StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The history table for this table: its own <c>history-table</c> override, else the
        /// model-level shared default. Both are validated at model load; a failure here means
        /// the model changed underneath the writer, so it fails the mutation rather than
        /// silently dropping the trail.
        /// </summary>
        private static IDbTable ResolveHistoryTable(IDbModel model, IDbTable table, HistoryConfig config)
        {
            var name = config.ResolveTargetName(model);
            if (name is null)
                throw new BifrostExecutionError(
                    $"Table '{Qualify(table)}' records change history but no '{MetadataKeys.History.Table}' is " +
                    "configured on the table or on the model.");

            return ModelTableReference.Find(model, name)
                ?? throw new BifrostExecutionError(
                    $"The configured history table '{name}' was not found in the model.");
        }

        /// <summary>
        /// The row's primary-key values, for the WHERE predicate and the recorded
        /// <c>entity_id</c>. On an INSERT with a database-generated single-column key the
        /// client supplied no value, so it is taken from the write result (the returned
        /// identity). A key that still cannot be resolved fails the mutation: a history row
        /// that cannot name its row is unusable, and a composite key with a generated
        /// component is not supported (the same limit the outbox writer carries).
        /// </summary>
        private static Dictionary<string, object?> ResolveKeyData(
            IDbTable table, IDictionary<string, object?> data, object? result, MutationType mutationType)
        {
            var keyColumns = table.KeyColumns.ToList();
            var keyData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var column in keyColumns)
            {
                var name = column.ColumnName;
                if (data.TryGetValue(name, out var value) && value is not null)
                {
                    keyData[name] = value;
                    continue;
                }

                if (mutationType == MutationType.Insert && keyColumns.Count == 1 && result is not null)
                {
                    keyData[name] = result;
                    continue;
                }

                throw new BifrostExecutionError(
                    $"The change-history writer could not resolve primary-key column '{name}' of " +
                    $"'{Qualify(table)}'. Refusing to record a change that cannot name its row.");
            }

            return keyData;
        }

        /// <summary>
        /// The mutation's full primary key, or null when it does not carry one — the signal
        /// that the write is predicate-scoped rather than row-scoped.
        /// </summary>
        private static Dictionary<string, object?>? TryBuildKeyData(
            IDbTable table, IDictionary<string, object?> data)
        {
            var keyData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in table.KeyColumns)
            {
                if (!data.TryGetValue(column.ColumnName, out var value) || value is null)
                    return null;
                keyData[column.ColumnName] = value;
            }

            return keyData.Count > 0 ? keyData : null;
        }

        private static bool IsUpdateOrDelete(MutationType mutationType)
            => mutationType is MutationType.Update or MutationType.Delete;

        // The write result of an UPDATE/DELETE is the affected-row count. A non-numeric or
        // null result is treated as non-zero: never suppress a record we cannot classify.
        private static bool AffectedZeroRows(object? result)
            => result is not null
               && (result is int i ? i == 0
                   : result is long l ? l == 0
                   : result is IConvertible c && Convert.ToInt64(c) == 0);

        private static string Qualify(IDbTable table) => $"{table.TableSchema}.{table.DbName}";
    }
}
