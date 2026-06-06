/**
 * Pure helpers for scoping standalone child drill-down queries against
 * polymorphic child tables.
 *
 * A polymorphic child (e.g. `notes`) is a shared table keyed by a discriminator
 * column (`entity_type`) plus an id column (`entity_id`). The nested-relationship
 * path (`companies { notes }`) is scoped server-side by both columns, but the
 * editor's separate drill-down query filters by the id column ALONE. For a
 * polymorphic child that leaks other parents' rows that share the id value, so
 * the discriminator predicate must be added to the drill-down filter.
 */

import type { Join } from '../types/schema';

/**
 * Minimal descriptor of the parent→child relationship needed to decide whether
 * (and how) to scope a polymorphic drill-down. Mirrors the polymorphic fields
 * projected onto a multi-join by the server's `_dbSchema` resolver.
 */
export interface PolymorphicDescriptor {
    isPolymorphic?: boolean;
    polymorphicTypeColumn?: string;
    polymorphicTypeValue?: string;
}

/** Escape a value for embedding as a GraphQL string literal. */
function gqlStringLiteral(value: string): string {
    return JSON.stringify(value);
}

/**
 * Build the discriminator filter clause for a polymorphic child drill-down, or
 * `null` when the relationship is not polymorphic (or lacks discriminator info).
 *
 * Returns a single filter object clause string such as
 * `{entity_type: {_eq: "company"}}` ready to be combined with the id predicate.
 */
export function buildPolymorphicClause(rel: PolymorphicDescriptor): string | null {
    if (!rel.isPolymorphic) return null;
    if (!rel.polymorphicTypeColumn || rel.polymorphicTypeValue == null) return null;
    return `{${rel.polymorphicTypeColumn}: {_eq: ${gqlStringLiteral(rel.polymorphicTypeValue)}}}`;
}

/**
 * Build the complete drill-down filter for a parent→child relationship,
 * combining the id predicate with the polymorphic discriminator when present.
 *
 * - Non-polymorphic: `{<idColumn>: {_eq: $id}}` (unchanged legacy shape).
 * - Polymorphic: `{and: [{<idColumn>: {_eq: $id}}, {<typeColumn>: {_eq: "<typeValue>"}}]}`.
 *
 * `idVar` defaults to `$id` to match `buildQuery`'s variable naming.
 */
export function buildChildDrillDownFilter(
    idColumn: string,
    rel: PolymorphicDescriptor,
    idVar = '$id',
): string {
    const idClause = `{ ${idColumn}: { _eq: ${idVar}}}`;
    const typeClause = buildPolymorphicClause(rel);
    if (!typeClause) return idClause;
    return `{and: [${idClause}, ${typeClause}]}`;
}

/**
 * Locate the parent table's multi-join that targets a given child via a given
 * destination (id) column, so the drill-down can read its polymorphic fields.
 * Returns `undefined` when no matching multi-join exists.
 */
export function findChildMultiJoin(
    parentMultiJoins: Join[] | undefined,
    childTable: string,
    idColumn: string,
): Join | undefined {
    return parentMultiJoins?.find(
        (j) => j.destinationTable === childTable && j.destinationColumnNames?.[0] === idColumn,
    );
}
