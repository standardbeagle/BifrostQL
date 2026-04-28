import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createElement, type ReactNode } from 'react';
import { useDeleteMutation } from './useDeleteMutation';
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
        isNullable: false,
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

describe('useDeleteMutation', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    describe('single-column PK', () => {
        const users = tbl('users', ['id'], [col('id', 'Int!', true), col('name', 'String')]);

        it('accepts a legacy scalar pk and sends {id: <coerced int>}', async () => {
            const { query, wrapper } = createHarness();
            const { result } = renderHook(() => useDeleteMutation(users), { wrapper });

            await result.current.deleteRow('42');

            expect(query).toHaveBeenCalledTimes(1);
            const [mutationText, variables] = query.mock.calls[0];
            expect(mutationText).toContain('mutation deleteSingle($detail: Delete_users)');
            expect(variables).toEqual({ detail: { id: 42 } });
        });

        it('accepts a PkFilter and sends it as-is', async () => {
            const { query, wrapper } = createHarness();
            const { result } = renderHook(() => useDeleteMutation(users), { wrapper });

            await result.current.deleteRow({ id: 99 });

            expect(query.mock.calls[0][1]).toEqual({ detail: { id: 99 } });
        });

        it('batch-deletes with each action wrapping the coerced filter', async () => {
            const { query, wrapper } = createHarness();
            const { result } = renderHook(() => useDeleteMutation(users), { wrapper });

            await result.current.deleteRows(['1', { id: 2 }, 3]);

            expect(query).toHaveBeenCalledTimes(1);
            const variables = query.mock.calls[0][1];
            expect(variables).toEqual({
                actions: [
                    { delete: { id: 1 } },
                    { delete: { id: 2 } },
                    { delete: { id: 3 } },
                ],
            });
        });
    });

    describe('composite PK', () => {
        const enrollment = tbl(
            'enrollment',
            ['student_id', 'course_id'],
            [
                col('student_id', 'Int!', true),
                col('course_id', 'String!', true),
                col('grade', 'String'),
            ],
        );

        it('sends every PK column with the correct type coercion', async () => {
            const { query, wrapper } = createHarness();
            const { result } = renderHook(() => useDeleteMutation(enrollment), { wrapper });

            await result.current.deleteRow({ student_id: '1', course_id: 'cs-101', grade: 'A' });

            // grade (non-PK) must not be in the payload; both PK columns must be coerced.
            expect(query.mock.calls[0][1]).toEqual({
                detail: { student_id: 1, course_id: 'cs-101' },
            });
        });

        it('rejects legacy scalar input for composite PKs by populating only the first column', async () => {
            // Transitional behavior: a scalar input cannot describe a composite PK.
            // The hook keeps the same shape but fills only the first PK column so the mutation
            // will fail server-side with a clear error rather than silently dropping data.
            const { query, wrapper } = createHarness();
            const { result } = renderHook(() => useDeleteMutation(enrollment), { wrapper });

            await result.current.deleteRow('1');

            expect(query.mock.calls[0][1]).toEqual({
                detail: { student_id: 1 },
            });
        });

        it('batch-deletes with composite filters on every action', async () => {
            const { query, wrapper } = createHarness();
            const { result } = renderHook(() => useDeleteMutation(enrollment), { wrapper });

            await result.current.deleteRows([
                { student_id: 1, course_id: 'cs-101' },
                { student_id: 2, course_id: 'cs-202' },
            ]);

            expect(query.mock.calls[0][1]).toEqual({
                actions: [
                    { delete: { student_id: 1, course_id: 'cs-101' } },
                    { delete: { student_id: 2, course_id: 'cs-202' } },
                ],
            });
        });
    });

    describe('type coercion', () => {
        it('truncates floats to Int for Int columns', async () => {
            const { query, wrapper } = createHarness();
            const orders = tbl('orders', ['id'], [col('id', 'Int!', true)]);
            const { result } = renderHook(() => useDeleteMutation(orders), { wrapper });

            await result.current.deleteRow({ id: 42.9 });

            expect(query.mock.calls[0][1]).toEqual({ detail: { id: 42 } });
        });

        it('keeps string PKs as strings even when the value looks numeric', async () => {
            const { query, wrapper } = createHarness();
            const codes = tbl('codes', ['code'], [col('code', 'String!', true)]);
            const { result } = renderHook(() => useDeleteMutation(codes), { wrapper });

            await result.current.deleteRow({ code: '42' });

            expect(query.mock.calls[0][1]).toEqual({ detail: { code: '42' } });
        });

        it('parses Float PKs as Float', async () => {
            const { query, wrapper } = createHarness();
            const prices = tbl('prices', ['price'], [col('price', 'Float!', true)]);
            const { result } = renderHook(() => useDeleteMutation(prices), { wrapper });

            await result.current.deleteRow({ price: '3.14' });

            expect(query.mock.calls[0][1]).toEqual({ detail: { price: 3.14 } });
        });
    });

    describe('error surfacing', () => {
        it('surfaces mutation errors via the hook result', async () => {
            const query = vi.fn().mockRejectedValue(new Error('boom'));
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

            const users = tbl('users', ['id'], [col('id', 'Int!', true)]);
            const { result } = renderHook(() => useDeleteMutation(users), { wrapper });

            await expect(result.current.deleteRow(1)).rejects.toThrow('boom');
            await waitFor(() => expect(result.current.error?.message).toBe('boom'));
        });
    });
});
