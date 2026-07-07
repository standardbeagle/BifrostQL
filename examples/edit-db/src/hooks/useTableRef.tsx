import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { Schema, Table } from "../types/schema";
import { useFetcher } from "../common/fetcher";
import {
    TABLE_REF_LIMIT,
    TABLE_REF_SEARCH_LIMIT,
    tableRefPlan,
    tableRefLookupPlan,
    coerceKeyValue,
} from "../lib/table-ref";

export interface TableRefValue {
    key: string;
    label: string;
}

export interface TableRef {
    loading: boolean;
    error: unknown;
    data: TableRefValue[];
    /** True when `search` narrows server-side (String label column, `_contains`);
     *  false means the caller must filter the fetched window client-side. */
    serverSearch: boolean;
}

function findTable(schema: Schema, tableName: string): Table | undefined {
    return schema?.data?.find((x: { graphQlName: string }) => x.graphQlName === tableName);
}

/**
 * Fetch the option rows for an FK dropdown (key + label per parent row).
 * When `search` is non-empty and the parent's label column is String-typed the
 * list is narrowed server-side, so parents beyond the fetch window stay
 * findable; otherwise one window is fetched and the caller filters it.
 */
export function useTableRef(schema: Schema, tableName: string, columnName: string, search = '', enabled = true): TableRef {
    const fetcher = useFetcher();

    const table = useMemo(() => findTable(schema, tableName), [schema, tableName]);
    const term = search.trim();

    const plan = useMemo(() => {
        if (schema.loading || schema.error || !table) return null;
        return tableRefPlan(table, columnName, term);
    }, [columnName, table, schema.loading, schema.error, term]);

    // Only vary the cache (and the request) by term when the server actually
    // filters; otherwise one fetch is reused and filtering happens client-side.
    const effectiveTerm = plan?.serverSearch ? term : '';
    const { isLoading, error, data } = useQuery({
        queryKey: ['tableRef', tableName, columnName, effectiveTerm],
        queryFn: ({ signal }) => fetcher.query<{ values: { data: TableRefValue[] } }>(
            plan!.query,
            effectiveTerm
                ? { limit: TABLE_REF_SEARCH_LIMIT, search: effectiveTerm }
                : { limit: TABLE_REF_LIMIT },
            { signal },
        ),
        // `enabled` lets a caller defer the (up to TABLE_REF_LIMIT-row) option fetch
        // until the dropdown is actually opened — every edit dialog would otherwise
        // fire one such query per FK field on mount. The cheap current-value lookup
        // (useTableRefValue) stays live so the closed Select still shows its label.
        enabled: !!plan && enabled,
    });

    return {
        loading: isLoading || schema.loading,
        error: error || schema.error,
        data: data?.values?.data ?? [],
        serverSearch: plan?.serverSearch ?? false,
    };
}

export interface TableRefLookup {
    loading: boolean;
    error: unknown;
    value: TableRefValue | null;
}

/**
 * Resolve key + label for a single parent row by its key value. Used when the
 * currently stored FK value falls outside the fetched dropdown window, so the
 * Select can still display the stored value instead of a misleading
 * placeholder. Pass `key` as null/'' to disable the lookup.
 */
export function useTableRefValue(schema: Schema, tableName: string, columnName: string, key: unknown): TableRefLookup {
    const fetcher = useFetcher();

    const table = useMemo(() => findTable(schema, tableName), [schema, tableName]);
    const enabled = !schema.loading && !schema.error && !!table && key != null && key !== '';

    const plan = useMemo(
        () => (enabled ? tableRefLookupPlan(table!, columnName) : null),
        [enabled, table, columnName],
    );

    const { isLoading, error, data } = useQuery({
        queryKey: ['tableRefValue', tableName, columnName, String(key ?? '')],
        queryFn: ({ signal }) => fetcher.query<{ values: { data: TableRefValue[] } }>(
            plan!.query,
            { key: coerceKeyValue(plan!.keyType, key) },
            { signal },
        ),
        enabled: !!plan,
    });

    return {
        loading: isLoading,
        error: error || schema.error,
        value: data?.values?.data?.at(0) ?? null,
    };
}
