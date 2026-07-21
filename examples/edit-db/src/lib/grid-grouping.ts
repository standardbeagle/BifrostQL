import type { ColumnFiltersState } from '@tanstack/react-table';
import type { Column, Table } from '../types/schema';
import { getFilterOperators, type ColumnFilterValue } from './query-builder';

/** URL parameter that owns the selected grouping field. */
export const GRID_GROUP_BY_PARAM = 'gb';
/** Optional, URL-owned numeric measure shown beside every server aggregate. */
export const GRID_GROUP_SUM_PARAM = 'gs';

const GRAPHQL_NAME = /^[_A-Za-z][_0-9A-Za-z]*$/;

/**
 * Resolves the URL value against the current schema.  Keeping this lookup at
 * the boundary means a stale browser-history value can never become GraphQL
 * source text for a different table.
 */
export function groupingColumnFromUrl(value: string | null, table: Table | null): Column | null {
    if (!value || !table) return null;
    return table.columns.find((column) => column.name === value || column.graphQlName === value) ?? null;
}

/** The aggregate result is deliberately server-owned; it is never calculated from page rows. */
export interface GroupingRow {
    value: unknown;
    count: number;
    sum: unknown;
}

export interface GridGroupingRequest {
    query: string;
    variables: Record<string, unknown>;
    groupBy: Column;
    sumBy: Column | null;
}

export interface GridGroupMemberRequest {
    query: string;
    variables: Record<string, unknown>;
    /** Response field selected from the current schema, never parsed from query text. */
    responseKey: string;
}

export function groupingSumColumnFromUrl(value: string | null, table: Table | null): Column | null {
    if (!value || !table) return null;
    const column = groupingColumnFromUrl(value, table);
    return column && /^(byte|short|int|long|float|double|decimal|numeric|money|real)/i.test(column.paramType.replace(/[!\[\]]/g, ''))
        ? column
        : null;
}

/**
 * Update one of the grouping-owned URL fields without disturbing filters, the
 * selected profile, or other application-owned state.  The caller receives a
 * fresh instance so it is safe to use during render/effects as well as event
 * handlers.
 */
export function withGroupingUrlParam(
    search: URLSearchParams,
    param: typeof GRID_GROUP_BY_PARAM | typeof GRID_GROUP_SUM_PARAM,
    value: string | null,
): URLSearchParams {
    const next = new URLSearchParams(search);
    if (value) next.set(param, value);
    else next.delete(param);
    return next;
}

/** Remove grouping state when navigation changes the source table. */
export function withoutGroupingUrlParams(search: URLSearchParams): URLSearchParams {
    const next = new URLSearchParams(search);
    next.delete(GRID_GROUP_BY_PARAM);
    next.delete(GRID_GROUP_SUM_PARAM);
    return next;
}

function asFilter(filters: ColumnFiltersState, table: Table, headerFilter: string): Record<string, unknown> | undefined {
    const clauses: Record<string, unknown>[] = [];

    // Header filters predate the column-filter URL state. Parse only the small,
    // validated tuple format used by the grid; malformed history is ignored.
    try {
        const parsed = JSON.parse(headerFilter) as unknown;
        if (Array.isArray(parsed) && parsed.length === 4) {
            const [name, operator, value, type] = parsed;
            const column = typeof name === 'string' ? table.columns.find((candidate) => candidate.name === name) : undefined;
            if (column && type === column.paramType && typeof operator === 'string' && getFilterOperators(column.paramType).includes(operator)) {
                clauses.push({ [column.graphQlName]: { [operator]: operator === '_null' ? Boolean(value) : value } });
            }
        }
    } catch { /* malformed URL filter: same fail-closed behavior as the grid */ }

    for (const filter of filters) {
        const value = filter.value as ColumnFilterValue;
        const column = table.columns.find((candidate) => candidate.name === filter.id);
        if (!column || !getFilterOperators(column.paramType).includes(value.operator)) continue;
        // _null is a boolean predicate. Empty input is valid for it (the filter
        // controls represent "is null" as true), but not for value operators.
        if (value.operator !== '_null' && (value.value === undefined || value.value === null || value.value === '')) continue;
        clauses.push({ [column.graphQlName]: { [value.operator]: value.operator === '_null' ? Boolean(value.value) : value.value } });
    }
    return clauses.length === 0 ? undefined : clauses.length === 1 ? clauses[0] : { and: clauses };
}

/** Builds the schema-derived aggregate document used by the grouped grid. */
export function buildGridGroupingRequest(
    table: Table,
    groupBy: Column,
    columnFilters: ColumnFiltersState,
    headerFilter: string,
    sumBy: Column | null = null,
): GridGroupingRequest {
    for (const name of [table.name, table.graphQlName, groupBy.graphQlName, ...(sumBy ? [sumBy.graphQlName] : [])]) {
        if (!GRAPHQL_NAME.test(name)) throw new Error('Invalid schema-derived grouping name.');
    }
    const filter = asFilter(columnFilters, table, headerFilter);
    const declarations = filter ? `($filter: ${table.graphQlName}Filter)` : '';
    const args = [filter ? 'filter: $filter' : '', `groupBy: [${groupBy.graphQlName}]`].filter(Boolean).join(', ');
    return {
        query: `query GridGrouping${declarations} { ${table.name}Aggregate(${args}) { ${groupBy.graphQlName} _count${sumBy ? ` _sum { ${sumBy.graphQlName} }` : ''} } }`,
        variables: filter ? { filter } : {},
        groupBy,
        sumBy,
    };
}

export function readGroupingRows(data: Record<string, unknown> | undefined, table: Table, groupBy: Column): GroupingRow[] {
    const rows = data?.[`${table.name}Aggregate`];
    if (!Array.isArray(rows)) return [];
    // Grouped mode has no flat-row header to sort. Its defined sort is the
    // group key ascending (null bucket first), independent of the flat-grid
    // sort state. This keeps paging stable and avoids pretending a row sort
    // applies to an aggregate result.
    return rows.map((row) => {
        const record = row as Record<string, unknown>;
        return { value: record[groupBy.graphQlName], count: Number(record._count ?? 0), sum: undefined };
    }).sort(compareGroupingRowsByKey);
}

/** Defined aggregate ordering for the server-grouped replacement surface. */
export function compareGroupingRowsByKey(left: GroupingRow, right: GroupingRow): number {
    if (left.value === right.value) return 0;
    if (left.value === null || left.value === undefined) return -1;
    if (right.value === null || right.value === undefined) return 1;
    if (typeof left.value === 'number' && typeof right.value === 'number') return left.value - right.value;
    return String(left.value).localeCompare(String(right.value));
}

/** Reads a selected configured measure without ever deriving it from page rows. */
export function readGroupingRowsWithSum(data: Record<string, unknown> | undefined, table: Table, groupBy: Column, sumBy: Column | null): GroupingRow[] {
    const raw = data?.[`${table.name}Aggregate`];
    if (!Array.isArray(raw)) return [];
    // Couple each server row to its configured sum before applying the defined
    // display ordering. Sorting a count-only projection first would associate
    // a null/key-sorted row with a different raw aggregate index.
    return raw.map((source) => {
        const record = source as Record<string, unknown>;
        return {
            value: record[groupBy.graphQlName],
            count: Number(record._count ?? 0),
            sum: sumBy ? (record._sum as Record<string, unknown> | undefined)?.[sumBy.graphQlName] : undefined,
        };
    }).sort(compareGroupingRowsByKey);
}

/** Schema-derived member query for a single aggregate key. It merges, never replaces, active filters. */
export function buildGridGroupMemberRequest(table: Table, groupBy: Column, value: unknown, columnFilters: ColumnFiltersState, headerFilter: string): GridGroupMemberRequest {
    for (const name of [table.name, table.graphQlName, groupBy.graphQlName, ...table.columns.map((column) => column.graphQlName)]) {
        if (!GRAPHQL_NAME.test(name)) throw new Error('Invalid schema-derived grouping name.');
    }
    const active = asFilter(columnFilters, table, headerFilter);
    // A null bucket is a predicate, not equality to null; empty strings remain _eq "".
    const member = { [groupBy.graphQlName]: value === null || value === undefined ? { _null: true } : { _eq: value } };
    const filter = active ? { and: [active, member] } : member;
    const fields = table.columns.map((column) => column.graphQlName).join(' ');
    return {
        query: `query GridGroupMembers($filter: ${table.graphQlName}Filter, $limit: Int, $offset: Int) { ${table.name}(filter: $filter limit: $limit offset: $offset) { total offset limit data { ${fields} } } }`,
        variables: { filter, limit: 50, offset: 0 },
        responseKey: table.name,
    };
}
