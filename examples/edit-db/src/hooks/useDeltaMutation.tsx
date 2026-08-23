import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useMemo } from "react";
import { useFetcher } from "../common/fetcher";
import { Table } from "../types/schema";
import { invalidateAfterTableWrite } from "../lib/invalidate";
import { useToast } from "./useToast";
import { assertGraphQlName } from "../lib/query-builder";

/**
 * The server's collection-diff save shape (`<t>_delta`): three optional lists
 * that apply in inserted → updated → deleted order inside ONE transaction — a
 * failure anywhere applies nothing — and ride the server's set-based bulk fast
 * path at batch scale. Payloads must already be wire-ready (coerceDetail /
 * delete key payloads); this hook only carries the document.
 */
export interface DeltaDocument {
    inserted?: Record<string, unknown>[];
    updated?: Record<string, unknown>[];
    deleted?: Record<string, unknown>[];
}

export interface UseDeltaMutationResult {
    saveDelta: (delta: DeltaDocument) => Promise<unknown>;
    isPending: boolean;
    error: Error | null;
}

export function deltaChangeCount(delta: DeltaDocument): number {
    return (delta.inserted?.length ?? 0) + (delta.updated?.length ?? 0) + (delta.deleted?.length ?? 0);
}

export function useDeltaMutation(table: Table): UseDeltaMutationResult {
    assertGraphQlName(table.name, 'delta mutation table name');
    const fetcher = useFetcher();
    const queryClient = useQueryClient();
    const { toast } = useToast();

    const deltaQueryStr = useMemo(() =>
        `mutation saveDelta($delta: ${table.name}_delta){
            ${table.name}(delta: $delta)
        }`,
        [table]
    );

    const deltaMutation = useMutation({
        mutationFn: (delta: DeltaDocument) => fetcher.query(deltaQueryStr, { delta }),
        onSuccess: (_data, delta) => {
            invalidateAfterTableWrite(queryClient, table.name);
            const n = deltaChangeCount(delta);
            toast(`${n} ${n === 1 ? 'change' : 'changes'} saved`);
        },
    });

    return {
        saveDelta: (delta: DeltaDocument) => deltaMutation.mutateAsync(delta),
        isPending: deltaMutation.isPending,
        error: deltaMutation.error as Error | null,
    };
}
