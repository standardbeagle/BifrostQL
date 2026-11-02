using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.History
{
    /// <summary>
    /// The built-in history writer (<c>history-writer: table</c>, the default): one row per
    /// change in the table's <c>history-table</c>, carrying the
    /// <see cref="MetadataKeys.History.HistoryColumns"/> contract that ModelConfigValidator
    /// checks at model load and the generated <c>&lt;table&gt;History</c> field reads back.
    /// </summary>
    public sealed class HistoryTableWriter : IHistoryWriter
    {
        public string Name => MetadataKeys.History.DefaultWriter;

        public async ValueTask WriteAsync(HistoryRecord record, MutationObserverContext context)
        {
            var historyRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataKeys.History.Column.Entity] = record.Entity,
                [MetadataKeys.History.Column.EntityId] = JsonSerializer.Serialize(record.Key),
                [MetadataKeys.History.Column.Op] = record.Operation.ToString().ToLowerInvariant(),
                [MetadataKeys.History.Column.Actor] = record.Actor?.ToString(),
                [MetadataKeys.History.Column.ChangedAt] = record.ChangedAt,
                [MetadataKeys.History.Column.Before] = record.Before is null ? null : JsonSerializer.Serialize(record.Before),
                [MetadataKeys.History.Column.After] = record.After is null ? null : JsonSerializer.Serialize(record.After),
                [MetadataKeys.History.Column.ChangedColumns] = JsonSerializer.Serialize(record.ChangedColumns),
                // id is an identity column, so it is omitted from the INSERT column list.
            };

            // A tenant-filtered table materializes its tenant scope value into the trail
            // row's own column (validated to exist at model load), because history reads
            // are authorized by plain column predicates against the trail itself.
            if (record.TenantScopeColumn is not null)
                historyRow[record.TenantScopeColumn] = record.TenantScopeValue;

            var historyTable = ResolveHistoryTable(context.Model!, record.Table);
            var tableRef = context.Dialect!.TableReference(historyTable.TableSchema, historyTable.DbName);
            var sql = MutationCommandExecutor.BuildInsertInto(
                context.Dialect, historyTable, tableRef, historyRow.Keys) + ";";

            await MutationCommandExecutor.ExecuteNonQuery(
                context.Connection!, context.Transaction, sql, historyRow);
        }

        /// <summary>
        /// The right-to-erasure trail policy: TOMBSTONE, not append.
        /// <list type="number">
        ///   <item>it PURGES the entity's existing trail rows (the before/after images from
        ///     prior inserts/updates — themselves the erased PII) via a direct dialect DELETE
        ///     on the purge's own transaction, so no PII survives the erasure;</item>
        ///   <item>it records ONE payload-free tombstone (<c>op='erase'</c>, before/after
        ///     both null) so the FACT of erasure — who, when, which entity — stays auditable
        ///     without the data.</item>
        /// </list>
        /// Both statements are direct SQL that never re-enter the mutation pipeline, so
        /// neither triggers history-of-history, and the erasure terminates by construction.
        /// </summary>
        public async ValueTask EraseAsync(HistoryRecord record, MutationObserverContext context)
        {
            var historyTable = ResolveHistoryTable(context.Model!, record.Table);
            var entityId = JsonSerializer.Serialize(record.Key);
            var tableRef = context.Dialect!.TableReference(historyTable.TableSchema, historyTable.DbName);

            // 1. Purge the entity's existing trail. entity_id is the row's serialized primary
            //    key (row-unique), so this reaches only this entity's trail — it cannot cross
            //    to another row, or, on a tenant-filtered table, to another tenant.
            var purgeScope = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataKeys.History.Column.Entity] = record.Entity,
                [MetadataKeys.History.Column.EntityId] = entityId,
            };
            await MutationCommandExecutor.ExecuteNonQuery(
                context.Connection!, context.Transaction,
                HistoryErasure.BuildTrailPurgeSql(context.Dialect, tableRef), purgeScope);

            // 2. Record the payload-free tombstone.
            var tombstone = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataKeys.History.Column.Entity] = record.Entity,
                [MetadataKeys.History.Column.EntityId] = entityId,
                [MetadataKeys.History.Column.Op] = HistoryErasure.EraseOp,
                [MetadataKeys.History.Column.Actor] = record.Actor?.ToString(),
                [MetadataKeys.History.Column.ChangedAt] = record.ChangedAt,
                [MetadataKeys.History.Column.Before] = null,
                [MetadataKeys.History.Column.After] = null,
                [MetadataKeys.History.Column.ChangedColumns] = JsonSerializer.Serialize(Array.Empty<string>()),
            };

            if (record.TenantScopeColumn is not null)
                tombstone[record.TenantScopeColumn] = record.TenantScopeValue;

            var insertSql = MutationCommandExecutor.BuildInsertInto(
                context.Dialect, historyTable, tableRef, tombstone.Keys) + ";";
            await MutationCommandExecutor.ExecuteNonQuery(
                context.Connection!, context.Transaction, insertSql, tombstone);
        }

        /// <summary>
        /// The history table for this table: its own <c>history-table</c> override, else the
        /// model-level shared default. Both are validated at model load; a failure here means
        /// the model changed underneath the writer, so it fails the mutation rather than
        /// silently dropping the trail.
        /// </summary>
        private static IDbTable ResolveHistoryTable(IDbModel model, IDbTable table)
        {
            var name = HistoryConfig.FromTable(table).ResolveTargetName(model);
            if (name is null)
                throw new BifrostExecutionError(
                    $"Table '{table.TableSchema}.{table.DbName}' records change history but no " +
                    $"'{MetadataKeys.History.Table}' is configured on the table or on the model.");

            return ModelTableReference.Find(model, name)
                ?? throw new BifrostExecutionError(
                    $"The configured history table '{name}' was not found in the model.");
        }
    }
}
