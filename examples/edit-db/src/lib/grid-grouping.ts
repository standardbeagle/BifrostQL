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

export function groupingSumColumnFromUrl(value: string | null, table: Table | null): Column | null {
    if (!value || !table) return null;
    const column = groupingColumnFromUrl(value, table);
    return column && /^(byte|short|int|long|float|double|decimal|numeric|money|real)/i.test(column.paramType.replace(/[!\[\]]/g, ''))
        ? column
        : null;
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
    return rows.map((row) => {
        const record = row as Record<string, unknown>;
        return { value: record[groupBy.graphQlName], count: Number(record._count ?? 0), sum: undefined };
    });
}

/** Reads a selected configured measure without ever deriving it from page rows. */
export function readGroupingRowsWithSum(data: Record<string, unknown> | undefined, table: Table, groupBy: Column, sumBy: Column | null): GroupingRow[] {
    const rows = readGroupingRows(data, table, groupBy);
    if (!sumBy) return rows;
    const raw = data?.[`${table.name}Aggregate`];
    return rows.map((row, index) => ({ ...row, sum: ((raw as Record<string, unknown>[] | undefined)?.[index]?._sum as Record<string, unknown> | undefined)?.[sumBy.graphQlName] }));
}

/** Schema-derived member query for a single aggregate key. It merges, never replaces, active filters. */
export function buildGridGroupMemberRequest(table: Table, groupBy: Column, value: unknown, columnFilters: ColumnFiltersState, headerFilter: string): { query: string; variables: Record<string, unknown> } {
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
    };
}
