import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useMemo } from "react";
import { useFetcher } from "../common/fetcher";
import { Table, Column } from "../types/schema";

const numericTypes = ["Int", "Int!", "Float", "Float!"];
const booleanTypes = ["Boolean", "Boolean!"];

interface ColumnJoin {
    column: Column;
}

function coerceDetail(
    detail: Record<string, unknown>,
    editColumns: ColumnJoin[],
    idColumns: Column[],
    editId: string | undefined,
    isInsert: boolean
): Record<string, unknown> {
    const coerced = { ...detail };
    for (const { column: col } of editColumns) {
        if (numericTypes.some(t => t === col.paramType)) {
            const val = coerced[col.name];
            coerced[col.name] = val != null ? +val : undefined;
        }
        if (booleanTypes.some(t => t === col.paramType)) {
            coerced[col.name] = !!coerced[col.name];
        }
    }
    if (!isInsert) {
        for (const col of idColumns) {
            coerced[col.name] = col.paramType.startsWith("Int") ? +(editId ?? 0) : editId;
        }
    }
    return coerced;
}

export interface UseTableMutationResult {
    update: (detail: Record<string, unknown>) => Promise<unknown>;
    insert: (detail: Record<string, unknown>) => Promise<unknown>;
    isPending: boolean;
    error: Error | null;
}

export function useTableMutation(
    table: Table,
    editColumns: ColumnJoin[],
    idColumns: Column[],
    editId?: string
): UseTableMutationResult {
    const fetcher = useFetcher();
    const queryClient = useQueryClient();
    const isInsert = editId === undefined || editId === '';

    const updateQueryStr = useMemo(() =>
        `mutation updateSingle($detail: Update_${table.name}){
            ${table.name}(update: $detail)
        }`,
        [table]
    );

    const insertQueryStr = useMemo(() =>
        `mutation insertSingle($detail: Insert_${table.name}){
            ${table.name}(insert: $detail)
        }`,
        [table]
    );

    const updateMutation = useMutation({
        mutationFn: (detail: Record<string, unknown>) => fetcher.query(updateQueryStr, { detail }),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['tableData', table.name] }),
    });

    const insertMutation = useMutation({
        mutationFn: (detail: Record<string, unknown>) => fetcher.query(insertQueryStr, { detail }),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['tableData', table.name] }),
    });

    const update = (detail: Record<string, unknown>) => {
        const coerced = coerceDetail(detail, editColumns, idColumns, editId, false);
        return updateMutation.mutateAsync(coerced);
    };

    const insert = (detail: Record<string, unknown>) => {
        const coerced = coerceDetail(detail, editColumns, idColumns, editId, true);
        return insertMutation.mutateAsync(coerced);
    };

    const error = updateMutation.error ?? insertMutation.error;

    return {
        update,
        insert,
        isPending: updateMutation.isPending || insertMutation.isPending,
        error: error as Error | null,
    };
}
