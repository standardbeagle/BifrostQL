import { useQuery } from "@tanstack/react-query";
import { GET_DB_SCHEMA } from "../common/schema-queries";
import { createContext, useContext, useMemo, ReactNode } from "react";
import { SchemaContextValue, Table, Column, TableMetadata, ManyToManyJoin } from '../types/schema';
import { useFetcher } from "../common/fetcher";
import { humanizeName } from "../lib/humanize";
import { useEditorConfig } from "./useEditorConfig";

interface MetadataItem {
    key: string;
    value: string;
}

interface DbColumnItem {
    graphQlName: string;
    dbName: string;
    paramType: string;
    dbType: string;
    isPrimaryKey: boolean;
    isIdentity: boolean;
    isNullable: boolean;
    isReadOnly: boolean;
    metadata: MetadataItem[];
    isLargeValue?: boolean;
    maxLength?: number;
    minLength?: number;
    min?: number;
    max?: number;
    step?: number;
    pattern?: string;
    patternMessage?: string;
    inputType?: string;
    defaultValue?: string;
    enumValues?: string[] | null;
    enumLabels?: string[] | null;
}

interface DbSchemaItem {
    graphQlName: string;
    dbName: string;
    labelColumn: string;
    primaryKeys: string[];
    isEditable: boolean;
    metadata: MetadataItem[];
    columns: DbColumnItem[];
    multiJoins: { name: string; fieldName?: string; sourceColumnNames: string[]; destinationTable: string; destinationColumnNames: string[] }[];
    singleJoins: { name: string; fieldName?: string; sourceColumnNames: string[]; destinationTable: string; destinationColumnNames: string[] }[];
    manyToManyJoins?: ManyToManyJoin[];
    // Carried onto Table by the `...s` spread below.
    indexes?: { name: string; isUnique: boolean; isClustered: boolean; isPrimaryKey: boolean; columns: string[] }[];
}

interface DbSchemaResponse {
    _dbSchema: DbSchemaItem[];
}

const SchemaContext = createContext<SchemaContextValue>({loading: true, error: null, data: [], findTable: () => undefined});

/**
 * Provider component for database schema context.
 * Fetches schema via GraphQL and makes it available to child components.
 * 
 * @example
 * ```tsx
 * <SchemaProvider>
 *   <YourApp />
 * </SchemaProvider>
 * ```
 */
export const SchemaProvider = ({ children }: { children: ReactNode }) => {
    const value = useSchemaLoader();
    return (<SchemaContext.Provider value={value}>
        {children}
    </SchemaContext.Provider>);
};

/**
 * Hook to access the database schema context.
 * 
 * Returns schema loading state, error information, table definitions,
 * and a utility function to find tables by name.
 * 
 * @example
 * ```tsx
 * const { loading, error, data, findTable } = useSchema();
 * const userTable = findTable('users');
 * ```
 * 
 * @returns Schema context value with tables and metadata
 */
export function useSchema() {
    return useContext(SchemaContext);
}

/**
 * Keep only the tables a host allow-listed, matching on either name and ignoring
 * case so a host can list them the way they read in its own docs. An empty or
 * absent list means no restriction.
 */
export function filterAllowedTables(tables: Table[], allowed?: string[]): Table[] {
    if (!allowed || allowed.length === 0) return tables;
    const wanted = new Set(allowed.map((name) => name.toLowerCase()));
    return tables.filter((t) => wanted.has(t.graphQlName.toLowerCase()) || wanted.has(t.dbName.toLowerCase()));
}

function useSchemaLoader(): SchemaContextValue {
    const fetcher = useFetcher();
    const { tables: allowedTables } = useEditorConfig();

    const { isLoading, error, data: dbData } = useQuery({
        queryKey: ['dbSchema'],
        queryFn: () => fetcher.query<DbSchemaResponse>(GET_DB_SCHEMA),
        staleTime: Infinity,
    });

    return useMemo((): SchemaContextValue => {
        if (isLoading) return { loading: true, error: null, data: [], findTable: () => undefined };
        if (error) return { loading: false, error: { message: (error as Error).message }, data: [], findTable: () => undefined };
        if (!dbData) return { loading: false, error: null, data: [], findTable: () => undefined };

        const allTables = dbData._dbSchema.map((s: DbSchemaItem): Table => ({
            ...s,
            name: s.graphQlName,
            label: humanizeName(s.dbName),
            metadata: parseMetadata(s.metadata),
            columns: s.columns.map((c: DbColumnItem): Column => ({
                ...c,
                name: c.graphQlName,
                label: humanizeName(c.dbName),
                metadata: parseMetadata(c.metadata),
                inputType: parseInputType(c.inputType),
            }))
        }));

        const tables = filterAllowedTables(allTables, allowedTables);

        const findTable = (tableName: string): Table | undefined =>
            tables.find((t: Table) => t.graphQlName === tableName);

        return { loading: false, error: null, data: tables, findTable };
    }, [isLoading, error, dbData, allowedTables]);
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

const validInputTypes = ['text', 'email', 'url', 'tel', 'date', 'datetime-local', 'number', 'password', 'search'] as const;

type ValidInputType = typeof validInputTypes[number];

function parseInputType(inputType: string | undefined): ValidInputType | undefined {
    if (!inputType) return undefined;
    if (validInputTypes.includes(inputType as ValidInputType)) {
        return inputType as ValidInputType;
    }
    return undefined;
}
