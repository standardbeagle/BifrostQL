import { useQuery } from "@apollo/client";
import { GET_SCHEMA, GET_DB_SCHEMA } from "../common/schema";
import { useEffect, useState } from "react";

//Load both regular and enhanced schema, and return enhanced schema if available
export function useSchema() {
    const { loading: dbLoading, error: dbError, data: dbData } = useQuery(GET_DB_SCHEMA);
    const { loading, error, data } = useQuery(GET_SCHEMA);
    const [result, setResult] = useState<any>({
        loading: false,
        error: null,
        data: []
    });

    useEffect(() => {
        if (dbLoading || loading) {
            setResult({ loading: true, error: null, data: [] });
            return;
        }
        if (dbError || error) {
            setResult({ loading: false, error: [dbError, error].join(' '), data: [] });
            return;
        }
        if (!dbData && !data) {
            setResult({ loading, error, data: [] });
            return;
        }
        //Regular graphql schema
        if (data && !dbLoading && !dbError && !dbData) {
            const tables = (data?.schema?.queryType?.fields ?? [])
                .map((f: any) => ({
                    ...f,
                    label: f.name,
                    columns: f?.type?.fields
                        ?.find((d: any) => d.name === "data")
                        ?.type?.ofType.fields
                        ?.filter((c: any) => c.name.startsWith('_') == false)
                        ?.map((c: any) => ({ ...c, paramType: c?.type?.ofType?.name ?? "String" }))
                }
                ))
                ?.sort((a: any, b: any) => a.name.localeCompare(b.name));

            console.log(tables);
            setResult({ loading: false, error: null, data: tables });
        }
        //Enhanced graphql schema
        setResult({
            loading: false,
            error: null,
            data: dbData._dbSchema
                .map((s: any) => ({
                    ...s,
                    name: s.graphQlName,
                    label: s.dbName,
                    columns: s.columns.map((c: any) => ({
                        ...c,
                        name: c.graphQlName,
                        label: c.dbName,
                    }))
                })),
        });

    }, [dbLoading, dbError, dbData, loading, error, data]);

    return result;
}