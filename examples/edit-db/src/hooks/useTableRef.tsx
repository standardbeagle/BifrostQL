import { gql, useQuery } from "@apollo/client";
import { useMemo } from "react";
import { Join, Schema } from "../types/schema";

export function useTableRef(schema: Schema, tableName: string, columnName: string) : TableRef {

    const [table] = useMemo(() => {
        const destTable = schema?.data?.find((x: { graphQlName: string }) => x.graphQlName === tableName);
        return [destTable];
    }, [schema, columnName, tableName]);

    const query = useMemo(() => {
        if (schema.loading || schema.error) return gql`query {  }`;

        return gql`
            query Get_${tableName}_Ref{ 
                values: ${tableName}(limit: -1) { 
                    data { 
                        key: ${columnName}
                        label: ${table?.labelColumn ?? 'id'}
                    }
                }
            }`;
    }, [columnName, table, schema.loading, schema.error, tableName]);

    const { loading, error, data } = useQuery(query, {
        skip: !table || !schema.data || !!schema.error || schema.loading,
    });

    return {
        loading: loading || schema.loading,
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
    error: any;
    data: TableRefValue[];
}
