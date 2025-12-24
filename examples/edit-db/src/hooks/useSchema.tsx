import { useQuery } from "@apollo/client";
import { GET_DB_SCHEMA } from "../common/schema";
import { createContext, useCallback, useContext, useEffect, useState, ReactNode } from "react";
import { Schema, Table, Column, TableMetadata } from '../types/schema';

interface MetadataItem {
    key: string;
    value: string;
}

interface DbColumnItem {
    graphQlName: string;
    dbName: string;
    paramType: string;
    isPrimaryKey: boolean;
    isIdentity: boolean;
    isNullable: boolean;
    isReadOnly: boolean;
    metadata: MetadataItem[];
}

interface DbSchemaItem {
    graphQlName: string;
    dbName: string;
    labelColumn: string;
    primaryKeys: string[];
    isEditable: boolean;
    metadata: MetadataItem[];
    columns: DbColumnItem[];
    multiJoins: { name: string; sourceColumnNames: string[]; destinationTable: string; destinationColumnNames: string[] }[];
    singleJoins: { name: string; sourceColumnNames: string[]; destinationTable: string; destinationColumnNames: string[] }[];
}

const SchemaContext = createContext<Schema>({loading: true, error: null, data: [], findTable: () => undefined});

export const SchemaProvider = ({ children }: { children: ReactNode }) => {
    const value = useSchemaLoader();
    return (<SchemaContext.Provider value={value}>
        {children}
    </SchemaContext.Provider>);
};

export function useSchema() {
    return useContext(SchemaContext);
}

function useSchemaLoader(): Schema {
    const { loading: dbLoading, error: dbError, data: dbData } = useQuery(GET_DB_SCHEMA);
    const [result, setResult] = useState<Schema>({
        loading: false,
        error: null,
        data: [],
        findTable: () => undefined
    });
    const [internal, setInternal] = useState<Omit<Schema, 'findTable'>>({ 
        loading: false,
        error: null,
        data: []
    });

    const findTable = useCallback((tableName: string): Table | undefined => {
        return internal.data.find((t: Table) => t.graphQlName === tableName);
    }, [internal]);

    useEffect(() => {
        if (dbLoading) {
            setInternal({ loading: true, error: null, data: [] });
            return;
        }
        if (dbError) {
            setInternal({ loading: false, error: { message: dbError.message }, data: [] });
            return;
        }
        if (!dbData) {
            setInternal({ loading: false, error: null, data: [] });
            return;
        }

        const schema: Omit<Schema, 'findTable'> = {
            loading: false,
            error: null,
            data: dbData._dbSchema.map((s: DbSchemaItem): Table => ({
                ...s,
                name: s.graphQlName,
                label: s.dbName,
                metadata: parseMetadata(s.metadata),
                columns: s.columns.map((c: DbColumnItem): Column => ({
                    ...c,
                    name: c.graphQlName,
                    label: c.dbName,
                    metadata: parseMetadata(c.metadata),
                }))
            })),
        };
        setInternal(schema);
    }, [dbLoading, dbError, dbData]);

    useEffect(() => {
        const value: Schema = {...internal, findTable};
        setResult(value);
    }, [internal, findTable]);

    return result;
}

function parseMetadata(metadata: MetadataItem[]): Record<string, string | TableMetadata['type']> {
    return metadata.reduce((acc: Record<string, string | TableMetadata['type']>, m: MetadataItem) => ({ ...acc, [m.key]: getMetaValue(m) }), {});
}

function getMetaValue({key, value}: {key: string, value: string}) {
    if (key === 'type') return parseTableType(value);
    return value;
}

function parseTableType(lookup: string): TableMetadata['type'] {
    const lookupMatch = lookup.match(/lookup\s*\(\s*(?<id>\w+)\s*,\s*(?<label>\w+)\s*\)/m);
    if (lookupMatch?.groups?.id && lookupMatch?.groups?.label) {
        return { 
            type: 'lookup', 
            id: lookupMatch.groups.id, 
            label: lookupMatch.groups.label 
        };
    }
    return undefined;
}