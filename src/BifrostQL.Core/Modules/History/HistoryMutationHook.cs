using System;
using System.Collections.Generic;
using System.Linq;
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
    ///
    /// Where a recorded change is stored is a plugin: the hook hands each
    /// <see cref="HistoryRecord"/> to the <see cref="IHistoryWriter"/> the table's
    /// <c>history-writer</c> names — the built-in <see cref="HistoryTableWriter"/> unless a
    /// host registers and selects its own.
    /// </summary>
    public sealed class HistoryMutationHook : IBeforeCommitMutationHook, IInTransactionMutationHook
    {
        private readonly IReadOnlyDictionary<string, IHistoryWriter> _writers;

        /// <summary>A hook with only the built-in <see cref="HistoryTableWriter"/>.</summary>
        public HistoryMutationHook() : this(Array.Empty<IHistoryWriter>())
        {
        }

        /// <summary>
        /// A hook with the built-in <see cref="HistoryTableWriter"/> plus the host's writers
        /// (every <see cref="IHistoryWriter"/> registered in DI). The built-in name is
        /// reserved and names must be unique, so a metadata value always selects exactly one
        /// writer.
        /// </summary>
        public HistoryMutationHook(IEnumerable<IHistoryWriter> writers)
        {
            var byName = new Dictionary<string, IHistoryWriter>(StringComparer.OrdinalIgnoreCase);
            foreach (var writer in writers.Prepend(new HistoryTableWriter()))
            {
                if (string.IsNullOrWhiteSpace(writer.Name))
                    throw new InvalidOperationException(
                        $"History writer {writer.GetType().FullName} has no Name; '{MetadataKeys.History.Writer}' could not select it.");
                if (!byName.TryAdd(writer.Name.Trim(), writer))
                    throw new InvalidOperationException(
                        $"Two history writers are named '{writer.Name}' ({byName[writer.Name.Trim()].GetType().FullName} and " +
                        $"{writer.GetType().FullName}); '{MetadataKeys.History.Writer}' must select exactly one. " +
                        $"'{MetadataKeys.History.DefaultWriter}' is the built-in writer's name.");
            }
            _writers = byName;
        }

        /// <summary>The writer the table's <c>history-writer</c> selects. Unknown names fail the write.</summary>
        private IHistoryWriter ResolveWriter(IDbModel model, IDbTable table)
        {
            var name = HistoryConfig.ResolveWriterName(model, table);
            // The wire gets a constant message; the table, configured name and registered plugin
            // names are server detail (invariant 3), logged by the sink for the operator.
            return _writers.TryGetValue(name, out var writer)
                ? writer
                : throw BifrostErrorSink.Sanitized(
                    "The table's history writer is not registered, so the change was refused.",
                    $"Table '{Qualify(table)}' records change history with '{MetadataKeys.History.Writer}: {name}', " +
                    $"but no history writer of that name is registered (registered: {string.Join(", ", _writers.Keys)}). " +
                    "Refusing to commit a change that cannot be recorded.",
                    nameof(HistoryMutationHook));
        }
        // Key under which the pre-write row is parked for this mutation's after-write phase.
        internal const string BeforeImageKey = "bifrost.history.before-image";

        // The pre-write row, or null when no row matched the key. Wrapped in a type (rather
        // than storing the row dictionary directly) so "captured, and there was no row" is
        // distinguishable from "never captured" — the latter must fail closed.
        internal sealed record BeforeImage(IReadOnlyDictionary<string, object?>? Row);

        /// <summary>
        /// The writer acts only on a table that opts into <c>history</c> — the same predicate
        /// both phases below use to no-op — so a set-based fast path may skip it elsewhere.
        /// </summary>
        public bool AppliesTo(IDbTable table) => HistoryConfig.FromTable(table).RecordsHistory;

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
            if (!config.RecordsHistory)
                return;

            // An UPDATE/DELETE that affected zero rows changed nothing — an out-of-scope
            // tenant/policy no-op, or a predicate that matched no row. Recording it would
            // fabricate a change that never happened, so the before-image read above is
            // simply discarded (and an erasure that removed nothing must not tombstone a
            // trail it never touched). An INSERT's result is the generated identity, not a
            // count.
            if (IsUpdateOrDelete(context.MutationType) && AffectedZeroRows(context.Result))
                return;

            if (context.Connection is null || context.Model is null || context.Dialect is null)
                throw new BifrostExecutionError(
                    "The change-history writer was invoked without an open transaction; the change " +
                    "could not be recorded.");

            // Right-to-erasure purge that PHYSICALLY removed the row: tombstone the trail
            // instead of appending a before-image of the very PII the purge erased. Runs for
            // ANY history-recording table — even one that opts into inserts/updates but not
            // deletes — because prior insert/update trail rows still hold that PII and must be
            // cleared. Gated on MutationType.Delete so a ttl expiry that the pipeline turned
            // into a SOFT delete (surfaced here as an Update, the row still present) records
            // normally; only an actual physical removal erases. See IHistoryWriter.EraseAsync.
            if (context.MutationType == MutationType.Delete && HistoryErasure.IsErasurePurge(context.UserContext))
            {
                var erasedKey = ResolveKeyData(context.Table, context.Data, context.Result, MutationType.Delete);
                var erasureScopeColumn = HistoryConfig.ResolveTenantScopeColumn(context.Table);
                await ResolveWriter(context.Model, context.Table).EraseAsync(new HistoryRecord(
                    context.Table, erasedKey, MutationType.Delete,
                    AuditMutationTransformer.ResolveActor(context.Model, context.UserContext),
                    DateTime.UtcNow, Before: null, After: null, Array.Empty<string>(),
                    erasureScopeColumn,
                    erasureScopeColumn is null ? null : ResolveErasureScopeValue(context)), context);
                return;
            }

            if (!MayRecord(config, context.MutationType))
                return;

            var writer = ResolveWriter(context.Model, context.Table);
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

            // A tenant-filtered table carries its tenant scope value on the record: the
            // row's stored scope, the after-image on insert/update, the before-image on
            // delete — copied even when history-columns narrows the tenant column out of
            // the recorded images.
            var scopeColumn = HistoryConfig.ResolveTenantScopeColumn(context.Table);
            var scopeValue = scopeColumn is null
                ? null
                : Lookup((operation == MutationType.Delete ? before : after)!, scopeColumn);

            await writer.WriteAsync(new HistoryRecord(
                context.Table,
                keyData,
                operation,
                AuditMutationTransformer.ResolveActor(context.Model, context.UserContext),
                DateTime.UtcNow,
                before is null ? null : Project(before, trackedColumns),
                after is null ? null : Project(after, trackedColumns),
                changedColumns,
                scopeColumn,
                scopeValue), context);
        }

        // The tenant scope value for an erasure tombstone: the purge synthesizes a per-tenant
        // system context carrying the tenant claim under the model's resolved tenant-context
        // key, and the tracked row was scoped to exactly that tenant, so the claim IS the
        // trail scope value.
        private static object? ResolveErasureScopeValue(MutationObserverContext context)
        {
            var key = TenantFilterTransformer.ResolveTenantContextKey(context.Model!);
            return context.UserContext.TryGetValue(key, out var value) ? value : null;
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
        internal static IReadOnlyDictionary<string, object?>? GetCapturedBeforeImage(MutationObserverContext context)
            => RequireCapturedBeforeImage(context).Row;

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
