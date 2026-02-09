import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { Schema } from "../types/schema";
import { useFetcher } from "../common/fetcher";

export function useTableRef(schema: Schema, tableName: string, columnName: string): TableRef {
    const fetcher = useFetcher();

    const table = useMemo(
        () => schema?.data?.find((x: { graphQlName: string }) => x.graphQlName === tableName),
        [schema, tableName]
    );

    const query = useMemo(() => {
        if (schema.loading || schema.error || !table) return null;
        return `query Get_${tableName}_Ref {
            values: ${tableName}(limit: -1) {
                data {
                    key: ${columnName}
                    label: ${table.labelColumn ?? 'id'}
                }
            }
        }`;
    }, [columnName, table, schema.loading, schema.error, tableName]);

    const { isLoading, error, data } = useQuery({
        queryKey: ['tableRef', tableName, columnName],
        queryFn: () => fetcher.query<{ values: { data: TableRefValue[] } }>(query!),
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
