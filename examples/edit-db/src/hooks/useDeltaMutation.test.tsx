import { describe, it, expect, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import '@testing-library/jest-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createElement, type ReactNode } from 'react';
import { useDeltaMutation, deltaChangeCount } from './useDeltaMutation';
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
    const query = vi.fn(async (_query: string, _variables?: Record<string, unknown>) => 3 as unknown);
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
    return { query, wrapper };
}

describe('useDeltaMutation', () => {
    const orders = tbl('orders', ['order_id', 'line_no'], [
        col('order_id', 'BigInt!', true),
        col('line_no', 'Int!', true),
        col('status', 'String'),
    ]);

    it('sends the collection-diff document as one delta mutation', async () => {
        const { query, wrapper } = createHarness();
        const { result } = renderHook(() => useDeltaMutation(orders), { wrapper });

        // Composite-PK rows carry EVERY key column; BigInt keys stay strings so a
        // key above 2^53 targets the exact row it was read from.
        const delta = {
            inserted: [{ status: 'new' }],
            updated: [{ order_id: '9007199254740993', line_no: 2, status: 'paid' }],
            deleted: [{ order_id: '9007199254740993', line_no: 3 }],
        };
        await result.current.saveDelta(delta);

        expect(query).toHaveBeenCalledTimes(1);
        expect(query.mock.calls[0][0]).toContain('mutation saveDelta($delta: orders_delta)');
        expect(query.mock.calls[0][0]).toContain('orders(delta: $delta)');
        expect(query.mock.calls[0][1]).toEqual({ delta });
    });

    it('refuses a hostile table name before building any mutation text', () => {
        const { wrapper } = createHarness();
        const evil = tbl('orders){x} mutation{', ['id'], [col('id', 'Int!', true)]);
        expect(() => renderHook(() => useDeltaMutation(evil), { wrapper })).toThrow();
    });

    it('counts changes across all three sections', () => {
        expect(deltaChangeCount({})).toBe(0);
        expect(deltaChangeCount({ inserted: [{}], updated: [{}, {}], deleted: [{}] })).toBe(4);
    });
});
