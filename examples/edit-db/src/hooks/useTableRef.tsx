import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { Schema } from "../types/schema";
import { useFetcher } from "../common/fetcher";

// Cap the FK-option fetch. Previously `limit: -1` pulled the entire parent table,
// which froze the edit dialog (and hammered the server) for large tables. The
// dropdown pairs this with a client-side search box.
const TABLE_REF_LIMIT = 1000;

export function useTableRef(schema: Schema, tableName: string, columnName: string): TableRef {
    const fetcher = useFetcher();

    const table = useMemo(
        () => schema?.data?.find((x: { graphQlName: string }) => x.graphQlName === tableName),
        [schema, tableName]
    );

    const query = useMemo(() => {
        if (schema.loading || schema.error || !table) return null;
        // `||` (not `??`) so an empty-string labelColumn falls back to `id`
        // instead of emitting `label: ` (a GraphQL syntax error).
        const labelColumn = table.labelColumn || 'id';
        return `query Get_${tableName}_Ref {
            values: ${tableName}(limit: ${TABLE_REF_LIMIT}) {
                data {
                    key: ${columnName}
                    label: ${labelColumn}
                }
            }
        }`;
    }, [columnName, table, schema.loading, schema.error, tableName]);

    const { isLoading, error, data } = useQuery({
        queryKey: ['tableRef', tableName, columnName],
        queryFn: ({ signal }) => fetcher.query<{ values: { data: TableRefValue[] } }>(query!, undefined, { signal }),
        enabled: !!query,
    });

    return {
        loading: isLoading || schema.loading,
        error: error || schema.error,
        data: data?.values?.data ?? []
    };
}

export interface TableRefValue {
    key: string;
    label: string;
}

export interface TableRef {
    loading: boolean;
    error: unknown;
    data: TableRefValue[];
}
