import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { Schema } from '../types/schema';
import { useFetcher } from '../common/fetcher';
import { rowIdOf, encodeRouteParts } from '../lib/row-id';
import { coerceForGql, gqlTypeOf } from '../lib/fk';
import { isGraphQlName } from '../lib/query-builder';

const COMPOSITE_REF_LIMIT = 500;
const COMPOSITE_REF_SEARCH_LIMIT = 50;

export interface CompositeTableRefValue {
    /** Route-encoded composite key (`v1::v2`) suitable as a Select value. */
    route: string;
    /** Per-column raw values keyed by destination column name. */
    values: Record<string, unknown>;
    label: string;
}

export interface CompositeTableRef {
    loading: boolean;
    error: unknown;
    data: CompositeTableRefValue[];
    /** True when `search` narrows server-side (String label column, `_contains`);
     *  false means the caller must filter the fetched window client-side. */
    serverSearch: boolean;
}

export interface CompositeTableRefOptions {
    /** Free-text search term. Applied server-side when the label column is String. */
    search?: string;
    /** Defer the list fetch until the dropdown is opened (fetch-on-open). */
    enabled?: boolean;
    /** The currently-selected destination-column values, so the selected parent row
     *  can be looked up and shown even when it falls outside the fetched window. */
    currentValues?: Record<string, unknown> | null;
}

/**
 * Fetch parent rows for a composite-FK Select widget. Returns one row per parent
 * with its label plus every destination column value so the caller can write
 * each composite-FK source column on the child form when a selection is made.
 *
 * Mirrors the single-column FK data source (table-ref.ts / useTableRef +
 * useTableRefValue): the window is sorted by the label column (deterministic),
 * narrowed server-side when a search term is given and the label is String-typed,
 * and the currently-selected row is resolved via a single-row lookup when it lies
 * outside the fetched window so it stays selectable instead of showing blank.
 */
export function useCompositeTableRef(
    schema: Schema,
    destTableName: string,
    destColumnNames: string[],
    options: CompositeTableRefOptions = {},
): CompositeTableRef {
    const fetcher = useFetcher();
    const { search = '', enabled = true, currentValues = null } = options;
    const term = search.trim();

    const destTable = useMemo(
        () => schema?.data?.find((t) => t.graphQlName === destTableName),
        [schema, destTableName],
    );

    const labelCol = useMemo(
        () => destTable?.labelColumn || destColumnNames[0],
        [destTable, destColumnNames],
    );

    // Server-side search is only valid on a String-typed label column (`_contains`
    // is only generated for those); otherwise the caller filters the window client-side.
    const serverSearch = useMemo(() => {
        const labelType = destTable?.columns.find((c) => c.name === labelCol)?.paramType?.replace('!', '');
        return labelType === 'String';
    }, [destTable, labelCol]);

    const validQueryShape = useMemo(() => {
        if (!destTable || destColumnNames.length === 0) return false;
        if (!isGraphQlName(destTableName) || !isGraphQlName(labelCol)) return false;
        const columnNames = new Set(destTable.columns.map((c) => c.name));
        if (!columnNames.has(labelCol)) return false;
        return destColumnNames.every((c) => isGraphQlName(c) && columnNames.has(c));
    }, [destTable, destTableName, destColumnNames, labelCol]);

    const fields = useMemo(() => {
        if (!destTable || !validQueryShape) return '';
        const cols = new Set<string>(destColumnNames);
        cols.add(labelCol);
        return Array.from(cols).join(' ');
    }, [destTable, validQueryShape, destColumnNames, labelCol]);

    const hasSearch = !!term && serverSearch;

    const query = useMemo(() => {
        if (schema.loading || schema.error || !destTable || destColumnNames.length === 0) return null;
        if (!validQueryShape) return null;
        const paramDecls = hasSearch ? '($limit: Int, $search: String)' : '($limit: Int)';
        const filterText = hasSearch ? `filter: {${labelCol}: {_contains: $search}} ` : '';
        return `query Get_${destTableName}_CompositeRef${paramDecls} { values: ${destTableName}(${filterText}limit: $limit sort: [${labelCol}_asc]) { data { ${fields} } } }`;
    }, [destTable, destTableName, fields, labelCol, hasSearch, schema.loading, schema.error, destColumnNames.length, validQueryShape]);

    // Only vary the request by term when the server actually filters.
    const effectiveTerm = serverSearch ? term : '';
    const { isLoading, error, data } = useQuery({
        queryKey: ['compositeTableRef', destTableName, destColumnNames.join('|'), effectiveTerm],
        queryFn: () => fetcher.query<{ values: { data: Record<string, unknown>[] } }>(
            query!,
            effectiveTerm
                ? { limit: COMPOSITE_REF_SEARCH_LIMIT, search: effectiveTerm }
                : { limit: COMPOSITE_REF_LIMIT },
        ),
        enabled: !!query && enabled,
    });

    const windowRows: CompositeTableRefValue[] = useMemo(() => {
        if (!destTable || !data) return [];
        const pkLike = { primaryKeys: destColumnNames };
        return (data.values?.data ?? []).map((row, i) => {
            const values: Record<string, unknown> = {};
            for (const c of destColumnNames) values[c] = row[c];
            const route = rowIdOf(row, pkLike, i);
            return { route, values, label: String(row[labelCol] ?? route) };
        });
    }, [destTable, destColumnNames, labelCol, data]);

    // Lookup fallback: when the currently-selected parent row is outside the fetched
    // window (a parent past the 500-row cap, or filtered out by a search term), fetch
    // that single row by its composite key so it can still render as the selection.
    const currentRoute = useMemo(() => {
        if (!currentValues) return '';
        const vals: unknown[] = [];
        for (const c of destColumnNames) {
            const v = currentValues[c];
            if (v === undefined || v === null || v === '') return '';
            vals.push(v);
        }
        return encodeRouteParts(vals);
    }, [currentValues, destColumnNames]);

    const currentInWindow = currentRoute === '' || windowRows.some((r) => r.route === currentRoute);

    const lookupPlan = useMemo(() => {
        if (!destTable || !validQueryShape || currentInWindow || currentRoute === '' || !currentValues) return null;
        const byName = new Map(destTable.columns.map((c) => [c.name, c] as const));
        const clauses: string[] = [];
        const params: string[] = [];
        const variables: Record<string, unknown> = {};
        for (let i = 0; i < destColumnNames.length; i++) {
            const c = destColumnNames[i];
            const gqlType = gqlTypeOf(byName.get(c));
            const varName = `k${i}`;
            params.push(`$${varName}: ${gqlType}`);
            clauses.push(`{${c}: {_eq: $${varName}}}`);
            variables[varName] = coerceForGql(currentValues[c], gqlType);
        }
        const filterText = clauses.length === 1 ? clauses[0] : `{and: [${clauses.join(', ')}]}`;
        const query = `query Get_${destTableName}_CompositeRefValue(${params.join(', ')}) { values: ${destTableName}(filter: ${filterText} limit: 1) { data { ${fields} } } }`;
        return { query, variables };
    }, [destTable, destTableName, fields, destColumnNames, currentInWindow, currentRoute, currentValues, validQueryShape]);

    const { data: lookupData } = useQuery({
        queryKey: ['compositeTableRefValue', destTableName, currentRoute],
        queryFn: () => fetcher.query<{ values: { data: Record<string, unknown>[] } }>(
            lookupPlan!.query,
            lookupPlan!.variables,
        ),
        enabled: !!lookupPlan,
    });

    const rows: CompositeTableRefValue[] = useMemo(() => {
        const lookupRow = lookupData?.values?.data?.[0];
        if (!lookupRow) return windowRows;
        const values: Record<string, unknown> = {};
        for (const c of destColumnNames) values[c] = lookupRow[c];
        const route = encodeRouteParts(destColumnNames.map((c) => lookupRow[c]));
        // Prepend the resolved current selection if the window doesn't already carry it.
        if (windowRows.some((r) => r.route === route)) return windowRows;
        return [{ route, values, label: String(lookupRow[labelCol] ?? route) }, ...windowRows];
    }, [windowRows, lookupData, destColumnNames, labelCol]);

    return {
        loading: isLoading || schema.loading,
        error: error || schema.error,
        data: rows,
        serverSearch,
    };
}
