import { useQuery } from "@apollo/client";
import { GET_DB_SCHEMA } from "../common/schema";
import { createContext, useCallback, useContext, useEffect, useState } from "react";

//Load both regular and enhanced schema, and return enhanced schema if available
const { loading, error, data } = { loading: false, error: null, data: null };

const SchemaContext = createContext<any>(null);

export const SchemaProvider = ({ children }: { children: any }) => {
    const value = useSchemaLoader();
    return (<SchemaContext.Provider value={value}>
        {children}
    </SchemaContext.Provider>);
};

export function useSchema() {
    return useContext(SchemaContext);
}

function useSchemaLoader() {
    //const client = useApolloClient();
    const { loading: dbLoading, error: dbError, data: dbData } = useQuery(GET_DB_SCHEMA);
    // const { loading, error, data } = useQuery(GET_SCHEMA);
    const [result, setResult] = useState<any>({
        loading: false,
        error: null,
        data: []
    });
    const [internal, setInternal] = useState<any>({ data: []});

    const findTable = useCallback((tableName: string) => {
        return internal.data.find((t: any) => t.graphQlName === tableName);
    }, [internal]);


    useEffect(() => {
        //console.log([dbLoading, dbError, dbData, loading, error, data]);
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
        // if (data && !dbLoading && !dbError && !dbData) {
        //     const tables = (data?.schema?.queryType?.fields ?? [])
        //         .map((f: any) => ({
        //             ...f,
        //             label: f.name,
        //             columns: f?.type?.fields
        //                 ?.find((d: any) => d.name === "data")
        //                 ?.type?.ofType.fields
        //                 ?.filter((c: any) => c.name.startsWith('_') == false)
        //                 ?.map((c: any) => ({ ...c, paramType: c?.type?.ofType?.name ?? "String" }))
        //         }
        //         ))
        //         ?.sort((a: any, b: any) => a.name.localeCompare(b.name));

        //     console.log(tables);
        //     setResult({ loading: false, error: null, data: tables });
        // }
        //Enhanced graphql schema
        const schema = {
            loading: false,
            error: null,
            data: dbData._dbSchema
                .map((s: any) => ({
                    ...s,
                    name: s.graphQlName,
                    label: s.dbName,
                    metadata: parseMetadata(s.metadata),
                    columns: s.columns.map((c: any) => ({
                        ...c,
                        name: c.graphQlName,
                        label: c.dbName,
                        metadata: parseMetadata(c.metadata),
                    }))
                })),
        };
        setInternal(schema);

    }, [dbLoading, dbError, dbData, loading, error, data]);

    useEffect(() => {
        const value = {...internal, findTable};
        setResult(value);
    }, [internal]);
    return result;
}

function parseMetadata(metadata: any) {
    return metadata.reduce((acc: any, m: any) => ({ ...acc, [m.key]: getMetaValue(m) }), {});
}

function getMetaValue({key, value}: {key: string, value: string}) {
    if (key === 'type') return parseTableType(value);
    return value;
}

function parseTableType(lookup: string) {
    const lookupMatch = lookup.match(/lookup\s*\(\s*(?<id>\w+)\s*,\s*(?<label>\w+)\s*\)/m);
    if (lookupMatch?.groups) return { type: 'lookup', ...lookupMatch.groups };
    return {};
}


// https://regex101.com/r/UUAoSj/1
// function parseJoins(joins: string) {
//     return joins.matchAll(/((?:[_\w]+\s+[\w]+\((?:([\.=\w]+)|(\s*,\s*))*\))+?)(?:(?:\s*,\s*)|(?:\s*$))/gm)
// }