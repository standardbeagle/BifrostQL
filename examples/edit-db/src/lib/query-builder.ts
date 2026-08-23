/**
 * Pure functions for building GraphQL queries and filters.
 * Extracted from useDataTable for testability.
 */

import type { Table, Column, Join, SchemaContextValue, TableIndex } from '../types/schema';
import type { ColumnFiltersState } from '@tanstack/react-table';
import { rowIdOf, buildPkEqFilter, parsePkRoute, decodePkPart, encodeRouteParts, type PkEqFilterResult, type PkFilter } from './row-id';
import { coerceForGql, gqlTypeOf } from './fk';
import { resolveChildJoin, childFieldName } from './polymorphic';
import { isComposite } from './fk';
import { isBinaryDbType, isLargeValueColumn } from './content-detect';

export interface FilterResult {
    variables: Record<string, unknown>;
    param: string;
    filterText: string;
    /**
     * Non-null when the filter could not be built. The predicate is NOT part of
     * the query, so the caller MUST refuse to run it rather than showing rows the
     * filter was supposed to exclude. `null` with an empty `filterText` means
     * "no filter requested", which is a different thing entirely.
     */
    error: string | null;
}

export interface ColumnFilterValue {
    operator: string;
    value: unknown;
}

interface ColumnWithJoin extends Column {
    joinTable?: Join;
    joinLabelColumn?: string;
}

export interface ColumnFilterResult {
    variables: Record<string, unknown>;
    params: string[];
    filterTexts: string[];
    /**
     * One message per column filter that could not be turned into a predicate.
     * Non-empty means the built predicate is NARROWER than what the UI is showing
     * as active, so the caller must refuse the query instead of returning rows.
     */
    errors: string[];
}

export interface PkTypeInfo {
    name: string;
    gqlType: string;
}

interface RowData {
    id?: number | string | null;
    [key: string]: unknown;
}

const NUMERIC_OPERATORS = ["_eq", "_neq", "_gt", "_gte", "_lt", "_lte", "_between", "_null"];

// Every scalar a type mapper can produce needs an entry. A column type that is
// MISSING here falls through to the String set below, which offers _contains and
// declares the value under a `String` variable — and a String variable in a
// `FilterType<T>Input` position is a hard GraphQL validation error, so the whole
// grid query fails and the column reads as "filter does nothing". Short/Byte
// (smallint/tinyint) and DateTimeOffset used to be exactly that gap; the header
// menu also has to recognize them (see data-table-column-header.tsx) or the
// Filter submenu never renders at all.
//
// The numeric types intentionally omit _in, whose array-valued path is not wired
// through buildColumnFilters or the number-filter UI.
const columnFilterOperators: Record<string, string[]> = {
    String:         ["_eq", "_neq", "_contains", "_starts_with", "_ends_with", "_null"],
    Int:            NUMERIC_OPERATORS,
    Float:          NUMERIC_OPERATORS,
    Short:          NUMERIC_OPERATORS,
    Byte:           NUMERIC_OPERATORS,
    BigInt:         NUMERIC_OPERATORS,
    Decimal:        NUMERIC_OPERATORS,
    Boolean:        ["_eq", "_null"],
    DateTime:       NUMERIC_OPERATORS,
    DateTimeOffset: NUMERIC_OPERATORS,
};

/** Column types whose filter bounds are timestamps, so a date-only bound must be widened to a day. */
const timestampTypes = new Set(["DateTime", "DateTimeOffset"]);

const graphQlNamePattern = /^[_A-Za-z][_0-9A-Za-z]*$/;
const graphQlTypePattern = /^[_A-Za-z][_0-9A-Za-z]*!?$/;
const columnFilterOperatorSet = new Set(Object.values(columnFilterOperators).flat());

export function isGraphQlName(value: unknown): value is string {
    return typeof value === "string" && graphQlNamePattern.test(value);
}

export function assertGraphQlName(value: unknown, kind: string): asserts value is string {
    if (!isGraphQlName(value)) {
        throw new Error(`Invalid GraphQL ${kind}: ${String(value)}`);
    }
}

function isGraphQlType(value: unknown): value is string {
    return typeof value === "string" && graphQlTypePattern.test(value);
}

function isFilterOperator(value: unknown, paramType: string): value is string {
    return typeof value === "string" && getFilterOperators(paramType).includes(value);
}

export function getFilterOperators(paramType: string): string[] {
    const baseType = paramType.replace("!", "");
    return columnFilterOperators[baseType] ?? columnFilterOperators.String;
}

function noFilter(): FilterResult {
    return { variables: {}, param: "", filterText: "", error: null };
}

function filterFailed(error: string): FilterResult {
    return { variables: {}, param: "", filterText: "", error };
}

/**
 * Parses the header filter (a JSON `[column, operator, value, type]` tuple carried
 * in the `filter` URL param) into a GraphQL predicate.
 *
 * A rejected filter reports an error rather than degrading to "no filter". Silently
 * dropping it left the grid showing the UNFILTERED table while the filter UI still
 * advertised the predicate as active — and select-all + Delete then operated over a
 * result set that was not the one the user believed they had narrowed to.
 */
export function parseTableFilterString(filterString: string): FilterResult {
    if (!filterString) return noFilter();
    let parsed: unknown;
    try {
        parsed = JSON.parse(filterString);
    } catch {
        return filterFailed("The filter in the URL is not valid JSON.");
    }
    if (!Array.isArray(parsed) || parsed.length < 4) {
        return filterFailed("The filter in the URL is malformed.");
    }
    const [column, action, value, type] = parsed as unknown[];
    if (!isGraphQlName(column)) {
        return filterFailed(`Filter column '${String(column)}' is not a valid column name.`);
    }
    if (!isGraphQlType(type)) {
        return filterFailed(`Filter type '${String(type)}' is not a valid type name.`);
    }
    if (!isFilterOperator(action, type)) {
        return filterFailed(`Filter operator '${String(action)}' is not supported for ${String(type)} column '${String(column)}'.`);
    }
    return {
        variables: { filter: value },
        param: `, $filter: ${type}`,
        filterText: `{${column}: {${action}: $filter} }`,
        error: null,
    };
}

export function getRowPkValue(row: RowData, table: Table, rowIndex = 0): string {
    // Delegate to the shared rowIdOf so the placeholder for a PK-less table is the
    // same `row-${rowIndex}` both sides use (finding: getRowPkValue previously emitted
    // an empty/`row.id` segment while rowIdOf emitted `row-${rowIndex}`, so links built
    // by the two never round-tripped). rowIdOf also route-encodes single/composite
    // keys, matching the segment decoding in parsePkRoute.
    return rowIdOf(row as Record<string, unknown>, table, rowIndex);
}

/**
 * The GraphQL type a filter/lookup VARIABLE must be declared as for a column of
 * this paramType. It has to be the column's own scalar: the variable is used in a
 * `FilterType<T>Input` field position, and GraphQL rejects any variable whose
 * declared type is not that exact scalar — "Variable '$cf_x_0' of type 'String'
 * used in position expecting type 'DateTime'" fails the WHOLE document, so one
 * mistyped column empties the grid rather than degrading that one predicate.
 *
 * BigInt/Decimal values travel as decimal STRINGS (coerceForGql's default branch
 * stringifies): a JSON number is a double, which rounds a bigint key past 2^53 and
 * drops exact-decimal precision. The server's BigInt/Decimal scalars accept the
 * string form for exactly this reason — see ExactNumericScalars.cs.
 */
export function getGraphQlType(paramType: string): string {
    const baseType = paramType.replace("!", "");
    switch (baseType) {
        case "Int":
        case "Float":
        case "Short":
        case "Byte":
        case "Boolean":
        case "BigInt":
        case "Decimal":
        case "DateTime":
        case "DateTimeOffset":
            return baseType;
        default: return "String";
    }
}

const DATE_ONLY = /^\d{4}-\d{2}-\d{2}$/;


/** `-780` (minutes behind UTC, as getTimezoneOffset reports it) → `"+13:00"`. */
function formatUtcOffset(offsetMinutes: number): string {
    const sign = offsetMinutes <= 0 ? "+" : "-";
    const abs = Math.abs(offsetMinutes);
    const hh = String(Math.floor(abs / 60)).padStart(2, "0");
    const mm = String(abs % 60).padStart(2, "0");
    return `${sign}${hh}:${mm}`;
}

/**
 * Widens a date-only bound ("2024-02-01") to the instant at the requested edge of
 * that LOCAL day, in the ISO-8601 form the server's DateTime/DateTimeOffset scalars
 * require.
 *
 * The date filter's control is `<input type="date">`, so its value carries no time
 * — but the column it filters is a timestamp. Sent verbatim the value is not
 * ISO-8601-complete and the scalar rejects it outright; sent as midnight it silently
 * means "the first instant of the day", so "on or before 1 Feb" excludes everything
 * recorded ON 1 Feb. Each operator therefore takes the edge that makes it true for
 * the whole day.
 */
export function dayBoundary(
    date: string,
    edge: "start" | "end",
    gqlType: string,
    offsetMinutes?: number,
): string {
    const time = edge === "start" ? "00:00:00.000" : "23:59:59.999";
    const stamp = `${date}T${time}`;
    if (gqlType !== "DateTimeOffset") return stamp;
    const offset = offsetMinutes ?? new Date(`${date}T00:00:00`).getTimezoneOffset();
    return `${stamp}${formatUtcOffset(offset)}`;
}

/**
 * The wire form of one column filter: the operator actually emitted and its
 * value(s). Everything passes through unchanged except a date-only bound on a
 * timestamp column, where the operator may widen to a day RANGE (`_eq` on a
 * timestamp column matches nothing otherwise — no row is recorded at exactly
 * midnight).
 */
export function toWireFilter(
    operator: string,
    value: unknown,
    gqlType: string,
    offsetMinutes?: number,
): { operator: string; value: unknown } {
    if (!timestampTypes.has(gqlType)) return { operator, value };

    const start = (v: unknown) => dayBoundary(String(v), "start", gqlType, offsetMinutes);
    const end = (v: unknown) => dayBoundary(String(v), "end", gqlType, offsetMinutes);
    const isDateOnly = (v: unknown) => typeof v === "string" && DATE_ONLY.test(v);

    if (operator === "_between") {
        const range = Array.isArray(value) ? value : null;
        if (!range || range.length !== 2) return { operator, value };
        return {
            operator,
            value: [
                isDateOnly(range[0]) ? start(range[0]) : range[0],
                isDateOnly(range[1]) ? end(range[1]) : range[1],
            ],
        };
    }

    if (!isDateOnly(value)) return { operator, value };

    switch (operator) {
        // "on that day" / "not on that day" are day-wide ranges, not instants.
        case "_eq": return { operator: "_between", value: [start(value), end(value)] };
        case "_neq": return { operator: "_nbetween", value: [start(value), end(value)] };
        // After/on-or-before include the whole day; on-or-after/before start at it.
        case "_gt":
        case "_lte": return { operator, value: end(value) };
        default: return { operator, value: start(value) };
    }
}

export function buildColumnFilters(columnFilters: ColumnFiltersState, table: Table): ColumnFilterResult {
    const variables: Record<string, unknown> = {};
    const params: string[] = [];
    const filterTexts: string[] = [];
    const errors: string[] = [];

    for (let i = 0; i < columnFilters.length; i++) {
        const cf = columnFilters[i];
        const filterValue = cf.value as ColumnFilterValue;
        // An empty value is "the user hasn't typed a bound yet", not a failure —
        // the filter row exists but asserts nothing, so skipping it is honest.
        if (filterValue.value === undefined || filterValue.value === null || filterValue.value === "") continue;

        const col = table.columns.find((c) => c.name === cf.id);
        if (!col) {
            errors.push(`Column '${String(cf.id)}' is not on this table.`);
            continue;
        }
        if (!isGraphQlName(cf.id)) {
            errors.push(`Column '${String(cf.id)}' is not a valid column name.`);
            continue;
        }
        if (!isFilterOperator(filterValue.operator, col.paramType)) {
            errors.push(`Operator '${String(filterValue.operator)}' is not supported for column '${cf.id}'.`);
            continue;
        }

        // Suffix with the filter index so two filters on the same column (e.g.
        // _gte and _lte) get distinct variable names instead of colliding into
        // one duplicated GraphQL variable.
        const varName = `cf_${cf.id}_${i}`;
        const gqlType = getGraphQlType(col.paramType);

        if (filterValue.operator === "_null") {
            filterTexts.push(`{${cf.id}: {_null: ${filterValue.value ? "true" : "false"}}}`);
            continue;
        }

        // Validated as the UI operator above; emitted as the WIRE operator here,
        // which for a date-only bound on a timestamp column may widen to a range.
        const wire = toWireFilter(filterValue.operator, filterValue.value, gqlType);

        if (wire.operator === "_between" || wire.operator === "_nbetween") {
            const range = wire.value as [unknown, unknown];
            if (!Array.isArray(range) || range.length !== 2) {
                errors.push(`The range filter on column '${cf.id}' needs exactly two bounds.`);
                continue;
            }
            const loVar = `${varName}_lo`;
            const hiVar = `${varName}_hi`;
            variables[loVar] = range[0];
            variables[hiVar] = range[1];
            params.push(`$${loVar}: ${gqlType}`, `$${hiVar}: ${gqlType}`);
            filterTexts.push(`{${cf.id}: {${wire.operator}: [$${loVar}, $${hiVar}]}}`);
            continue;
        }

        variables[varName] = wire.value;
        params.push(`$${varName}: ${gqlType}`);
        filterTexts.push(`{${cf.id}: {${wire.operator}: $${varName}}}`);
    }

    return { variables, params, filterTexts, errors };
}

/**
 * Every reason the requested filters could not be fully applied, header filter
 * first. Empty means the built predicate matches exactly what the filter UI is
 * advertising, which is the only condition under which the grid query may run.
 */
export function collectFilterErrors(
    table: Table,
    filterString: string,
    columnFilters: ColumnFiltersState,
): string[] {
    const header = parseTableFilterString(filterString);
    const columns = buildColumnFilters(columnFilters, table);
    return header.error ? [header.error, ...columns.errors] : columns.errors;
}

export function serializeColumnFilters(columnFilters: ColumnFiltersState): string {
    if (columnFilters.length === 0) return "";
    return JSON.stringify(columnFilters.map((cf) => [cf.id, (cf.value as ColumnFilterValue).operator, (cf.value as ColumnFilterValue).value]));
}

export function deserializeColumnFilters(raw: string): ColumnFiltersState {
    try {
        if (!raw) return [];
        const parsed = JSON.parse(raw);
        if (!Array.isArray(parsed)) return [];
        return parsed
            .filter(isSerializedColumnFilter)
            .map(([id, operator, value]) => ({ id, value: { operator, value } as ColumnFilterValue }));
    } catch {
        return [];
    }
}

function isSerializedColumnFilter(value: unknown): value is [string, string, unknown] {
    if (!Array.isArray(value) || value.length !== 3) return false;
    const [id, operator] = value;
    return isGraphQlName(id) && typeof operator === "string" && columnFilterOperatorSet.has(operator);
}

/**
 * Returns the GraphQL type of the first PK column. For composite PKs use {@link getPkTypes}.
 */
export function getPkType(table: Table): string {
    return getPkTypes(table)[0]?.gqlType ?? "Int";
}

/**
 * Returns one {name, gqlType} per primary-key column in declaration order.
 * Empty array if the table has no primary keys.
 */
export function getPkTypes(table: Table): PkTypeInfo[] {
    const keys = table.primaryKeys ?? [];
    if (keys.length === 0) return [];
    const byName = new Map(table.columns.map((c) => [c.name, c] as const));
    return keys.map((pk) => ({
        name: pk,
        gqlType: byName.get(pk)?.paramType?.replace("!", "") ?? "String",
    }));
}

/**
 * Picks the best index-served sort column for a table with no usable primary
 * key: the leading key column of the clustered index if any (it IS the row
 * order — sorting by it is free), else of a unique index, else of any index.
 * Only sortable columns qualify — an index leading on a LOB/JSON column can't
 * drive the grid's ORDER BY. Returns null when no index leads on a sortable,
 * visible column; the caller falls back to its positional heuristic. Sorting a
 * large table by an unindexed column re-sorts every row on every page turn, so
 * this choice is a correctness-of-experience matter, not a micro-optimization.
 */
export function pickIndexedSortColumn(
    table: Table,
    isSortable: (col: Column) => boolean = () => true,
): string | null {
    const indexes = table.indexes ?? [];
    if (indexes.length === 0) return null;
    const byName = new Map(table.columns.map((c) => [c.name, c] as const));
    const leadOf = (ix: { columns: string[] }): string | null => {
        const lead = ix.columns[0];
        const col = lead ? byName.get(lead) : undefined;
        return col && isSortable(col) ? col.name : null;
    };
    for (const pick of [
        (ix: TableIndex) => ix.isClustered,
        (ix: TableIndex) => ix.isUnique,
        () => true,
    ]) {
        for (const ix of indexes) {
            if (!pick(ix)) continue;
            const lead = leadOf(ix);
            if (lead) return lead;
        }
    }
    return null;
}

/**
 * Builds the variables dict that accompanies a buildQuery result for a single-record lookup.
 * - Single PK: returns `{ id: <coerced value> }` matching the `$id` variable in the query.
 * - Composite PK: returns `{ pk_${col1}: ..., pk_${col2}: ... }` matching the composite form.
 *
 * `idRoute` is the same string passed to buildQuery's `id` parameter — for composite PKs
 * it is a route-encoded string produced by {@link encodePkRoute}.
 */
export function buildPkEqVariables(idRoute: string, table: Table): Record<string, unknown> {
    const pkTypes = getPkTypes(table);
    if (pkTypes.length <= 1) {
        // The router captures `:id` still percent-encoded (usePath matchPath does
        // not decode segments), so a single-PK route value containing a space,
        // "%", or "/" arrives escaped. Decode it here — exactly as parsePkRoute
        // does for the composite path — before coercing, otherwise a String PK
        // filter is built from the escaped text and never matches the row.
        return { id: coerceForGql(decodePkPart(idRoute), getPkType(table)) };
    }
    const parsed = parsePkRoute(idRoute, table);
    if (!parsed) return {};
    const result = buildPkEqFilter(parsed, table);
    return result?.variables ?? {};
}

/**
 * Builds a single-row lookup query keyed by a primary-key equality filter
 * (produced by {@link buildPkEqFilter}). The row comes back under the `value`
 * alias: `{ value: { data: [row] } }`. Used by the content panel to re-read a
 * row fresh before echoing it back in an update.
 */
export function buildSingleRowQuery(
    table: Pick<Table, 'name'>,
    pkEq: Pick<PkEqFilterResult, 'filterText' | 'params'>,
    fields: readonly string[],
): string {
    assertGraphQlName(table.name, 'single-row table name');
    for (const field of fields) {
        assertGraphQlName(field, 'single-row selection field');
    }
    // GraphQL forbids an empty `()` variable-definition list — omit it entirely
    // when the filter carries no params.
    const paramDecls = pkEq.params.length > 0 ? `(${pkEq.params.join(', ')})` : '';
    return `query GetSingleRow_${table.name}${paramDecls} { value: ${table.name}(filter: ${pkEq.filterText}) { data { ${fields.join(' ')} } } }`;
}

/**
 * Builds ONE query fetching fresh snapshots of several rows by primary key —
 * `{or: [pkEq, pkEq, ...]}` — so a bulk edit can echo current server state for
 * every selected row in a single round trip (the multi-row analogue of
 * {@link buildSingleRowQuery}'s fresh re-read). Rows come back under the `value`
 * alias. Every key column of every row participates (composite-safe), values
 * coerce through the same GraphQL-type rules as single-row PK filters (BigInt
 * keys stay strings), and identifiers are asserted before any text is built.
 */
export function buildRowsByPkQuery(
    table: Pick<Table, 'name' | 'primaryKeys' | 'columns'>,
    pks: readonly PkFilter[],
    fields: readonly string[],
): { query: string; variables: Record<string, unknown> } | null {
    assertGraphQlName(table.name, 'rows-by-pk table name');
    for (const field of fields) assertGraphQlName(field, 'rows-by-pk selection field');
    const keys = table.primaryKeys ?? [];
    if (keys.length === 0 || pks.length === 0 || fields.length === 0) return null;
    for (const key of keys) assertGraphQlName(key, 'rows-by-pk key column');

    const columnByName = new Map((table.columns ?? []).map((c) => [c.name, c] as const));
    const params: string[] = [];
    const variables: Record<string, unknown> = {};
    const rowClauses: string[] = [];
    pks.forEach((pk, i) => {
        const clauses: string[] = [];
        for (const key of keys) {
            const gqlType = gqlTypeOf(columnByName.get(key));
            const varName = `pk${i}_${key}`;
            variables[varName] = coerceForGql(pk[key], gqlType);
            params.push(`$${varName}: ${gqlType}`);
            clauses.push(`{${key}: {_eq: $${varName}}}`);
        }
        rowClauses.push(clauses.length === 1 ? clauses[0] : `{and: [${clauses.join(', ')}]}`);
    });
    const filterText = rowClauses.length === 1 ? rowClauses[0] : `{or: [${rowClauses.join(', ')}]}`;
    const query = `query GetRowsByPk_${table.name}(${params.join(', ')}) { value: ${table.name}(filter: ${filterText} limit: ${pks.length}) { data { ${fields.join(' ')} } } }`;
    return { query, variables };
}

/**
 * Grid multi-join cells only show a count badge and a preview list of up to 10
 * labels. Capping the nested fetch at 11 rows (10 preview + 1 to prove "more")
 * and reading the real count from the paged `total` avoids pulling every child
 * row for every parent row on the page — the dominant payload cost on parent
 * tables.
 */
const MULTIJOIN_PREVIEW_LIMIT = 11;

function buildMultiJoinFields(schema: SchemaContextValue, multiJoins: Join[]): string {
    return multiJoins
        .map((j) => {
            const joinSchema = schema.findTable(j.destinationTable);
            const labelCol = joinSchema?.labelColumn ?? 'id';
            const destPks = joinSchema?.primaryKeys?.length ? joinSchema.primaryKeys : ['id'];
            const fields = [...destPks];
            if (labelCol && !fields.includes(labelCol)) fields.push(labelCol);
            // Multi-joins return a paged type (`<table>_paged`), so the row
            // selection must live under `data {}` — selecting fields directly
            // fails server validation (FIELDS_ON_CORRECT_TYPE). `total` carries
            // the true count so the badge stays correct despite the row cap.
            return `${j.fieldName ?? j.destinationTable}(limit: ${MULTIJOIN_PREVIEW_LIMIT}) { total data { ${fields.join(' ')} } }`;
        })
        .join(' ');
}

/**
 * The standard paged grid query: `<table>(sort limit offset <filterClause>) {
 * total offset limit data { <fields> } }`. `param` is the extra variable
 * declarations (leading with `, ` when non-empty); `filterClause` is either an
 * empty string, a `filter: {...}` clause, or a flat FK `filter: {...}` — the
 * caller decides. Shared by the list, by-id, and flat-FK drill query builders.
 */
function queryEnvelope(
    table: Pick<Table, 'name' | 'graphQlName'>,
    param: string,
    filterClause: string,
    fields: string,
): string {
    return `query Get${table.name}($sort: [${table.graphQlName}SortEnum!], $limit: Int, $offset: Int ${param}) { ${table.name}(sort: $sort limit: $limit offset: $offset ${filterClause}) { total offset limit data {${fields}}}}`;
}

/**
 * Build the SELECT field list (scalar columns + single-join FK blocks) for a
 * table's grid, excluding heavy blob/long-text payloads. FK columns emit a
 * nested block anchored on the FIRST source column of a composite FK.
 */
function buildDataColumns(table: Table, schema: SchemaContextValue, tableSchema: Table): string {
    // For composite FKs, anchor the nested sub-query on the FIRST source column so
    // we only emit one FK block. The other member columns render as plain scalars
    // (their values still come back on the parent row — useful for rebuilding a
    // composite-eq filter on the destination later).
    const emittedJoinSources = new Set<string>();
    return table.columns
        .filter((x: Column) => (x as ColumnWithJoin)?.joinTable === undefined)
        // Exclude large-value (LOB) columns from the grid SELECT: the grid only shows
        // a size/preview badge for them, and pulling the full payload for every row on
        // the page is the dominant transfer cost. The viewer fetches the real value on
        // demand (mirrors the multi-join preview cap above). Large-ness is the server's
        // dialect-decided isLargeValue flag — Postgres text / SQLite TEXT are ordinary
        // strings and stay selected. PK columns are always kept — they carry row
        // identity and drive links.
        .filter((x: Column) => x.isPrimaryKey || !isLargeValueColumn(x))
        .map((x: Column): ColumnWithJoin => {
            const joinTable = tableSchema.singleJoins.find((j: Join) => j.sourceColumnNames?.[0] === x.name);
            if (!joinTable) return x;
            const joinSchema = schema.findTable(joinTable.destinationTable);
            const labelColumn = joinSchema?.labelColumn ?? "id";
            return {...x, joinTable, joinLabelColumn: labelColumn};
        })
        .map((x: ColumnWithJoin) => {
            if (x?.joinTable) {
                emittedJoinSources.add(x.name);
                const joinSchema = schema.findTable(x.joinTable.destinationTable);
                const destPks = joinSchema?.primaryKeys?.length
                    ? joinSchema.primaryKeys
                    : x.joinTable.destinationColumnNames;
                const labelCol = x.joinLabelColumn ?? 'id';
                const labelField = labelCol && !destPks.includes(labelCol)
                    ? ` label: ${labelCol}`
                    : '';
                // Select by the RELATIONSHIP field, falling back to the destination
                // table name when the server sent no alias. A table with two foreign
                // keys to the same target has one field per key (billing_address,
                // shipping_address) and none named for the table, so asking by table
                // name fails the whole grid query with "Cannot query field
                // 'addresses' on type 'orders'". Same fallback the multi-join
                // selection already uses.
                const joinField = x.joinTable.fieldName ?? x.joinTable.destinationTable;
                if (destPks.length === 1) {
                    // Single-PK destination — keep the legacy `id: <destCol>` alias
                    // so cell renderers can read `joined.id` without composite awareness.
                    return `${x.name} ${joinField} { id: ${destPks[0]}${labelField} }`;
                }
                // Composite-PK destination — emit every PK column verbatim so callers
                // can recompose a composite route via rowIdOf.
                return `${x.name} ${joinField} { ${destPks.join(' ')}${labelField} }`;
            }
            return x.name;
        })
        .join(' ');
}

/**
 * MODEL B parent→child drill-down. Traverses the PARENT and selects the child
 * collection (paged) field; the server scopes child rows to this parent
 * (including any polymorphic discriminator), so the client only matches on the
 * parent PK. Simple single-column FKs instead query the child directly with a
 * flat FK filter. Returns null when no parent multi-join targets this child —
 * refusing to emit an unscoped query that would leak the whole table.
 */
function buildDrillQuery(
    table: Table,
    schema: SchemaContextValue,
    tableSchema: Table,
    dataColumns: string,
    allFields: string,
    filterTable?: string,
    filterColumn?: string,
    forExport = false,
): string | null {
    const drill = resolveDrillDown(table, schema, filterTable, filterColumn);
    if (drill && canFlatFilterDrill(drill.childJoin)) {
        // Simple single-column FK: query the child table directly with a flat
        // FK filter — a "parent grid with a filter applied" rather than MODEL
        // B's nested parent traversal. The grid's own table is the query root,
        // so paging/sort drive it natively and the response keeps the standard
        // `{ <table>: { data } }` shape (no unwrap). Global header/column
        // filters are keyed off the MAIN grid and must not bleed into this
        // child, so only the FK predicate is applied.
        const fkCol = drill.childJoin.destinationColumnNames[0];
        const parentPkType = getPkTypes(drill.parentTable)[0]?.gqlType ?? "Int";
        const param = `, $id: ${parentPkType}`;
        const flatFilter = `filter: { ${fkCol}: { _eq: $id } }`;
        const flatMultiJoinFields = forExport ? '' : buildMultiJoinFields(
            schema,
            tableSchema.multiJoins.filter((join) => !sameJoin(join, drill.childJoin)),
        );
        const flatFields = flatMultiJoinFields ? `${dataColumns} ${flatMultiJoinFields}` : dataColumns;
        return queryEnvelope(table, param, flatFilter, flatFields);
    }
    if (drill) {
        // The header filter + column filters are global URL params keyed off the
        // MAIN (first) grid's table. Drill child grids show a different table
        // scoped to one parent row, so those filters must NOT bleed into the
        // child. Drop the grid filter args here (and the $filter/$cf param decls
        // they require — declaring unused GraphQL variables is an error).
        const childField = `${drill.childField}(limit: $limit offset: $offset sort: $sort) { total offset limit data {${allFields}} }`;

        const parentPkTypes = getPkTypes(drill.parentTable);
        // A parent with NO declared key cannot be addressed by id. Guessing a
        // column called "id" built a filter against a column that may not exist,
        // and on a table that happens to have one it scopes the child grid by the
        // wrong column. Refuse, like the unresolvable-drill branch above.
        if (parentPkTypes.length === 0) return null;
        let param: string;
        let parentFilter: string;
        if (parentPkTypes.length === 1) {
            const parentPk = parentPkTypes[0].name;
            const parentPkType = parentPkTypes[0].gqlType;
            param = `, $id: ${parentPkType}`;
            parentFilter = `{ ${parentPk}: { _eq: $id}}`;
        } else {
            // Composite parent PK — one $pk_${name} variable per parent PK column.
            const pkParamDecls = parentPkTypes.map((t) => `$pk_${t.name}: ${t.gqlType}`).join(', ');
            param = `, ${pkParamDecls}`;
            const clauses = parentPkTypes.map((t) => `{${t.name}: {_eq: $pk_${t.name}}}`);
            parentFilter = `{and: [${clauses.join(', ')}]}`;
        }
        return `query Get${table.name}($sort: [${table.graphQlName}SortEnum!], $limit: Int, $offset: Int ${param}) { ${drill.parentTable.name}(filter: ${parentFilter}) { data { ${childField} } } }`;
    }
    // A drill was explicitly requested (id + filterTable/filterColumn) but the
    // parent→child relationship could not be resolved — e.g. no parent multi-join
    // targets this child, or a polymorphic/ambiguous join declined to guess.
    // Falling through here previously emitted the UNFILTERED full-table query, so a
    // "children of parent X" panel showed every row and a select-all + delete hit
    // unrelated rows. Refuse to emit an unscoped query: return null so the caller
    // shows an empty "relationship unavailable" grid instead of leaking the table.
    return null;
}

/**
 * Single-record lookup keyed by the table's own primary key (composite-aware).
 */
function buildByIdQuery(table: Table, tableSchema: Table, allFields: string): string | null {
    const pkTypes = getPkTypes(tableSchema);
    // Same refusal as the drill path: a keyless table has no by-id lookup, and
    // guessing a column named "id" produces a filter on a column that need not
    // exist. rowIdOf already emits `row-<index>` placeholders for such tables, so
    // no caller can supply a meaningful id here anyway.
    if (pkTypes.length === 0) return null;
    let param: string;
    let filterText: string;
    if (pkTypes.length === 1) {
        const pkType = getPkType(tableSchema);
        const primaryKey = pkTypes[0].name;
        param = `, $id: ${pkType}`;
        filterText = `{ ${primaryKey}: { _eq: $id}}`;
    } else {
        // Composite PK — one $pk_${name} variable per column, wrapped in and
        param = `, ${pkTypes.map((t) => `$pk_${t.name}: ${t.gqlType}`).join(', ')}`;
        const clauses = pkTypes.map((t) => `{${t.name}: {_eq: $pk_${t.name}}}`);
        filterText = `{and: [${clauses.join(', ')}]}`;
    }
    return queryEnvelope(table, param, `filter: ${filterText}`, allFields);
}

/**
 * Standard paged list query combining the header filter (`filterString`) and the
 * per-column filters into a single (optionally `and`-wrapped) predicate.
 */
function buildListQuery(
    table: Table,
    filterString: string,
    columnFilters: ColumnFiltersState,
    allFields: string,
): string {
    let { param, filterText } = parseTableFilterString(filterString);

    const { params: cfParams, filterTexts: cfFilterTexts } = buildColumnFilters(columnFilters, table);
    if (cfParams.length > 0) {
        param += cfParams.map((p) => `, ${p}`).join("");
    }

    const allFilterTexts: string[] = [];
    if (filterText) allFilterTexts.push(filterText);
    allFilterTexts.push(...cfFilterTexts);

    if (allFilterTexts.length > 1) {
        filterText = `{and: [${allFilterTexts.join(", ")}]}`;
    } else if (allFilterTexts.length === 1) {
        filterText = allFilterTexts[0];
    } else {
        filterText = "";
    }

    return queryEnvelope(table, param, filterText ? `filter: ${filterText}` : '', allFields);
}

/**
 * Columns included in a full-data export: every column EXCEPT binary payloads
 * (non-PK), which have no useful CSV/JSON representation. Long-text columns ARE
 * included — export means the full data, not the grid's preview projection.
 * Single source of truth for export headers, fields, and the export query's
 * SELECT list so the three cannot drift.
 */
export function exportableColumns(table: Table): Column[] {
    return table.columns.filter((c) => c.isPrimaryKey || !isBinaryDbType(c.dbType));
}

export interface BuildQueryOptions {
    /**
     * `grid` (default): the on-screen projection — large values excluded, FK label
     * blocks and multi-join count/preview blocks included.
     * `export`: the full-data projection — every exportable scalar column (long
     * text included, binary excluded), and NO join/multi-join blocks: exports
     * project scalar fields only, so fetching label/preview blocks for every row
     * would be pure transfer waste.
     */
    fields?: 'grid' | 'export';
}

export function buildQuery(
    table: Table,
    schema: SchemaContextValue,
    filterString: string,
    columnFilters: ColumnFiltersState,
    id?: string,
    filterTable?: string,
    filterColumn?: string,
    options?: BuildQueryOptions,
): string | null {
    if (!table || !schema?.data) return null;
    const tableSchema = schema.findTable(table.graphQlName);
    if (!tableSchema) return null;

    const forExport = options?.fields === 'export';
    const dataColumns = forExport
        ? exportableColumns(table).map((c) => c.name).join(' ')
        : buildDataColumns(table, schema, tableSchema);
    const multiJoinFields = forExport ? '' : buildMultiJoinFields(schema, tableSchema.multiJoins);
    const allFields = multiJoinFields ? `${dataColumns} ${multiJoinFields}` : dataColumns;

    if (id && (filterColumn || filterTable)) {
        return buildDrillQuery(table, schema, tableSchema, dataColumns, allFields, filterTable, filterColumn, forExport);
    }

    if (id && !filterTable && !filterColumn) {
        return buildByIdQuery(table, tableSchema, allFields);
    }

    // Refuse to emit a list query whose predicate is narrower than the filters the
    // UI claims are active — that query returns rows the user believes are filtered
    // out, and select-all + Delete over it destroys the wrong rows. Same refusal
    // shape as the unresolvable-drill branch above: null, and the caller reports it.
    if (collectFilterErrors(table, filterString, columnFilters).length > 0) return null;

    return buildListQuery(table, filterString, columnFilters, allFields);
}

export interface DrillDownTarget {
    /** The parent table whose row owns the child collection. */
    parentTable: Table;
    /** The child collection field name on the parent type. */
    childField: string;
    /** The parent multi-join that owns the child collection. Lets callers
     *  decide whether the drill can use a flat FK filter (simple single-column
     *  FK) or must keep the MODEL B parent traversal (composite/polymorphic). */
    childJoin: Join;
}

/**
 * A drill can render as a flat root-filtered child grid — `childTable(filter:
 * { fk: { _eq: $id } })`, i.e. a "parent grid with a filter applied" — only for
 * a simple single-column, non-polymorphic FK. Composite FKs have no single
 * filter column and polymorphic links need the server-injected discriminator;
 * both must keep MODEL B's parent traversal.
 */
export function canFlatFilterDrill(childJoin: Join): boolean {
    return !isComposite(childJoin)
        && childJoin.isPolymorphic !== true
        && (childJoin.destinationColumnNames?.length ?? 0) === 1;
}

function sameJoin(left: Join, right: Join): boolean {
    return (left.fieldName ?? left.destinationTable) === (right.fieldName ?? right.destinationTable)
        && left.destinationTable === right.destinationTable
        && (left.destinationColumnNames ?? []).join('\0') === (right.destinationColumnNames ?? []).join('\0')
        && (left.sourceColumnNames ?? []).join('\0') === (right.sourceColumnNames ?? []).join('\0');
}

/**
 * Resolve the parent table + child collection field for a parent→child
 * related-records drill-down. The child is `table`; the parent is `filterTable`
 * (or the destination of `table`'s single-join named `filterTable` when the
 * relationship is described from the child side). `filterColumn` (the child
 * destination column) disambiguates when the parent has several multi-joins to
 * the same child. Returns `null` when no parent multi-join targets the child —
 * the caller then falls back to the standard (non-traversal) query.
 */
export interface PagedResult {
    data: unknown[];
    total: number;
    offset: number;
    limit: number;
}

const EMPTY_PAGE: PagedResult = { data: [], total: 0, offset: 0, limit: 0 };

/**
 * Unwrap the nested paged child collection from a MODEL B drill-down response.
 *
 * The query shape is `{ <parentField>: { data: [ { <childField>: <paged> } ] } }`.
 * Returns the child's `{total,offset,limit,data}` page, or an empty page when the
 * parent row was not found (no children / unknown id).
 */
export function unwrapDrillDownPage(
    response: Record<string, unknown> | null | undefined,
    parentField: string,
    childField: string,
): PagedResult {
    const parent = response?.[parentField] as { data?: unknown[] } | undefined;
    const parentRow = parent?.data?.[0] as Record<string, unknown> | undefined;
    const page = parentRow?.[childField] as PagedResult | undefined;
    if (!page || !Array.isArray(page.data)) return EMPTY_PAGE;
    return page;
}

export function resolveDrillDown(
    table: Table,
    schema: SchemaContextValue,
    filterTable?: string,
    filterColumn?: string,
): DrillDownTarget | null {
    const parentName = filterTable
        ?? schema.findTable(table.graphQlName)?.singleJoins.find((j: Join) => j.destinationTable === filterColumn)?.destinationTable;
    if (!parentName) return null;
    const parentTable = schema.findTable(parentName);
    if (!parentTable) return null;
    const childJoin = resolveChildJoin(parentTable.multiJoins, table.graphQlName, filterColumn);
    if (!childJoin) return null;
    return { parentTable, childField: childFieldName(childJoin), childJoin };
}
