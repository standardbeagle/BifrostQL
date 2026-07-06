import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React from 'react';
import type { Column, Schema, Table } from '../types/schema';

const fetcherQuery = vi.fn();
vi.mock('../common/fetcher', () => ({ useFetcher: () => ({ query: fetcherQuery }) }));

import { useTableRef, useTableRefValue } from './useTableRef';
import { TABLE_REF_LIMIT, TABLE_REF_SEARCH_LIMIT } from '../lib/table-ref';

function col(name: string, opts: Partial<Column> = {}): Column {
    return {
        dbName: name, graphQlName: name, name, label: name, paramType: 'String',
        dbType: 'nvarchar', isPrimaryKey: false, isIdentity: false, isNullable: true,
        isReadOnly: false, metadata: {}, ...opts,
    };
}

function table(name: string, opts: Partial<Table> = {}): Table {
    return {
        dbName: name, graphQlName: name, name, label: name, labelColumn: 'name',
        primaryKeys: ['pk'], isEditable: true, metadata: {},
        columns: [col('pk', { isPrimaryKey: true, paramType: 'Int!' }), col('name')],
        multiJoins: [], singleJoins: [], ...opts,
    };
}

function schemaOf(...tables: Table[]): Schema {
    return {
        loading: false,
        error: null,
        data: tables,
        findTable: (n: string) => tables.find((t) => t.graphQlName === n),
    };
}

const createWrapper = () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return ({ children }: { children: React.ReactNode }) =>
        React.createElement(QueryClientProvider, { client }, children);
};

describe('useTableRef', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        fetcherQuery.mockResolvedValue({ values: { data: [{ key: '1', label: 'Alice' }] } });
    });

    it('fetches the option window with the default limit when no search is set', async () => {
        const schema = schemaOf(table('users'));
        const { result } = renderHook(() => useTableRef(schema, 'users', 'pk'), { wrapper: createWrapper() });

        await waitFor(() => expect(result.current.loading).toBe(false));

        expect(result.current.data).toEqual([{ key: '1', label: 'Alice' }]);
        expect(result.current.serverSearch).toBe(true);
        const [query, variables] = fetcherQuery.mock.calls[0];
        expect(query).toContain('sort: [name_asc]');
        expect(query).not.toContain('_contains');
        expect(variables).toEqual({ limit: TABLE_REF_LIMIT });
    });

    it('narrows server-side with _contains and the search limit for a String label column', async () => {
        const schema = schemaOf(table('users'));
        const { result } = renderHook(() => useTableRef(schema, 'users', 'pk', 'ali'), { wrapper: createWrapper() });

        await waitFor(() => expect(result.current.loading).toBe(false));

        const [query, variables] = fetcherQuery.mock.calls[0];
        expect(query).toContain('filter: {name: {_contains: $search}}');
        expect(variables).toEqual({ limit: TABLE_REF_SEARCH_LIMIT, search: 'ali' });
    });

    it('ignores the search term (single client-side window) for a non-String label column', async () => {
        const orders = table('orders', {
            labelColumn: 'orderNo',
            columns: [col('pk', { isPrimaryKey: true, paramType: 'Int!' }), col('orderNo', { paramType: 'Int' })],
        });
        const { result } = renderHook(
            () => useTableRef(schemaOf(orders), 'orders', 'pk', '42'),
            { wrapper: createWrapper() },
        );

        await waitFor(() => expect(result.current.loading).toBe(false));

        expect(result.current.serverSearch).toBe(false);
        const [query, variables] = fetcherQuery.mock.calls[0];
        expect(query).not.toContain('_contains');
        expect(variables).toEqual({ limit: TABLE_REF_LIMIT });
    });

    it('surfaces fetch errors instead of returning silent empty data', async () => {
        fetcherQuery.mockRejectedValue(new Error('boom'));
        const schema = schemaOf(table('users'));
        const { result } = renderHook(() => useTableRef(schema, 'users', 'pk'), { wrapper: createWrapper() });

        await waitFor(() => expect(result.current.error).toBeTruthy());
        expect((result.current.error as Error).message).toBe('boom');
        expect(result.current.data).toEqual([]);
    });
});

describe('useTableRefValue', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        fetcherQuery.mockResolvedValue({ values: { data: [{ key: '7', label: 'Grace' }] } });
    });

    it('resolves a single row by key, coercing the key to the column type', async () => {
        const schema = schemaOf(table('users'));
        const { result } = renderHook(
            () => useTableRefValue(schema, 'users', 'pk', '7'),
            { wrapper: createWrapper() },
        );

        await waitFor(() => expect(result.current.value).not.toBeNull());

        expect(result.current.value).toEqual({ key: '7', label: 'Grace' });
        const [query, variables] = fetcherQuery.mock.calls[0];
        expect(query).toContain('filter: {pk: {_eq: $key}}');
        expect(variables).toEqual({ key: 7 }); // '7' coerced for the Int! column
    });

    it('stays idle when the key is null or empty', async () => {
        const schema = schemaOf(table('users'));
        const { result, rerender } = renderHook(
            ({ key }: { key: unknown }) => useTableRefValue(schema, 'users', 'pk', key),
            { wrapper: createWrapper(), initialProps: { key: null as unknown } },
        );

        expect(result.current.loading).toBe(false);
        expect(result.current.value).toBeNull();
        rerender({ key: '' });
        expect(fetcherQuery).not.toHaveBeenCalled();
    });
});
