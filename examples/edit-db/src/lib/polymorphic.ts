/**
 * Pure helper for locating the parent multi-join that a parent→child
 * related-records drill-down targets.
 *
 * Drill-downs use MODEL B: the query traverses the PARENT and selects the
 * child collection field, so the server injects any polymorphic discriminator
 * (entity_type) automatically — the client only matches on the parent PK and
 * never sends the discriminator. To build that query the client needs the
 * child field name on the parent type, which is the matching multi-join's
 * `fieldName` (falling back to `destinationTable`).
 */

import type { Join } from '../types/schema';

/**
 * Locate the parent table's multi-join that targets a given child table. When
 * the parent has more than one multi-join to the same child (e.g. a polymorphic
 * shared table mapped under several parents, or a self-FK alias), `idColumn`
 * (the child destination column) disambiguates. Returns `undefined` when no
 * matching multi-join exists.
 */
export function resolveChildJoin(
    parentMultiJoins: Join[] | undefined,
    childTable: string,
    idColumn?: string,
): Join | undefined {
    if (!parentMultiJoins) return undefined;
    const matches = parentMultiJoins.filter((j) => j.destinationTable === childTable);
    if (matches.length === 0) return undefined;
    if (matches.length === 1) return matches[0];
    if (idColumn) {
        const byColumn = matches.find((j) => j.destinationColumnNames?.[0] === idColumn);
        if (byColumn) return byColumn;
    }
    return matches[0];
}

/** The child collection field name on the parent type for a given join. */
export function childFieldName(join: Join): string {
    return join.fieldName ?? join.destinationTable;
}
