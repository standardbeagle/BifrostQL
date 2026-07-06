import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useMemo } from "react";
import { useFetcher } from "../common/fetcher";
import { Table, Column } from "../types/schema";
import { parsePkRoute, type PkFilter } from "../lib/row-id";
import { isJsonColumn } from "../lib/content-detect";
import { useToast } from "./useToast";

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
        // Explicit NULL (e.g. an FK/enum cleared via "(none)") — send null on
        // update to clear it, omit on insert so the DB default applies.
        if (coerced[col.name] === null) {
            coerced[col.name] = isInsert ? undefined : null;
            continue;
        }
        if (numericTypes.some(t => t === col.paramType)) {
            const val = coerced[col.name];
            // An empty field means "no value": clear it with null rather than
            // coercing "" to 0 via +val (a silent, wrong data write). null/
            // undefined stay undefined so inserts omit the column entirely.
            coerced[col.name] = val == null ? undefined : val === "" ? null : +val;
        }
        if (booleanTypes.some(t => t === col.paramType)) {
            const v = coerced[col.name];
            // Nullable booleans keep NULL rather than coercing an unset value to
            // false. On insert an unset value is omitted so the DB default applies.
            if (col.isNullable && (v === null || v === undefined)) {
                coerced[col.name] = isInsert ? undefined : null;
            } else {
                coerced[col.name] = !!v;
            }
        }
        if (isJsonColumn(col)) {
            // The form edits JSON columns as text; parse back to a JSON value so
            // the JSON scalar isn't fed a double-encoded string. Unparseable text
            // is left as-is for the server to reject.
            const v = coerced[col.name];
            if (typeof v === 'string' && v.trim() !== '') {
                try { coerced[col.name] = JSON.parse(v); } catch { /* server validates */ }
            }
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
    const { toast } = useToast();
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
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['tableData', table.name] });
            toast(`${table.label ?? table.name} saved`);
        },
    });

    const insertMutation = useMutation({
        mutationFn: (detail: Record<string, unknown>) => fetcher.query(insertQueryStr, { detail }),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['tableData', table.name] });
            toast(`${table.label ?? table.name} created`);
        },
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
