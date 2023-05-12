import { useQuery } from "@apollo/client";
import { GET_SCHEMA, GET_DB_SCHEMA } from "../common/schema";

export function useSchema() {
    const { loading: dbLoading, error: dbError, data: dbData } = useQuery(GET_DB_SCHEMA);
    const { loading, error, data } = useQuery(GET_SCHEMA);

    if (!dbLoading && dbError)
        return { loading: false, error: dbError, data: [] };

    if (!dbLoading && !dbError && !dbData) {

        if (!data)
            return { loading, error, data: [] };

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
            ?.sort((a: any, b: any) => a.name.localeCompare(b.name))
        return { loading, error, data: tables };
    }

    if (dbLoading)
        return { loading: true, error: null, data: [] };

    return {
        loading: false,
        error: null,
        data: dbData._dbSchema
            .map((s: any) => ({ ...s, 
                name: s.graphQlName, 
                label: s.dbName, 
                columns: s.columns.map((c: any) => ({
                    ...c,
                    name: c.graphQlName,
                    label: c.dbName,
                })) })),
    };
}