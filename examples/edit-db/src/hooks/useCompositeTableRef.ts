import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { Schema } from '../types/schema';
import { useFetcher } from '../common/fetcher';
import { rowIdOf } from '../lib/row-id';

const COMPOSITE_REF_LIMIT = 500;

export interface CompositeTableRefValue {
    /** Route-encoded composite key (`v1::v2`) suitable as a Select value. */
    route: string;
    /** Per-column raw values keyed by destination column name. */
    values: Record<string, unknown>;
    label: string;
}

export interface CompositeTableRef {
    loading: boolean;
    error: unknown;
    data: CompositeTableRefValue[];
}

/**
 * Fetch parent rows for a composite-FK Select widget. Returns one row per parent
 * with its label plus every destination column value so the caller can write
 * each composite-FK source column on the child form when a selection is made.
 */
export function useCompositeTableRef(
    schema: Schema,
    destTableName: string,
    destColumnNames: string[],
): CompositeTableRef {
    const fetcher = useFetcher();

    const destTable = useMemo(
        () => schema?.data?.find((t) => t.graphQlName === destTableName),
        [schema, destTableName],
    );

    const fields = useMemo(() => {
        if (!destTable) return '';
        const labelCol = destTable.labelColumn ?? destColumnNames[0];
        const cols = new Set<string>(destColumnNames);
        cols.add(labelCol);
        return Array.from(cols).join(' ');
    }, [destTable, destColumnNames]);

    const query = useMemo(() => {
        if (schema.loading || schema.error || !destTable || destColumnNames.length === 0) return null;
        return `query Get_${destTableName}_CompositeRef { values: ${destTableName}(limit: ${COMPOSITE_REF_LIMIT}) { data { ${fields} } } }`;
    }, [destTable, destTableName, fields, schema.loading, schema.error, destColumnNames.length]);

    const { isLoading, error, data } = useQuery({
        queryKey: ['compositeTableRef', destTableName, destColumnNames.join('|')],
        queryFn: () => fetcher.query<{ values: { data: Record<string, unknown>[] } }>(query!),
        enabled: !!query,
    });

    const rows: CompositeTableRefValue[] = useMemo(() => {
        if (!destTable || !data) return [];
        const labelCol = destTable.labelColumn ?? destColumnNames[0];
        const pkLike = { primaryKeys: destColumnNames };
        return (data.values?.data ?? []).map((row, i) => {
            const values: Record<string, unknown> = {};
            for (const c of destColumnNames) values[c] = row[c];
            const route = rowIdOf(row, pkLike, i);
            return { route, values, label: String(row[labelCol] ?? route) };
        });
    }, [destTable, destColumnNames, data]);

    return {
        loading: isLoading || schema.loading,
        error: error || schema.error,
        data: rows,
    };
}
