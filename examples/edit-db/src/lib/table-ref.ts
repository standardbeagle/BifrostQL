/**
 * @module lib/table-ref
 *
 * Pure query planners for the FK-dropdown data source (`useTableRef`).
 * React-free so the query text, label-column fallback, and server-search
 * decision can be unit-tested without a live server.
 */
import type { Table } from '../types/schema';
import { assertGraphQlName, getPkTypes } from './query-builder';
import { coerceForGql } from './fk';

// Cap the FK-option fetch. `limit: -1` previously pulled the entire parent
// table, which froze the edit dialog for large tables. Searches narrow
// server-side where possible, so a smaller window suffices for matches.
export const TABLE_REF_LIMIT = 1000;
export const TABLE_REF_SEARCH_LIMIT = 50;

/**
 * The column used as the display label for a table's FK dropdown rows.
 * Falls back to schema-derived names only — never a guessed literal like `id`,
 * which is a server error on tables without such a column: first primary-key
 * column, then the first column. (Taking the first PK column here is a label
 * fallback for display, not a composite-key shortcut — one column is all a
 * label can show.) `||` (not `??`) so an empty-string labelColumn falls
 * through instead of emitting `label: ` (a GraphQL syntax error).
 */
export function refLabelColumn(table: Table): string {
    const label = table.labelColumn || getPkTypes(table)[0]?.name || table.columns[0]?.name;
    if (!label) throw new Error(`Table '${table.name}' has no usable label column for a reference dropdown.`);
    return label;
}

export interface TableRefPlan {
    query: string;
    labelColumn: string;
    /** True when the label column is String-typed and thus supports the
     *  server-side `_contains` search; callers fall back to client filtering. */
    serverSearch: boolean;
}

/**
 * Build the FK-dropdown list query. Sorted by the label column so the fetched
 * window is deterministic. When `search` is non-empty and the label column is
 * String-typed, the list is narrowed server-side (`_contains` on the label
 * column) so parents beyond the fetch limit stay findable; otherwise the caller
 * filters client-side over the fetched window.
 */
export function tableRefPlan(table: Table, keyColumn: string, search?: string): TableRefPlan {
    assertGraphQlName(table.name, 'reference table name');
    assertGraphQlName(keyColumn, 'reference key column');
    const labelColumn = refLabelColumn(table);
    assertGraphQlName(labelColumn, 'reference label column');
    if (!table.columns.some((c) => c.name === keyColumn)) {
        throw new Error(`Reference table '${table.name}' has no key column '${keyColumn}'.`);
    }
    const labelType = table.columns.find((c) => c.name === labelColumn)?.paramType?.replace('!', '');
    const serverSearch = labelType === 'String';
    const hasSearch = !!search && search.trim() !== '' && serverSearch;
    const paramDecls = hasSearch ? '$limit: Int, $search: String' : '$limit: Int';
    const filterText = hasSearch ? `filter: {${labelColumn}: {_contains: $search}} ` : '';
    const query = `query Get_${table.name}_Ref(${paramDecls}) {
            values: ${table.name}(${filterText}limit: $limit sort: [${labelColumn}_asc]) {
                data {
                    key: ${keyColumn}
                    label: ${labelColumn}
                }
            }
        }`;
    return { query, labelColumn, serverSearch };
}

export interface TableRefLookupPlan {
    query: string;
    /** GraphQL type of the key column (non-null suffix stripped), for coercion. */
    keyType: string;
}

/**
 * Build the single-row lookup that resolves the label of the currently stored
 * FK value when it falls outside the fetched dropdown window, so the Select can
 * display it instead of a misleading placeholder.
 */
export function tableRefLookupPlan(table: Table, keyColumn: string): TableRefLookupPlan {
    assertGraphQlName(table.name, 'reference table name');
    assertGraphQlName(keyColumn, 'reference key column');
    const labelColumn = refLabelColumn(table);
    assertGraphQlName(labelColumn, 'reference label column');
    const keyColumnDef = table.columns.find((c) => c.name === keyColumn);
    if (!keyColumnDef) {
        throw new Error(`Reference table '${table.name}' has no key column '${keyColumn}'.`);
    }
    const keyType = keyColumnDef.paramType.replace('!', '');
    const query = `query Get_${table.name}_RefValue($key: ${keyType}) {
            values: ${table.name}(filter: {${keyColumn}: {_eq: $key}} limit: 1) {
                data {
                    key: ${keyColumn}
                    label: ${labelColumn}
                }
            }
        }`;
    return { query, keyType };
}

/**
 * Coerce a stored/route FK value to what the lookup's `$key` variable expects.
 * Delegates to the shared coerceForGql so BigInt keys stay strings (Number()
 * loses precision past 2^53) and Boolean keys stay booleans.
 */
export function coerceKeyValue(keyType: string, value: unknown): unknown {
    return coerceForGql(value, keyType);
}
