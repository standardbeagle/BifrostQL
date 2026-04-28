import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useMemo } from "react";
import { useFetcher } from "../common/fetcher";
import { Table } from "../types/schema";
import type { PkFilter } from "../lib/row-id";

export type DeleteInput = PkFilter | string | number;

export interface UseDeleteMutationResult {
    deleteRow: (detail: DeleteInput) => Promise<unknown>;
    deleteRows: (details: DeleteInput[]) => Promise<unknown>;
    isPending: boolean;
    error: Error | null;
}

function isPkFilter(value: DeleteInput): value is PkFilter {
    return typeof value === 'object' && value !== null;
}

function coerceValue(value: unknown, paramType: string | undefined): unknown {
    if (value === null || value === undefined) return null;
    if (!paramType) return value;
    const base = paramType.replace('!', '');
    if (base === 'Int') {
        const n = typeof value === 'number' ? value : Number(value);
        return Number.isFinite(n) ? Math.trunc(n) : value;
    }
    if (base === 'Float') {
        const n = typeof value === 'number' ? value : Number(value);
        return Number.isFinite(n) ? n : value;
    }
    if (base === 'Boolean') {
        if (typeof value === 'boolean') return value;
        return value === 'true' || value === 1;
    }
    return String(value);
}

export function useDeleteMutation(table: Table): UseDeleteMutationResult {
    const fetcher = useFetcher();
    const queryClient = useQueryClient();

    const columnParamType = useMemo(() => {
        const map = new Map<string, string>();
        for (const c of table.columns) map.set(c.name, c.paramType);
        return map;
    }, [table]);

    const pkColumns = useMemo(() => {
        const keys = table.primaryKeys ?? [];
        return keys.length > 0 ? keys : ['id'];
    }, [table]);

    const buildPayload = (input: DeleteInput): PkFilter => {
        const payload: PkFilter = {};
        if (isPkFilter(input)) {
            for (const col of pkColumns) {
                payload[col] = coerceValue(input[col], columnParamType.get(col));
            }
            return payload;
        }
        // Legacy scalar form — assumes a single-column PK on the first PK.
        const firstPk = pkColumns[0];
        payload[firstPk] = coerceValue(input, columnParamType.get(firstPk));
        return payload;
    };

    const deleteQueryStr = useMemo(() =>
        `mutation deleteSingle($detail: Delete_${table.name}){
            ${table.name}(delete: $detail)
        }`,
        [table]
    );

    const batchQueryStr = useMemo(() =>
        `mutation batchDelete($actions: [batch_${table.name}!]!){
            ${table.name}_batch(actions: $actions)
        }`,
        [table]
    );

    const deleteMutation = useMutation({
        mutationFn: (detail: PkFilter) => fetcher.query(deleteQueryStr, { detail }),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['tableData', table.name] }),
    });

    const batchMutation = useMutation({
        mutationFn: (actions: Record<string, unknown>[]) => fetcher.query(batchQueryStr, { actions }),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['tableData', table.name] }),
    });

    const deleteRow = (detail: DeleteInput) => {
        return deleteMutation.mutateAsync(buildPayload(detail));
    };

    const deleteRows = (details: DeleteInput[]) => {
        const actions = details.map((d) => ({ delete: buildPayload(d) }));
        return batchMutation.mutateAsync(actions);
    };

    const error = deleteMutation.error ?? batchMutation.error;

    return {
        deleteRow,
        deleteRows,
        isPending: deleteMutation.isPending || batchMutation.isPending,
        error: error as Error | null,
    };
}
