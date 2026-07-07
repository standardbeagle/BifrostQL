import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useMemo } from "react";
import { useFetcher } from "../common/fetcher";
import { Table } from "../types/schema";
import type { PkFilter } from "../lib/row-id";
import { invalidateAfterTableWrite } from "../lib/invalidate";
import { useToast } from "./useToast";

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
    const { toast } = useToast();

    const columnParamType = useMemo(() => {
        const map = new Map<string, string>();
        for (const c of table.columns) map.set(c.name, c.paramType);
        return map;
    }, [table]);

    // No fallback to ['id']: a table with no declared primary key cannot be safely
    // targeted for a single-row delete. Guessing 'id' produced a `{id: null}` delete
    // when the column didn't exist or the value was missing.
    const pkColumns = useMemo(() => table.primaryKeys ?? [], [table]);

    const buildPayload = (input: DeleteInput): PkFilter => {
        if (pkColumns.length === 0) {
            throw new Error(`Cannot delete from '${table.name}': the table has no primary key.`);
        }
        const payload: PkFilter = {};
        if (isPkFilter(input)) {
            for (const col of pkColumns) {
                const raw = input[col];
                // Refuse a missing key value rather than coercing it to null and firing
                // a `{col: null}` delete (mirrors the m2m attach guard). null never
                // identifies a row and would either match nothing or, worse, a row whose
                // key is genuinely null.
                if (raw === undefined || raw === null) {
                    throw new Error(`Cannot delete row: missing value for primary-key column '${col}'.`);
                }
                payload[col] = coerceValue(raw, columnParamType.get(col));
            }
            return payload;
        }
        // Legacy scalar form — only valid for a single-column PK.
        if (pkColumns.length !== 1) {
            throw new Error(
                `Cannot delete from '${table.name}': composite primary key requires an object key, not a scalar.`,
            );
        }
        if (input === undefined || input === null) {
            throw new Error(`Cannot delete row: missing value for primary-key column '${pkColumns[0]}'.`);
        }
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
        onSuccess: () => {
            invalidateAfterTableWrite(queryClient, table.name);
            toast('Row deleted');
        },
    });

    const batchMutation = useMutation({
        mutationFn: (actions: Record<string, unknown>[]) => fetcher.query(batchQueryStr, { actions }),
        onSuccess: (_data, actions) => {
            invalidateAfterTableWrite(queryClient, table.name);
            const n = actions.length;
            toast(`${n} ${n === 1 ? 'row' : 'rows'} deleted`);
        },
    });

    const deleteRow = (detail: DeleteInput) => {
        let payload: PkFilter;
        try {
            payload = buildPayload(detail);
        } catch (e) {
            return Promise.reject(e);
        }
        return deleteMutation.mutateAsync(payload);
    };

    const deleteRows = (details: DeleteInput[]) => {
        let actions: { delete: PkFilter }[];
        try {
            actions = details.map((d) => ({ delete: buildPayload(d) }));
        } catch (e) {
            return Promise.reject(e);
        }
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
