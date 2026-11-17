import type { ColumnFiltersState } from '@tanstack/react-table';
import type { Column, Table } from '../types/schema';
import { buildColumnFilters, getGraphQlType, parseTableFilterString } from './query-builder';
import { coerceForGql } from './fk';

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

/**
 * Grouped results have their own aggregate-row ordering.  It is intentionally
 * separate from the flat-grid sort: the latter orders individual records,
 * while this controls aggregate buckets.  Count ties always use the group key
 * ascending so pagination remains deterministic.
 */
export type GroupingSort = { field: 'key' | 'count'; desc: boolean };

export const DEFAULT_GROUPING_SORT: GroupingSort = { field: 'key', desc: false };

/**
 * Most buckets one grouping request shows. The aggregate is asked for one more
 * (the cap+1 sentinel) so a cut is reported instead of passing as the whole set;
 * without an explicit limit the server's default row window (100) cut silently.
 */
export const GROUPING_BUCKET_CAP = 1000;

/** Aggregate buckets plus whether the server had more than the cap. */
export interface GroupingResult {
    rows: GroupingRow[];
    truncated: boolean;
}

export interface GridGroupingRequest {
    query: string;
    variables: Record<string, unknown>;
    groupBy: Column;
    sumBy: Column | null;
    sort: GroupingSort;
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
    return column && /^(byte|short|int|long|float|double|decimal|numeric|money|real)/i.test(column.paramType.replace(/[![\]]/g, ''))
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

/** The grid's active predicate as declarations, clauses and variables. */
interface ActivePredicate {
    params: string[];
    clauses: string[];
    variables: Record<string, unknown>;
}

/**
 * The predicate the grid's list query applies, built by the grid's OWN
 * builders (parseTableFilterString + buildColumnFilters): grouping counts the
 * rows the grid shows, including the date-filter day widening and scalar
 * coercion those builders do. A filter the grid cannot apply throws rather
 * than counting over a wider row set; the grid refuses to run in that case
 * too, and useDataTable does not request grouping while it does.
 */
function activePredicate(table: Table, columnFilters: ColumnFiltersState, headerFilter: string): ActivePredicate {
    const header = parseTableFilterString(headerFilter);
    const columns = buildColumnFilters(columnFilters, table);
    const errors = header.error ? [header.error, ...columns.errors] : columns.errors;
    if (errors.length > 0) throw new Error(`The grouping filter can't be applied: ${errors.join(' ')}`);
    return {
        params: [...(header.param ? [header.param.replace(/^,\s*/, '')] : []), ...columns.params],
        clauses: [...(header.filterText ? [header.filterText] : []), ...columns.filterTexts],
        variables: { ...header.variables, ...columns.variables },
    };
}

function andAll(clauses: readonly string[]): string {
    return clauses.length === 1 ? clauses[0] : `{and: [${clauses.join(', ')}]}`;
}

function declarations(params: readonly string[]): string {
    return params.length > 0 ? `(${params.join(', ')})` : '';
}

/** Builds the schema-derived aggregate document used by the grouped grid. */
export function buildGridGroupingRequest(
    table: Table,
    groupBy: Column,
    columnFilters: ColumnFiltersState,
    headerFilter: string,
    sumBy: Column | null = null,
    sort: GroupingSort = DEFAULT_GROUPING_SORT,
): GridGroupingRequest {
    for (const name of [table.name, table.graphQlName, groupBy.graphQlName, ...(sumBy ? [sumBy.graphQlName] : [])]) {
        if (!GRAPHQL_NAME.test(name)) throw new Error('Invalid schema-derived grouping name.');
    }
    const active = activePredicate(table, columnFilters, headerFilter);
    const args = [active.clauses.length > 0 ? `filter: ${andAll(active.clauses)}` : '', `groupBy: [${groupBy.graphQlName}]`, `limit: ${GROUPING_BUCKET_CAP + 1}`].filter(Boolean).join(', ');
    return {
        query: `query GridGrouping${declarations(active.params)} { ${table.name}Aggregate(${args}) { ${groupBy.graphQlName} _count${sumBy ? ` _sum { ${sumBy.graphQlName} }` : ''} } }`,
        variables: active.variables,
        groupBy,
        sumBy,
        sort,
    };
}

/** Defined aggregate ordering for the server-grouped replacement surface. */
export function compareGroupingRowsByKey(left: GroupingRow, right: GroupingRow): number {
    if (left.value === right.value) return 0;
    if (left.value === null || left.value === undefined) return -1;
    if (right.value === null || right.value === undefined) return 1;
    if (typeof left.value === 'number' && typeof right.value === 'number') return left.value - right.value;
    return String(left.value).localeCompare(String(right.value));
}

/** Sort aggregate buckets by the selected key/count direction with a stable key tie-breaker. */
export function compareGroupingRows(left: GroupingRow, right: GroupingRow, sort: GroupingSort = DEFAULT_GROUPING_SORT): number {
    if (sort.field === 'count') {
        const byCount = left.count - right.count;
        if (byCount !== 0) return sort.desc ? -byCount : byCount;
        return compareGroupingRowsByKey(left, right);
    }
    const byKey = compareGroupingRowsByKey(left, right);
    return sort.desc ? -byKey : byKey;
}

/**
 * Reads the aggregate buckets (with the configured measure, never derived from
 * page rows), dropping the cap+1 sentinel and reporting it as `truncated`.
 * The server windows buckets in key order, so a truncated count sort ranks
 * only the first GROUPING_BUCKET_CAP keys; the summary says so.
 */
export function readGroupingResult(data: Record<string, unknown> | undefined, table: Table, groupBy: Column, sumBy: Column | null, sort: GroupingSort = DEFAULT_GROUPING_SORT): GroupingResult {
    const raw = data?.[`${table.name}Aggregate`];
    if (!Array.isArray(raw)) return { rows: [], truncated: false };
    const truncated = raw.length > GROUPING_BUCKET_CAP;
    // Couple each server row to its configured sum before ordering; never
    // calculate a measure from the current flat page.
    const rows = raw.slice(0, GROUPING_BUCKET_CAP).map((source) => {
        const record = source as Record<string, unknown>;
        return {
            value: record[groupBy.graphQlName],
            count: Number(record._count ?? 0),
            sum: sumBy ? (record._sum as Record<string, unknown> | undefined)?.[sumBy.graphQlName] : undefined,
        };
    }).sort((left, right) => compareGroupingRows(left, right, sort));
    return { rows, truncated };
}

/** Schema-derived member query for a single aggregate key. It merges, never replaces, active filters. */
export function buildGridGroupMemberRequest(table: Table, groupBy: Column, value: unknown, columnFilters: ColumnFiltersState, headerFilter: string, sort: readonly string[] = []): GridGroupMemberRequest {
    for (const name of [table.name, table.graphQlName, groupBy.graphQlName, ...table.columns.map((column) => column.graphQlName)]) {
        if (!GRAPHQL_NAME.test(name)) throw new Error('Invalid schema-derived grouping name.');
    }
    const active = activePredicate(table, columnFilters, headerFilter);
    // A null bucket is a predicate, not equality to null; empty strings remain _eq "".
    // The key is bound as the column's own scalar, coerced like a grid filter value.
    const isNullBucket = value === null || value === undefined;
    const memberType = getGraphQlType(groupBy.paramType);
    const memberClause = isNullBucket ? `{${groupBy.graphQlName}: {_null: true}}` : `{${groupBy.graphQlName}: {_eq: $member}}`;
    const params = [...active.params, ...(isNullBucket ? [] : [`$member: ${memberType}`]), `$sort: [${table.graphQlName}SortEnum!]`, '$limit: Int', '$offset: Int'];
    const fields = table.columns.map((column) => column.graphQlName).join(' ');
    return {
        query: `query GridGroupMembers${declarations(params)} { ${table.name}(filter: ${andAll([...active.clauses, memberClause])} sort: $sort limit: $limit offset: $offset) { total offset limit data { ${fields} } } }`,
        // Member ordering deliberately remains the active flat-grid order.
        // It is bound as a GraphQL variable, never interpolated from UI state.
        variables: { ...active.variables, ...(isNullBucket ? {} : { member: coerceForGql(value, memberType) }), sort: [...sort], limit: 50, offset: 0 },
        responseKey: table.name,
    };
}
