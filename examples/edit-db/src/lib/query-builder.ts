/**
 * Pure functions for building GraphQL queries and filters.
 * Extracted from useDataTable for testability.
 */

import type { Table, Column, Join, Schema } from '../types/schema';
import type { ColumnFiltersState } from '@tanstack/react-table';
import { rowIdOf, buildPkEqFilter, parsePkRoute } from './row-id';

export interface FilterResult {
    variables: Record<string, unknown>;
    param: string;
    filterText: string;
}

export interface ColumnFilterValue {
    operator: string;
    value: unknown;
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

interface ColumnWithJoin extends Column {
    joinTable?: Join;
    joinLabelColumn?: string;
}

const columnFilterOperators: Record<string, string[]> = {
    String:   ["_eq", "_neq", "_contains", "_starts_with", "_ends_with", "_null"],
    Int:      ["_eq", "_neq", "_gt", "_gte", "_lt", "_lte", "_between", "_null"],
    Float:    ["_eq", "_neq", "_gt", "_gte", "_lt", "_lte", "_between", "_null"],
    Boolean:  ["_eq", "_null"],
    DateTime: ["_eq", "_neq", "_gt", "_gte", "_lt", "_lte", "_between", "_null"],
};

export function getFilterOperators(paramType: string): string[] {
    const baseType = paramType.replace("!", "");
    return columnFilterOperators[baseType] ?? columnFilterOperators.String;
}

export function getFilterObj(filterString: string): FilterResult {
    try {
        if (!filterString) return { variables: {}, param: "", filterText: "" };
        const [column, action, value, type] = JSON.parse(filterString);
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
    if (keys.length === 0) return String(row?.id ?? "");
    if (keys.length === 1) return String(row?.[keys[0]] ?? "");
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

    for (const cf of columnFilters) {
        const filterValue = cf.value as ColumnFilterValue;
        if (filterValue.value === undefined || filterValue.value === null || filterValue.value === "") continue;

        const col = table.columns.find((c) => c.name === cf.id);
        if (!col) continue;

        const varName = `cf_${cf.id}`;
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

function coerceGqlValue(raw: unknown, gqlType: string): unknown {
    if (raw === null || raw === undefined) return null;
    switch (gqlType) {
        case "Int": {
            const n = typeof raw === "number" ? raw : Number(raw);
            return Number.isFinite(n) ? Math.trunc(n) : raw;
        }
        case "Float": {
            const n = typeof raw === "number" ? raw : Number(raw);
            return Number.isFinite(n) ? n : raw;
        }
        case "Boolean":
            if (typeof raw === "boolean") return raw;
            return raw === "true" || raw === 1;
        default:
            return String(raw);
    }
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
        return { id: coerceGqlValue(idRoute, getPkType(table)) };
    }
    const parsed = parsePkRoute(idRoute, table);
    if (!parsed) return {};
    const result = buildPkEqFilter(parsed, table);
    return result?.variables ?? {};
}

function buildMultiJoinFields(schema: Schema, multiJoins: Join[]): string {
    return multiJoins
        .map((j) => {
            const joinSchema = schema.findTable(j.destinationTable);
            const labelCol = joinSchema?.labelColumn ?? 'id';
            const destPks = joinSchema?.primaryKeys?.length ? joinSchema.primaryKeys : ['id'];
            const fields = [...destPks];
            if (labelCol && !fields.includes(labelCol)) fields.push(labelCol);
            return `${j.destinationTable} { ${fields.join(' ')} }`;
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
                return x.name + ` ${x.joinTable.destinationTable} { id: ${x.joinTable.destinationColumnNames?.[0]} label: ${x.joinLabelColumn} }`;
            }
            return x.name;
        })
        .join(' ');

    const multiJoinFields = buildMultiJoinFields(schema, tableSchema.multiJoins);
    const allFields = multiJoinFields ? `${dataColumns} ${multiJoinFields}` : dataColumns;

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
    } else if (id && (filterColumn || tableFilter)) {
        const fkColumn = filterColumn
            ?? tableSchema.singleJoins.find((j: Join) => j.destinationTable === tableFilter)?.sourceColumnNames?.[0];
        const fkCol = table.columns.find((c: Column) => c.name === fkColumn);
        const idType = fkCol ? getGraphQlType(fkCol.paramType) : "Int";

        if (fkColumn) {
            // Direct FK filter — still a single column on the child side
            param = `, $id: ${idType}` + param;
            if (filterText)
                filterText = `{and: [${filterText}, { ${fkColumn}: { _eq: $id}} ]}`;
            else
                filterText = `{ ${fkColumn}: { _eq: $id}}`;
        } else {
            // Fallback: nested filter through join using the parent table's PK
            const parentTable = schema.findTable(tableFilter!);
            const parentPkTypes = getPkTypes(parentTable!);
            if (parentPkTypes.length <= 1) {
                const parentPk = parentPkTypes[0]?.name ?? "id";
                param = `, $id: ${idType}` + param;
                const wrapped = `{ ${tableFilter}: { ${parentPk}: { _eq: $id}}}`;
                filterText = filterText ? `{and: [${filterText}, ${wrapped} ]}` : wrapped;
            } else {
                // Composite parent PK — one $pk_${name} per parent PK column
                const pkParamDecls = parentPkTypes.map((t) => `$pk_${t.name}: ${t.gqlType}`).join(', ');
                param = `, ${pkParamDecls}` + param;
                const clauses = parentPkTypes.map((t) => `{${t.name}: {_eq: $pk_${t.name}}}`);
                const nestedAnd = `{and: [${clauses.join(', ')}]}`;
                const wrapped = `{ ${tableFilter}: ${nestedAnd}}`;
                filterText = filterText ? `{and: [${filterText}, ${wrapped} ]}` : wrapped;
            }
        }
    }

    if (filterText) filterText = `filter: ${filterText}`;
    return `query Get${table.name}($sort: [${table.graphQlName}SortEnum!], $limit: Int, $offset: Int ${param}) { ${table.name}(sort: $sort limit: $limit offset: $offset ${filterText}) { total offset limit data {${allFields}}}}`;
}
