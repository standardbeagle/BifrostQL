using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules.History
{
    /// <summary>
    /// Stores the changes <see cref="HistoryMutationHook"/> records. The hook owns WHAT
    /// is recorded — the locked before-image, the read-back after-image, the changed
    /// columns, the upsert insert-flip, the zero-row and keyless refusals — and hands each
    /// change to the writer named by the <c>history-writer</c> metadata (default
    /// <see cref="MetadataKeys.History.DefaultWriter"/>, the built-in
    /// <see cref="HistoryTableWriter"/>). A host adds its own writer by registering an
    /// <see cref="IHistoryWriter"/> singleton and naming it in metadata.
    ///
    /// Both methods run on the mutation's own connection and transaction
    /// (<see cref="MutationObserverContext.Connection"/> /
    /// <see cref="MutationObserverContext.Transaction"/>), so what they write commits or
    /// rolls back with the change. A throw rolls the change back: a change that cannot be
    /// recorded must not commit.
    /// </summary>
    public interface IHistoryWriter
    {
        /// <summary>The name a <c>history-writer</c> metadata value selects (case-insensitive).</summary>
        string Name { get; }

        /// <summary>Store one recorded insert, update or delete.</summary>
        ValueTask WriteAsync(HistoryRecord record, MutationObserverContext context);

        /// <summary>
        /// A right-to-erasure purge physically removed the row. The writer must not keep
        /// the erased data: the built-in writer deletes the row's trail and stores a
        /// payload-free tombstone. A writer whose store cannot forget (an append-only
        /// audit log) should throw, which refuses the purge.
        /// </summary>
        ValueTask EraseAsync(HistoryRecord record, MutationObserverContext context);
    }

    /// <summary>
    /// One recorded change, as <see cref="HistoryMutationHook"/> determined it. Column
    /// names in <see cref="Key"/>, <see cref="Before"/> and <see cref="After"/> are DB
    /// column names and match case-insensitively.
    /// </summary>
    /// <param name="Table">The tracked table.</param>
    /// <param name="Key">The row's primary-key values by DB column name (composite-safe).</param>
    /// <param name="Operation">What actually happened: an upsert that inserted is an Insert.
    /// Delete on <see cref="IHistoryWriter.EraseAsync"/>.</param>
    /// <param name="Actor">The audit user (<c>user-audit-key</c>), or null for system writes.</param>
    /// <param name="ChangedAt">The UTC time the change was recorded.</param>
    /// <param name="Before">Tracked columns before the write; null on insert and erasure.</param>
    /// <param name="After">Tracked columns as stored after the write; null on delete and erasure.</param>
    /// <param name="ChangedColumns">Tracked columns that moved (all stored ones on insert/delete; empty on erasure).</param>
    /// <param name="TenantScopeColumn">The table's tenant column, or null when not tenant-filtered.</param>
    /// <param name="TenantScopeValue">The row's tenant value when <paramref name="TenantScopeColumn"/> is set.</param>
    public sealed record HistoryRecord(
        IDbTable Table,
        IReadOnlyDictionary<string, object?> Key,
        MutationType Operation,
        object? Actor,
        DateTime ChangedAt,
        IReadOnlyDictionary<string, object?>? Before,
        IReadOnlyDictionary<string, object?>? After,
        IReadOnlyList<string> ChangedColumns,
        string? TenantScopeColumn,
        object? TenantScopeValue)
    {
        /// <summary>The tracked table as <c>schema.table</c>.</summary>
        public string Entity => $"{Table.TableSchema}.{Table.DbName}";
    }
}
