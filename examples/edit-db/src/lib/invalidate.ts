/**
 * @module lib/invalidate
 *
 * Cache invalidation after a table write (insert/update/delete). With the
 * editor's long staleTime, invalidating only ['tableData', table] leaves every
 * other cached view of the written rows stale — most dangerously the edit
 * dialog's ['editRecord'] query: reopening a record served the pre-save row,
 * and re-saving echoed the old values back, silently reverting the first save.
 *
 * Row data also leaks across query families and tables: grids embed joined
 * labels from other tables, FK popovers/preview cards cache row snapshots, FK
 * dropdowns cache label lists, and m2m panels cache junction+target rows. So a
 * write invalidates every family that can carry the written rows. Correctness
 * over refetch thrift: only actively-observed queries refetch immediately;
 * the rest just refetch on next mount instead of serving stale data.
 */

import type { QueryClient } from '@tanstack/react-query';

/**
 * Families whose cached rows can embed data from ANY table (joined labels,
 * nested previews, cross-table counts), so a write to one table can stale them
 * all. Invalidate the whole family.
 */
const CROSS_TABLE_FAMILIES = new Set([
    'tableData',    // grids embed joined labels from other tables
    'editRecord',   // edit dialog row (stale re-save = silent data loss)
    'tableRowCounts',
    'm2mRows',      // junction rows + nested target labels
    'fkPreview',    // FK hover-card row snapshot
]);

/**
 * Families keyed by the queried table in queryKey[1]; only stale when that
 * table is the one written (they never embed other tables' data).
 */
const TABLE_SCOPED_FAMILIES = new Set([
    'tableRef',          // FK dropdown options sourced from the written table
    'tableRefValue',     // FK display label for a key on the written table
    'compositeTableRef',
    'm2mTargets',        // m2m picker target list
]);

/**
 * Invalidate every query family that can serve rows affected by a write to
 * `tableName`. Returns the refetch promise for callers that want to await it;
 * queries are marked invalidated synchronously either way.
 */
export function invalidateAfterTableWrite(queryClient: QueryClient, tableName: string): Promise<void> {
    return queryClient.invalidateQueries({
        predicate: (query) => {
            const [family, table] = query.queryKey as readonly unknown[];
            if (typeof family !== 'string') return false;
            if (CROSS_TABLE_FAMILIES.has(family)) return true;
            if (TABLE_SCOPED_FAMILIES.has(family)) return table === tableName;
            return false;
        },
    });
}
