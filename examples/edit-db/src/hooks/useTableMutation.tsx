import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useMemo } from "react";
import { useFetcher } from "../common/fetcher";
import { Table, Column } from "../types/schema";
import { parsePkRoute, type PkFilter } from "../lib/row-id";
import { isJsonColumn } from "../lib/content-detect";
import { invalidateAfterTableWrite } from "../lib/invalidate";
import { useToast } from "./useToast";
import { assertGraphQlName } from "../lib/query-builder";

const numericTypes = ["Int", "Int!", "Float", "Float!"];
// BigInt exceeds Number's 2^53 safe-integer range: coercing through +val /
// Number() silently rounds large keys and values. The read path (fk.ts /
// row-id.ts coerceForGql) already passes BigInt as strings, so the write path
// must too — otherwise an edited BigInt PK targets the wrong row.
const bigIntTypes = ["BigInt", "BigInt!"];
const booleanTypes = ["Boolean", "Boolean!"];

function coerceNumericValue(value: unknown, paramType: string, columnName: string): number {
    const baseType = paramType.replace('!', '');
    if (typeof value === 'number') {
        if (Number.isFinite(value) && (baseType !== 'Int' || Number.isInteger(value))) return value;
        throw new Error(`Invalid ${baseType} value for column '${columnName}'.`);
    }

    const text = String(value).trim();
    const valid = baseType === 'Int'
        ? /^[+-]?\d+$/.test(text)
        : /^[+-]?(?:(?:\d+\.?\d*)|(?:\.\d+))(?:[eE][+-]?\d+)?$/.test(text);
    if (!valid) throw new Error(`Invalid ${baseType} value for column '${columnName}'.`);
    const parsed = Number(text);
    if (!Number.isFinite(parsed)) throw new Error(`Invalid ${baseType} value for column '${columnName}'.`);
    return parsed;
}

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
            // An empty field means "no value": on update clear it with null
            // rather than coercing "" to 0 (a silent, wrong data write); on
            // insert omit the column entirely (undefined) so an explicit null
            // doesn't bypass the column's DB default. null/undefined stay
            // undefined so inserts omit the column entirely.
            coerced[col.name] = val == null ? undefined
                : val === "" ? (isInsert ? undefined : null)
                : coerceNumericValue(val, col.paramType, col.name);
        }
        if (bigIntTypes.some(t => t === col.paramType)) {
            const val = coerced[col.name];
            // Same empty-value semantics as numeric, but the value itself is
            // passed as a string to preserve precision beyond 2^53.
            coerced[col.name] = val == null ? undefined
                : val === "" ? (isInsert ? undefined : null)
                : String(val);
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
                coerced[col.name] = raw == null ? null : coerceNumericValue(raw, col.paramType, col.name);
            } else {
                // Strings — including BigInt PKs, which must stay strings so a
                // key above 2^53 targets the exact row it was read from.
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
    assertGraphQlName(table.name, 'table mutation table name');
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
            invalidateAfterTableWrite(queryClient, table.name);
            toast(`${table.label ?? table.name} saved`);
        },
    });

    const insertMutation = useMutation({
        mutationFn: (detail: Record<string, unknown>) => fetcher.query(insertQueryStr, { detail }),
        onSuccess: () => {
            invalidateAfterTableWrite(queryClient, table.name);
            toast(`${table.label ?? table.name} created`);
        },
    });

    const pkFilter = useMemo(() => {
        if (isInsert || !editId) return null;
        return parsePkRoute(editId, table);
    }, [isInsert, editId, table]);

    const update = (detail: Record<string, unknown>) => {
        // An UPDATE with no primary-key columns has no WHERE clause — it would rewrite
        // every row. A null pkFilter (malformed/stale editid) or a table with no PK
        // columns must be refused client-side rather than sent.
        if (!pkFilter || idColumns.length === 0) {
            return Promise.reject(
                new Error(`Cannot update ${table.label ?? table.name}: the record has no resolvable primary key.`),
            );
        }
        let coerced: Record<string, unknown>;
        try {
            coerced = coerceDetail(detail, editColumns, idColumns, pkFilter, false);
        } catch (e) {
            return Promise.reject(e);
        }
        return updateMutation.mutateAsync(coerced);
    };

    const insert = (detail: Record<string, unknown>) => {
        let coerced: Record<string, unknown>;
        try {
            coerced = coerceDetail(detail, editColumns, idColumns, null, true);
        } catch (e) {
            return Promise.reject(e);
        }
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
