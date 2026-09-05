import { describe, it, expect, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { createElement, type ReactNode } from 'react';
import { FetcherProvider, type GraphQLFetcher } from '../common/fetcher';
import { EditorConfigProvider } from './useEditorConfig';
import type { Column, Table } from '../types/schema';

const idColumn: Column = {
    dbName: 'id',
    graphQlName: 'id',
    name: 'id',
    label: 'id',
    paramType: 'Int',
    dbType: 'int',
    isPrimaryKey: true,
    isIdentity: false,
    isNullable: false,
    isReadOnly: false,
    metadata: {},
};

const items: Table = {
    dbName: 'items',
    graphQlName: 'items',
    name: 'items',
    label: 'items',
    labelColumn: 'id',
    primaryKeys: ['id'],
    isEditable: true,
    metadata: {},
    columns: [idColumn],
    multiJoins: [],
    singleJoins: [],
};

vi.mock('./useSchema', () => ({
    useSchema: () => ({ loading: false, error: null, data: [items], findTable: () => items }),
}));

import { useTableExport } from './useTableExport';

describe('useTableExport', () => {
    it('forwards the caller-supplied AbortSignal to fetcher.query on the in-flight page', async () => {
        // The signal is NOT type-forced onto fetcher.query's options, so a hook
        // that drops it would still compile. Abort while the page request is in
        // flight and assert the fetcher saw the SAME (now aborted) signal.
        const controller = new AbortController();
        const seen: (AbortSignal | undefined)[] = [];
        const query = vi.fn(async (_q: string, _vars?: Record<string, unknown>, options?: { signal?: AbortSignal }) => {
            seen.push(options?.signal);
            controller.abort();
            return { items: { total: 1, data: [{ id: 1 }] } };
        });
        const fetcher: GraphQLFetcher = { query: query as unknown as GraphQLFetcher['query'] };
        const saveFile = vi.fn(async () => {});
        const wrapper = ({ children }: { children: ReactNode }) =>
            createElement(EditorConfigProvider, {
                config: { showStats: false, saveFile },
                children: createElement(FetcherProvider, { value: fetcher }, children),
            });

        const { result } = renderHook(() => useTableExport(), { wrapper });

        await expect(result.current(items, 'csv', controller.signal)).rejects.toThrow();
        expect(seen).toHaveLength(1);
        expect(seen[0]).toBe(controller.signal);
        expect(seen[0]?.aborted).toBe(true);
        expect(saveFile).not.toHaveBeenCalled();
    });
});
