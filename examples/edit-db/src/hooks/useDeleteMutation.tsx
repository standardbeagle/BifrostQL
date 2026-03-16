import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useMemo } from "react";
import { useFetcher } from "../common/fetcher";
import { Table } from "../types/schema";

export interface UseDeleteMutationResult {
    deleteRow: (pkValue: string | number) => Promise<unknown>;
    deleteRows: (pkValues: (string | number)[]) => Promise<unknown>;
    isPending: boolean;
    error: Error | null;
}

export function useDeleteMutation(table: Table): UseDeleteMutationResult {
    const fetcher = useFetcher();
    const queryClient = useQueryClient();
    const pkColumn = table.primaryKeys?.[0] ?? "id";
    const pkCol = table.columns.find((c) => c.name === pkColumn);
    const isIntPk = pkCol?.paramType?.startsWith("Int") ?? true;

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
        mutationFn: (detail: Record<string, unknown>) => fetcher.query(deleteQueryStr, { detail }),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['tableData', table.name] }),
    });

    const batchMutation = useMutation({
        mutationFn: (actions: Record<string, unknown>[]) => fetcher.query(batchQueryStr, { actions }),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['tableData', table.name] }),
    });

    const deleteRow = (pkValue: string | number) => {
        const detail: Record<string, unknown> = { [pkColumn]: isIntPk ? +pkValue : pkValue };
        return deleteMutation.mutateAsync(detail);
    };

    const deleteRows = (pkValues: (string | number)[]) => {
        const actions = pkValues.map((pk) => ({
            delete: { [pkColumn]: isIntPk ? +pk : pk },
        }));
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
