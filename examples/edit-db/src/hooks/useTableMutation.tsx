import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useMemo } from "react";
import { useFetcher } from "../common/fetcher";
import { Table, Column } from "../types/schema";
import { parsePkRoute, type PkFilter } from "../lib/row-id";

const numericTypes = ["Int", "Int!", "Float", "Float!", "BigInt", "BigInt!"];
const booleanTypes = ["Boolean", "Boolean!"];

interface ColumnJoin {
    column: Column;
}

function coerceDetail(
    detail: Record<string, unknown>,
    editColumns: ColumnJoin[],
    idColumns: Column[],
    pkFilter: PkFilter | null,
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
    if (!isInsert && pkFilter) {
        for (const col of idColumns) {
            const raw = pkFilter[col.name];
            if (numericTypes.some(t => t === col.paramType)) {
                coerced[col.name] = raw == null ? null : Number(raw);
            } else {
                coerced[col.name] = raw;
            }
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

    const pkFilter = useMemo(() => {
        if (isInsert || !editId) return null;
        return parsePkRoute(editId, table);
    }, [isInsert, editId, table]);

    const update = (detail: Record<string, unknown>) => {
        const coerced = coerceDetail(detail, editColumns, idColumns, pkFilter, false);
        return updateMutation.mutateAsync(coerced);
    };

    const insert = (detail: Record<string, unknown>) => {
        const coerced = coerceDetail(detail, editColumns, idColumns, null, true);
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
