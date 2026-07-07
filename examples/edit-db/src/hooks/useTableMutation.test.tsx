import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createElement, type ReactNode } from 'react';
import { useTableMutation } from './useTableMutation';
import { FetcherProvider, type GraphQLFetcher } from '../common/fetcher';
import type { Column, Table } from '../types/schema';

function col(name: string, paramType: string, isPrimaryKey = false): Column {
    return {
        dbName: name,
        graphQlName: name,
        name,
        label: name,
        paramType,
        dbType: '',
        isPrimaryKey,
        isIdentity: false,
        isNullable: true,
        isReadOnly: false,
        metadata: {},
    };
}

function tbl(name: string, primaryKeys: string[], columns: Column[]): Table {
    return {
        dbName: name,
        graphQlName: name,
        name,
        label: name,
        labelColumn: 'id',
        primaryKeys,
        isEditable: true,
        metadata: {},
        columns,
        multiJoins: [],
        singleJoins: [],
    };
}

function createHarness() {
    const query = vi.fn(async (_query: string, _variables?: Record<string, unknown>) => 1 as unknown);
    const fetcher: GraphQLFetcher = { query: query as unknown as GraphQLFetcher['query'] };
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });

    const wrapper = ({ children }: { children: ReactNode }) =>
        createElement(
            QueryClientProvider,
            { client: queryClient },
            createElement(FetcherProvider, { value: fetcher }, children),
        );

    return { query, wrapper, queryClient };
}

const editCols = (...cols: Column[]) => cols.map((column) => ({ column }));

describe('useTableMutation', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    describe('keyless update guard', () => {
        it('refuses to update when the editid does not resolve to a primary key', async () => {
            // A malformed/stale editid parses to no PK filter; an UPDATE with no WHERE
            // columns would rewrite every row. Refuse client-side and never call fetch.
            const { query, wrapper } = createHarness();
            const idCol = col('id', 'Int!', true);
            const nameCol = col('name', 'String');
            const users = tbl('users', ['id'], [idCol, nameCol]);
            const { result } = renderHook(
                // editId '' would be treated as insert; use a composite-arity mismatch
                // route against a single-PK table so parsePkRoute yields a value, but
                // pass a table with NO primary keys so idColumns is empty.
                () => useTableMutation(tbl('users', [], [idCol, nameCol]), editCols(nameCol), [], 'stale'),
                { wrapper },
            );

            await expect(result.current.update({ name: 'Ada' })).rejects.toThrow(/no resolvable primary key/);
            expect(query).not.toHaveBeenCalled();
        });
    });

    describe('BigInt precision (values pass as strings, never through Number)', () => {
        // Above Number.MAX_SAFE_INTEGER (2^53-1 = 9007199254740991): Number()
        // rounds both to ...992, so a lossy write would target the wrong row.
        const bigPk = '9007199254740993';
        const bigVal = '12345678901234567890';
        const idCol = col('id', 'BigInt!', true);
        const amountCol = col('amount', 'BigInt');
        const ledger = tbl('ledger', ['id'], [idCol, amountCol]);

        it('keeps a BigInt PK as a string on update so the WHERE targets the exact row', async () => {
            const { query, wrapper } = createHarness();
            const { result } = renderHook(
                () => useTableMutation(ledger, editCols(amountCol), [idCol], bigPk),
                { wrapper },
            );

            await result.current.update({ amount: bigVal });

            const [, variables] = query.mock.calls[0];
            expect((variables as { detail: Record<string, unknown> }).detail).toEqual({
                id: bigPk,
                amount: bigVal,
            });
        });

        it('keeps a BigInt data value as a string on insert', async () => {
            const { query, wrapper } = createHarness();
            const { result } = renderHook(
                () => useTableMutation(ledger, editCols(amountCol), [idCol]),
                { wrapper },
            );

            await result.current.insert({ amount: bigPk });

            const detail = (query.mock.calls[0][1] as { detail: Record<string, unknown> }).detail;
            expect(detail.amount).toBe(bigPk);
        });

        it('clears an emptied BigInt field with null on update and omits it on insert', async () => {
            const { query, wrapper } = createHarness();
            const { result } = renderHook(
                () => useTableMutation(ledger, editCols(amountCol), [idCol], bigPk),
                { wrapper },
            );

            await result.current.update({ amount: '' });
            expect((query.mock.calls[0][1] as { detail: Record<string, unknown> }).detail.amount).toBeNull();

            const { query: q2, wrapper: w2 } = createHarness();
            const { result: r2 } = renderHook(
                () => useTableMutation(ledger, editCols(amountCol), [idCol]),
                { wrapper: w2 },
            );
            await r2.current.insert({ amount: '' });
            expect((q2.mock.calls[0][1] as { detail: Record<string, unknown> }).detail.amount).toBeUndefined();
        });
    });

    describe('empty numeric fields on insert (DB defaults must apply)', () => {
        const idCol = col('id', 'Int!', true);
        const qtyCol = col('qty', 'Int');
        const items = tbl('items', ['id'], [idCol, qtyCol]);

        it('omits an empty numeric field on insert instead of sending an explicit null', async () => {
            const { query, wrapper } = createHarness();
            const { result } = renderHook(
                () => useTableMutation(items, editCols(qtyCol), [idCol]),
                { wrapper },
            );

            await result.current.insert({ qty: '' });

            const detail = (query.mock.calls[0][1] as { detail: Record<string, unknown> }).detail;
            // undefined is dropped by JSON serialization, so the column is
            // absent from the payload and the DB default applies.
            expect(detail.qty).toBeUndefined();
        });

        it('still clears an emptied numeric field with null on update', async () => {
            const { query, wrapper } = createHarness();
            const { result } = renderHook(
                () => useTableMutation(items, editCols(qtyCol), [idCol], '5'),
                { wrapper },
            );

            await result.current.update({ qty: '' });

            const detail = (query.mock.calls[0][1] as { detail: Record<string, unknown> }).detail;
            expect(detail.qty).toBeNull();
        });

        it('coerces a non-empty numeric field with + on both paths', async () => {
            const { query, wrapper } = createHarness();
            const { result } = renderHook(
                () => useTableMutation(items, editCols(qtyCol), [idCol]),
                { wrapper },
            );

            await result.current.insert({ qty: '7' });

            expect((query.mock.calls[0][1] as { detail: Record<string, unknown> }).detail.qty).toBe(7);
        });
    });

    describe('cache invalidation after a write', () => {
        const idCol = col('id', 'Int!', true);
        const nameCol = col('name', 'String');
        const users = tbl('users', ['id'], [idCol, nameCol]);

        function seedCaches(queryClient: QueryClient) {
            const keys = {
                editRecordSame: ['editRecord', 'users', '1'],
                editRecordOther: ['editRecord', 'orders', '9'],
                tableDataOther: ['tableData', 'orders', 'q', {}],
                tableRefSame: ['tableRef', 'users', 'name', ''],
                tableRefOther: ['tableRef', 'orders', 'sku', ''],
                tableRefValueSame: ['tableRefValue', 'users', 'name', '1'],
                rowCounts: ['tableRowCounts', ['users', 'orders']],
                m2mRows: ['m2mRows', 'user_roles', 'roles', '1'],
            } as const;
            for (const key of Object.values(keys)) queryClient.setQueryData(key as unknown as unknown[], {});
            return keys;
        }

        it('invalidates editRecord, other tables, ref lookups, counts, and m2m rows after update', async () => {
            const { wrapper, queryClient } = createHarness();
            const keys = seedCaches(queryClient);
            const { result } = renderHook(
                () => useTableMutation(users, editCols(nameCol), [idCol], '1'),
                { wrapper },
            );

            await result.current.update({ name: 'Ada' });

            await waitFor(() => {
                // The critical data-loss path: the edit dialog must not serve the
                // pre-save row when reopened within staleTime.
                expect(queryClient.getQueryState(keys.editRecordSame as unknown as unknown[])?.isInvalidated).toBe(true);
            });
            expect(queryClient.getQueryState(keys.editRecordOther as unknown as unknown[])?.isInvalidated).toBe(true);
            expect(queryClient.getQueryState(keys.tableDataOther as unknown as unknown[])?.isInvalidated).toBe(true);
            expect(queryClient.getQueryState(keys.tableRefSame as unknown as unknown[])?.isInvalidated).toBe(true);
            expect(queryClient.getQueryState(keys.tableRefValueSame as unknown as unknown[])?.isInvalidated).toBe(true);
            expect(queryClient.getQueryState(keys.rowCounts as unknown as unknown[])?.isInvalidated).toBe(true);
            expect(queryClient.getQueryState(keys.m2mRows as unknown as unknown[])?.isInvalidated).toBe(true);
            // Ref lookups against an unrelated table stay cached.
            expect(queryClient.getQueryState(keys.tableRefOther as unknown as unknown[])?.isInvalidated).toBe(false);
        });

        it('invalidates the same families after insert', async () => {
            const { wrapper, queryClient } = createHarness();
            const keys = seedCaches(queryClient);
            const { result } = renderHook(
                () => useTableMutation(users, editCols(nameCol), [idCol]),
                { wrapper },
            );

            await result.current.insert({ name: 'Grace' });

            await waitFor(() => {
                expect(queryClient.getQueryState(keys.editRecordSame as unknown as unknown[])?.isInvalidated).toBe(true);
            });
            expect(queryClient.getQueryState(keys.rowCounts as unknown as unknown[])?.isInvalidated).toBe(true);
            expect(queryClient.getQueryState(keys.tableRefOther as unknown as unknown[])?.isInvalidated).toBe(false);
        });
    });
});
