/**
 * @module lib/m2m
 *
 * Pure-logic helpers for presenting many-to-many relationships in the edit-db
 * detail panel. A junction table linking a parent to a target entity is skipped
 * for navigation: the detail tab shows the target rows (resolved through the
 * junction's own single-join), with the junction payload revealed on demand.
 *
 * Dependency-free so it can be unit-tested without React or a live server.
 */

import type { Column, Join, ManyToManyJoin, Table } from '../types/schema';
import { rowIdOf } from './row-id';

/** A tab in the detail panel: an ordinary child collection or a m2m bridge. */
export type DetailTab =
    | { kind: 'child'; key: string; join: Join }
    | { kind: 'm2m'; key: string; m2m: ManyToManyJoin };

const NUMERIC_TYPES = new Set(['Int', 'Int!', 'Float', 'Float!']);

/** GraphQL names of every junction table reachable from `table` via a m2m join. */
export function junctionTableNames(table: Table): Set<string> {
    return new Set((table.manyToManyJoins ?? []).map((m) => m.junctionTable));
}

/**
 * The detail tabs for a parent table: its plain one-to-many children (minus any
 * junction tables, which are folded into their m2m tab) followed by one tab per
 * many-to-many bridge.
 */
export function detailTabs(table: Table): DetailTab[] {
    const junctions = junctionTableNames(table);

    const childTabs: DetailTab[] = table.multiJoins
        .filter((j) => !junctions.has(j.destinationTable))
        .map((join) => ({ kind: 'child', key: `child:${join.fieldName ?? join.destinationTable}`, join }));

    const m2mTabs: DetailTab[] = (table.manyToManyJoins ?? []).map((m2m) => ({
        kind: 'm2m',
        key: `m2m:${m2m.junctionTable}:${m2m.targetTable}`,
        m2m,
    }));

    return [...childTabs, ...m2mTabs];
}

/** Junction columns that are neither a primary key nor one of the two bridge FKs. */
export function payloadColumns(junction: Table, m2m: ManyToManyJoin): Column[] {
    const fkNames = new Set([...m2m.junctionSourceColumnNames, ...m2m.junctionTargetColumnNames]);
    return junction.columns.filter((c) => !c.isPrimaryKey && !fkNames.has(c.name));
}

/** Coerce a route id string to the GraphQL type its FK column expects. */
function coerceId(value: string, gqlType: string | undefined): unknown {
    return gqlType && NUMERIC_TYPES.has(gqlType) ? Number(value) : value;
}

/**
 * Build the one-shot query that fetches the junction rows for a parent, each
 * carrying its payload plus the nested target row (key + label). The grid
 * flattens the target columns for display and keeps the junction PK so a link
 * can be detached by deleting its junction row.
 */
export function m2mRowsQuery(
    junction: Table,
    target: Table,
    m2m: ManyToManyJoin,
    parentId: string,
): { query: string; variables: Record<string, unknown> } {
    const srcFk = m2m.junctionSourceColumnNames[0];
    const srcCol = junction.columns.find((c) => c.name === srcFk);
    const idType = srcCol ? srcCol.paramType.replace('!', '') : 'Int';

    const junctionPks = junction.primaryKeys ?? [];
    const payload = payloadColumns(junction, m2m).map((c) => c.name);

    const targetPks = target.primaryKeys?.length ? target.primaryKeys : m2m.targetColumnNames;
    const labelCol = target.labelColumn;
    const targetFields = [...targetPks];
    if (labelCol && !targetPks.includes(labelCol)) targetFields.push(`label: ${labelCol}`);

    const targetSelection = `${m2m.junctionTargetField} { ${targetFields.join(' ')} }`;
    const fields = [...junctionPks, ...payload, targetSelection].join(' ');

    const query =
        `query GetLinks_${junction.name}($id: ${idType}, $limit: Int, $offset: Int) ` +
        `{ ${junction.name}(filter: { ${srcFk}: { _eq: $id } } limit: $limit offset: $offset) ` +
        `{ total offset limit data { ${fields} } } }`;

    return { query, variables: { id: coerceId(parentId, idType) } };
}

/**
 * Read the target row's id and display label from a junction row's nested target
 * selection (emitted by {@link m2mRowsQuery}). Returns null when the link has no
 * resolved target. The label falls back to the id; the id is the target's first
 * primary-key column, coerced to a string for routing.
 */
export function targetDisplay(
    junctionRow: Record<string, unknown>,
    m2m: ManyToManyJoin,
    target: Table,
): { id: string; label: string } | null {
    const nested = junctionRow[m2m.junctionTargetField] as Record<string, unknown> | undefined;
    if (!nested) return null;

    // Composite-aware route id (encodes every target PK column).
    const id = rowIdOf(nested, target, 0);
    const label = nested.label != null ? String(nested.label) : id;
    return { id, label };
}

/**
 * The insert detail for a new link: the parent key on the junction source FK and
 * the chosen target key on the junction target FK. Any payload columns are left
 * unset so the database applies its defaults; the user edits them afterwards.
 */
export function attachJunctionDetail(
    m2m: ManyToManyJoin,
    parentId: string,
    targetId: string,
): Record<string, unknown> {
    return {
        [m2m.junctionSourceColumnNames[0]]: parentId,
        [m2m.junctionTargetColumnNames[0]]: targetId,
    };
}
