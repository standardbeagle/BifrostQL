/**
 * Pure functions for building GraphQL queries and filters.
 * Extracted from useDataTable for testability.
 */

import type { Table, Column, Join, Schema } from '../types/schema';
import type { ColumnFiltersState } from '@tanstack/react-table';
import { rowIdOf, buildPkEqFilter, parsePkRoute, decodePkPart, encodeRouteParts, type PkEqFilterResult } from './row-id';
import { coerceForGql } from './fk';
import { resolveChildJoin, childFieldName } from './polymorphic';
import { isComposite } from './fk';

export interface FilterResult {
    variables: Record<string, unknown>;
    param: string;
    filterText: string;
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
}

export interface PkTypeInfo {
    name: string;
    gqlType: string;
}

interface RowData {
    id?: number | string | null;
    [key: string]: unknown;
}

const columnFilterOperators: Record<string, string[]> = {
    String:   ["_eq", "_neq", "_contains", "_starts_with", "_ends_with", "_null"],
    Int:      ["_eq", "_neq", "_gt", "_gte", "_lt", "_lte", "_between", "_null"],
    Float:    ["_eq", "_neq", "_gt", "_gte", "_lt", "_lte", "_between", "_null"],
    Boolean:  ["_eq", "_null"],
    DateTime: ["_eq", "_neq", "_gt", "_gte", "_lt", "_lte", "_between", "_null"],
};

const graphQlNamePattern = /^[_A-Za-z][_0-9A-Za-z]*$/;
const graphQlTypePattern = /^[_A-Za-z][_0-9A-Za-z]*!?$/;

function isGraphQlName(value: unknown): value is string {
    return typeof value === "string" && graphQlNamePattern.test(value);
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

export function getFilterObj(filterString: string): FilterResult {
    try {
        if (!filterString) return { variables: {}, param: "", filterText: "" };
        const [column, action, value, type] = JSON.parse(filterString);
        if (!isGraphQlName(column) || !isGraphQlType(type) || !isFilterOperator(action, type)) {
            return { variables: {}, param: "", filterText: "" };
        }
        return { variables: { filter: value }, param: `, $filter: ${type}`, filterText: `{${column}: {${action}: $filter} }` }
    } catch {
        return { variables: {}, param: "", filterText: "" };
    }
}

export function toLocaleDate(d: string): string {
    if (!d) return "";
    const dd = new Date(d);
    if (dd.toString() === "Invalid Date") return "";
    if (dd < new Date('1973-01-01')) return "";
    return dd.toLocaleString();
}

export function getRowPkValue(row: RowData, table: Table): string {
    const keys = table.primaryKeys ?? [];
    // This value is consumed as a route segment and decoded with
    // decodeURIComponent (see parsePkRoute), so the single-key case must
    // route-encode too — a raw value containing "%", "/", or "::" would
    // otherwise produce a broken link that never round-trips.
    if (keys.length === 0) return encodeRouteParts([row?.id]);
    if (keys.length === 1) return encodeRouteParts([row?.[keys[0]]]);
    return rowIdOf(row as Record<string, unknown>, table, 0);
}

export function getGraphQlType(paramType: string): string {
    const baseType = paramType.replace("!", "");
    switch (baseType) {
        case "Int": return "Int";
        case "Float": return "Float";
        case "Boolean": return "Boolean";
        case "DateTime": return "String";
        default: return "String";
    }
}

export function buildColumnFilters(columnFilters: ColumnFiltersState, table: Table): ColumnFilterResult {
    const variables: Record<string, unknown> = {};
    const params: string[] = [];
    const filterTexts: string[] = [];

    for (let i = 0; i < columnFilters.length; i++) {
        const cf = columnFilters[i];
        const filterValue = cf.value as ColumnFilterValue;
        if (filterValue.value === undefined || filterValue.value === null || filterValue.value === "") continue;

        const col = table.columns.find((c) => c.name === cf.id);
        if (!col) continue;
        if (!isGraphQlName(cf.id) || !isFilterOperator(filterValue.operator, col.paramType)) continue;

        // Suffix with the filter index so two filters on the same column (e.g.
        // _gte and _lte) get distinct variable names instead of colliding into
        // one duplicated GraphQL variable.
        const varName = `cf_${cf.id}_${i}`;
        const gqlType = getGraphQlType(col.paramType);

        if (filterValue.operator === "_null") {
            filterTexts.push(`{${cf.id}: {_null: ${filterValue.value ? "true" : "false"}}}`);
            continue;
        }

        if (filterValue.operator === "_between") {
            const range = filterValue.value as [unknown, unknown];
            if (!Array.isArray(range) || range.length !== 2) continue;
            const loVar = `${varName}_lo`;
            const hiVar = `${varName}_hi`;
            variables[loVar] = range[0];
            variables[hiVar] = range[1];
            params.push(`$${loVar}: ${gqlType}`, `$${hiVar}: ${gqlType}`);
            filterTexts.push(`{${cf.id}: {_between: [$${loVar}, $${hiVar}]}}`);
            continue;
        }

        variables[varName] = filterValue.value;
        params.push(`$${varName}: ${gqlType}`);
        filterTexts.push(`{${cf.id}: {${filterValue.operator}: $${varName}}}`);
    }

    return { variables, params, filterTexts };
}

export function serializeColumnFilters(columnFilters: ColumnFiltersState): string {
    if (columnFilters.length === 0) return "";
    return JSON.stringify(columnFilters.map((cf) => [cf.id, (cf.value as ColumnFilterValue).operator, (cf.value as ColumnFilterValue).value]));
}

export function deserializeColumnFilters(raw: string): ColumnFiltersState {
    try {
        if (!raw) return [];
        const parsed = JSON.parse(raw) as [string, string, unknown][];
        return parsed.map(([id, operator, value]) => ({ id, value: { operator, value } as ColumnFilterValue }));
    } catch {
        return [];
    }
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
    // GraphQL forbids an empty `()` variable-definition list — omit it entirely
    // when the filter carries no params.
    const paramDecls = pkEq.params.length > 0 ? `(${pkEq.params.join(', ')})` : '';
    return `query GetSingleRow_${table.name}${paramDecls} { value: ${table.name}(filter: ${pkEq.filterText}) { data { ${fields.join(' ')} } } }`;
}

/**
 * Grid multi-join cells only show a count badge and a preview list of up to 10
 * labels. Capping the nested fetch at 11 rows (10 preview + 1 to prove "more")
 * and reading the real count from the paged `total` avoids pulling every child
 * row for every parent row on the page — the dominant payload cost on parent
 * tables.
 */
const MULTIJOIN_PREVIEW_LIMIT = 11;

function buildMultiJoinFields(schema: Schema, multiJoins: Join[]): string {
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

export function buildQuery(
    table: Table,
    schema: Schema,
    filterString: string,
    columnFilters: ColumnFiltersState,
    id?: string,
    tableFilter?: string,
    filterColumn?: string,
): string | null {
    if (!table || !schema?.data) return null;
    const tableSchema = schema.findTable(table.graphQlName);
    if (!tableSchema) return null;
    const pkType = getPkType(tableSchema);
    const pkTypes = getPkTypes(tableSchema);
    const primaryKey = pkTypes[0]?.name ?? "id";
    let { param, filterText } = getFilterObj(filterString);

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

    // For composite FKs, anchor the nested sub-query on the FIRST source column so
    // we only emit one FK block. The other member columns render as plain scalars
    // (their values still come back on the parent row — useful for rebuilding a
    // composite-eq filter on the destination later).
    const emittedJoinSources = new Set<string>();
    const dataColumns = table.columns
        .filter((x: Column) => (x as ColumnWithJoin)?.joinTable === undefined)
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
                if (destPks.length === 1) {
                    // Single-PK destination — keep the legacy `id: <destCol>` alias
                    // so cell renderers can read `joined.id` without composite awareness.
                    return `${x.name} ${x.joinTable.destinationTable} { id: ${destPks[0]}${labelField} }`;
                }
                // Composite-PK destination — emit every PK column verbatim so callers
                // can recompose a composite route via rowIdOf.
                return `${x.name} ${x.joinTable.destinationTable} { ${destPks.join(' ')}${labelField} }`;
            }
            return x.name;
        })
        .join(' ');

    const multiJoinFields = buildMultiJoinFields(schema, tableSchema.multiJoins);
    const allFields = multiJoinFields ? `${dataColumns} ${multiJoinFields}` : dataColumns;

    if (id && (filterColumn || tableFilter)) {
        // MODEL B parent→child drill-down: traverse the PARENT and select the
        // child collection (paged) field. The server scopes the child rows to
        // this parent — including any polymorphic discriminator — so the client
        // only matches on the parent PK and never sends a discriminator. This
        // also handles multi-column FK relationships (parent PK match, no child
        // FK column needed). Grid filter/column-filters/sort/paging are pushed
        // INTO the nested child field args so server paging drives the grid.
        const drill = resolveDrillDown(table, schema, tableFilter, filterColumn);
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
            param = `, $id: ${parentPkType}`;
            const flatFilter = `filter: { ${fkCol}: { _eq: $id } }`;
            return `query Get${table.name}($sort: [${table.graphQlName}SortEnum!], $limit: Int, $offset: Int ${param}) { ${table.name}(sort: $sort limit: $limit offset: $offset ${flatFilter}) { total offset limit data {${allFields}}}}`;
        }
        if (drill) {
            // The header filter + column filters are global URL params keyed off the
            // MAIN (first) grid's table. Drill child grids show a different table
            // scoped to one parent row, so those filters must NOT bleed into the
            // child. Drop the grid filter args here (and the $filter/$cf param decls
            // they require — declaring unused GraphQL variables is an error).
            const childField = `${drill.childField}(limit: $limit offset: $offset sort: $sort) { total offset limit data {${allFields}} }`;

            const parentPkTypes = getPkTypes(drill.parentTable);
            if (parentPkTypes.length <= 1) {
                const parentPk = parentPkTypes[0]?.name ?? "id";
                const parentPkType = parentPkTypes[0]?.gqlType ?? "Int";
                param = `, $id: ${parentPkType}`;
                const parentFilter = `{ ${parentPk}: { _eq: $id}}`;
                return `query Get${table.name}($sort: [${table.graphQlName}SortEnum!], $limit: Int, $offset: Int ${param}) { ${drill.parentTable.name}(filter: ${parentFilter}) { data { ${childField} } } }`;
            }
            // Composite parent PK — one $pk_${name} variable per parent PK column.
            const pkParamDecls = parentPkTypes.map((t) => `$pk_${t.name}: ${t.gqlType}`).join(', ');
            param = `, ${pkParamDecls}`;
            const clauses = parentPkTypes.map((t) => `{${t.name}: {_eq: $pk_${t.name}}}`);
            const parentFilter = `{and: [${clauses.join(', ')}]}`;
            return `query Get${table.name}($sort: [${table.graphQlName}SortEnum!], $limit: Int, $offset: Int ${param}) { ${drill.parentTable.name}(filter: ${parentFilter}) { data { ${childField} } } }`;
        }
    }

    if (id && !tableFilter && !filterColumn) {
        if (pkTypes.length <= 1) {
            // Single PK (or no PK) — byte-identical with the legacy shape
            param = `, $id: ${pkType}`;
            filterText = `{ ${primaryKey}: { _eq: $id}}`;
        } else {
            // Composite PK — one $pk_${name} variable per column, wrapped in and
            param = `, ${pkTypes.map((t) => `$pk_${t.name}: ${t.gqlType}`).join(', ')}`;
            const clauses = pkTypes.map((t) => `{${t.name}: {_eq: $pk_${t.name}}}`);
            filterText = `{and: [${clauses.join(', ')}]}`;
        }
    }

    if (filterText) filterText = `filter: ${filterText}`;
    return `query Get${table.name}($sort: [${table.graphQlName}SortEnum!], $limit: Int, $offset: Int ${param}) { ${table.name}(sort: $sort limit: $limit offset: $offset ${filterText}) { total offset limit data {${allFields}}}}`;
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

/**
 * Resolve the parent table + child collection field for a parent→child
 * related-records drill-down. The child is `table`; the parent is `tableFilter`
 * (or the destination of `table`'s single-join named `tableFilter` when the
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
    schema: Schema,
    tableFilter?: string,
    filterColumn?: string,
): DrillDownTarget | null {
    const parentName = tableFilter
        ?? schema.findTable(table.graphQlName)?.singleJoins.find((j: Join) => j.destinationTable === filterColumn)?.destinationTable;
    if (!parentName) return null;
    const parentTable = schema.findTable(parentName);
    if (!parentTable) return null;
    const childJoin = resolveChildJoin(parentTable.multiJoins, table.graphQlName, filterColumn);
    if (!childJoin) return null;
    return { parentTable, childField: childFieldName(childJoin), childJoin };
}
