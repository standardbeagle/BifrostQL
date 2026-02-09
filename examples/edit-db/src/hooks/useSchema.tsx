import { useQuery } from "@tanstack/react-query";
import { GET_DB_SCHEMA } from "../common/schema";
import { createContext, useContext, useMemo, ReactNode } from "react";
import { Schema, Table, Column, TableMetadata } from '../types/schema';
import { useFetcher } from "../common/fetcher";

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

interface DbSchemaResponse {
    _dbSchema: DbSchemaItem[];
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
    const fetcher = useFetcher();

    const { isLoading, error, data: dbData } = useQuery({
        queryKey: ['dbSchema'],
        queryFn: () => fetcher.query<DbSchemaResponse>(GET_DB_SCHEMA),
        staleTime: Infinity,
    });

    return useMemo((): Schema => {
        if (isLoading) return { loading: true, error: null, data: [], findTable: () => undefined };
        if (error) return { loading: false, error: { message: (error as Error).message }, data: [], findTable: () => undefined };
        if (!dbData) return { loading: false, error: null, data: [], findTable: () => undefined };

        const tables = dbData._dbSchema.map((s: DbSchemaItem): Table => ({
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
        }));

        const findTable = (tableName: string): Table | undefined =>
            tables.find((t: Table) => t.graphQlName === tableName);

        return { loading: false, error: null, data: tables, findTable };
    }, [isLoading, error, dbData]);
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
