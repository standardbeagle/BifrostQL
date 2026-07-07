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
    // Several multi-joins target the same child. Only `idColumn` can pick the right
    // one; return the exact match (or undefined when it names no candidate). Never
    // fall back to matches[0] — silently choosing the wrong relationship built a query
    // scoped to the wrong FK, which returns the wrong parent's children. Ambiguity
    // (no idColumn, or an idColumn that matches nothing) resolves to undefined so the
    // caller surfaces "relationship unavailable" rather than wrong data.
    if (!idColumn) return undefined;
    return matches.find((j) => j.destinationColumnNames?.[0] === idColumn);
}

/** The child collection field name on the parent type for a given join. */
export function childFieldName(join: Join): string {
    return join.fieldName ?? join.destinationTable;
}
