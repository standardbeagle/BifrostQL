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
import { rowIdOf, decodeRouteParts } from './row-id';
import { getPkTypes } from './query-builder';

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
    const srcCols = m2m.junctionSourceColumnNames;
    if (srcCols.length === 0)
        throw new Error(`Many-to-many relationship '${m2m.name}' has no junction source columns.`);

    // Decompose the route-encoded parent id into one value per junction source column.
    // A composite junction FK MUST be filtered on every column: filtering on the first
    // column alone can match another parent's junction rows whose first-column value
    // collides, and detaching then deletes that other parent's link.
    //
    // COLUMN-ORDER INVARIANT (composite keys): parentId is produced by rowIdOf /
    // encodePkRoute, which encodes the parent row's key values in `parentTable.primaryKeys`
    // order. This function pairs `parentParts[i]` with `junctionSourceColumnNames[i]`
    // positionally, so the three arrays must line up index-for-index:
    //   parentTable.primaryKeys[i]  ↔  m2m.sourceColumnNames[i]  ↔  m2m.junctionSourceColumnNames[i]
    // i.e. the i-th junction source column references the i-th parent key column, and the
    // parent key columns are enumerated in the same order the route was encoded. The server
    // relationship metadata emits sourceColumnNames / junctionSourceColumnNames as paired
    // arrays, and single-column keys (the common case) are trivially aligned. Locked by
    // m2m.test.ts ("composite junction rows query"). If a future server ever emits these
    // arrays out of parent-PK order, pair by name here instead of by index.
    // Locked by m2m.test.ts › m2mRowsQuery › "pairs each junction source column with the
    // route part at the same index".
    const parentParts = decodeRouteParts(parentId, srcCols.length);

    const columnByName = new Map(junction.columns.map((c) => [c.name, c] as const));
    const paramDecls: string[] = [];
    const clauses: string[] = [];
    const variables: Record<string, unknown> = {};
    srcCols.forEach((col, i) => {
        const gqlType = (columnByName.get(col)?.paramType ?? 'Int').replace('!', '');
        const varName = `src${i}`;
        paramDecls.push(`$${varName}: ${gqlType}`);
        clauses.push(`{ ${col}: { _eq: $${varName} } }`);
        variables[varName] = coerceId(parentParts[i], gqlType);
    });
    const filterText = clauses.length === 1 ? clauses[0] : `{ and: [${clauses.join(', ')}] }`;

    const junctionPks = junction.primaryKeys ?? [];
    const payload = payloadColumns(junction, m2m).map((c) => c.name);

    const targetPks = target.primaryKeys?.length ? target.primaryKeys : m2m.targetColumnNames;
    const labelCol = target.labelColumn;
    const targetFields = [...targetPks];
    if (labelCol && !targetPks.includes(labelCol)) targetFields.push(`label: ${labelCol}`);

    const targetSelection = `${m2m.junctionTargetField} { ${targetFields.join(' ')} }`;
    const fields = [...junctionPks, ...payload, targetSelection].join(' ');

    const query =
        `query GetLinks_${junction.name}(${paramDecls.join(', ')}, $limit: Int, $offset: Int) ` +
        `{ ${junction.name}(filter: ${filterText} limit: $limit offset: $offset) ` +
        `{ total offset limit data { ${fields} } } }`;

    return { query, variables };
}

export interface M2mTargetPickerPlan {
    query: string;
    idColumn: string;
    /** True when the label column is String-typed and thus supports the
     *  server-side `_contains` search; callers fall back to client filtering. */
    serverSearch: boolean;
}

/**
 * Build the target-list query used when adding a many-to-many link.
 * Sort values are BifrostQL enum literals, so they must not be quoted.
 *
 * When `search` is non-empty the list is filtered server-side (a `_contains` on
 * the label column) so a large target table stays findable beyond the fetch
 * limit, instead of searching only within a client-side window.
 */
export function m2mTargetPickerPlan(target: Table, m2m: ManyToManyJoin, search?: string): M2mTargetPickerPlan {
    const idColumn = getPkTypes(target)[0]?.name ?? m2m.targetColumnNames[0];
    const labelColumn = target.labelColumn || idColumn;
    const fields = labelColumn !== idColumn ? `${idColumn} label: ${labelColumn}` : idColumn;
    // `_contains` is only generated for String columns, so server-side search is
    // only valid when the label column is String; otherwise the caller filters
    // client-side over the fetched window.
    const labelType = target.columns.find((c) => c.name === labelColumn)?.paramType?.replace('!', '');
    const serverSearch = labelType === 'String';
    const hasSearch = !!search && search.trim() !== '' && serverSearch;
    const paramDecls = hasSearch ? '$limit: Int, $search: String' : '$limit: Int';
    const filterText = hasSearch ? `filter: {${labelColumn}: {_contains: $search}} ` : '';
    const query = `query PickTarget(${paramDecls}) { ${target.name}(${filterText}limit: $limit sort: [${labelColumn}_asc]) { data { ${fields} } } }`;

    return { query, idColumn, serverSearch };
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
 *
 * Single-column junction FKs only. `parentId`/`targetId` are route-encoded
 * strings; for a composite FK they carry multiple values joined by "::", which
 * would be written verbatim into one column — silent data corruption. Rather
 * than guess a decomposition, fail loudly until composite-FK support is built.
 */
export function attachJunctionDetail(
    m2m: ManyToManyJoin,
    parentId: string,
    targetId: string,
): Record<string, unknown> {
    if (m2m.junctionSourceColumnNames.length !== 1 || m2m.junctionTargetColumnNames.length !== 1) {
        throw new Error(
            'attachJunctionDetail supports single-column junction FKs only; ' +
            'this relationship uses a composite key which is not yet supported.',
        );
    }
    return {
        [m2m.junctionSourceColumnNames[0]]: parentId,
        [m2m.junctionTargetColumnNames[0]]: targetId,
    };
}
