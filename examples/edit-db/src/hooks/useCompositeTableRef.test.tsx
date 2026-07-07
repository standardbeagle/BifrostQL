import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React from 'react';
import type { Column, Schema, Table } from '../types/schema';

const fetcherQuery = vi.fn();
vi.mock('../common/fetcher', () => ({ useFetcher: () => ({ query: fetcherQuery }) }));

import { useCompositeTableRef } from './useCompositeTableRef';

function col(name: string, opts: Partial<Column> = {}): Column {
    return {
        dbName: name,
        graphQlName: name,
        name,
        label: name,
        paramType: 'String',
        dbType: 'nvarchar',
        isPrimaryKey: false,
        isIdentity: false,
        isNullable: true,
        isReadOnly: false,
        metadata: {},
        ...opts,
    };
}

function table(name: string, opts: Partial<Table> = {}): Table {
    return {
        dbName: name,
        graphQlName: name,
        name,
        label: name,
        labelColumn: 'label',
        primaryKeys: ['tenant_id', 'code'],
        isEditable: true,
        metadata: {},
        columns: [
            col('tenant_id', { isPrimaryKey: true, paramType: 'Int!' }),
            col('code', { isPrimaryKey: true, paramType: 'String!' }),
            col('label'),
        ],
        multiJoins: [],
        singleJoins: [],
        ...opts,
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

describe('useCompositeTableRef', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        fetcherQuery.mockImplementation((query: string) => {
            if (query.includes('CompositeRefValue')) {
                return Promise.resolve({
                    values: {
                        data: [{ tenant_id: 7, code: 'A', label: 'Selected parent' }],
                    },
                });
            }
            return Promise.resolve({ values: { data: [] } });
        });
    });

    it('uses server-compatible and for composite current-value lookup filters', async () => {
        const schema = schemaOf(table('parents'));

        const { result } = renderHook(
            () =>
                useCompositeTableRef(schema, 'parents', ['tenant_id', 'code'], {
                    currentValues: { tenant_id: '7', code: 'A' },
                }),
            { wrapper: createWrapper() },
        );

        await waitFor(() =>
            expect(result.current.data).toEqual([
                {
                    route: '7::A',
                    values: { tenant_id: 7, code: 'A' },
                    label: 'Selected parent',
                },
            ]),
        );

        const lookupCall = fetcherQuery.mock.calls.find(([query]) =>
            String(query).includes('CompositeRefValue'),
        );
        expect(lookupCall).toBeTruthy();
        expect(lookupCall?.[0]).toContain('filter: {and: [');
        expect(lookupCall?.[0]).not.toContain('{_and: [');
        expect(lookupCall?.[1]).toEqual({ k0: 7, k1: 'A' });
    });

    it('does not issue a query when destination names are unsafe', () => {
        const schema = schemaOf(table('parents) { injected'));

        renderHook(
            () => useCompositeTableRef(schema, 'parents) { injected', ['tenant_id', 'code']),
            { wrapper: createWrapper() },
        );

        expect(fetcherQuery).not.toHaveBeenCalled();
    });

    it('does not issue a query when destination columns are unsafe or missing', () => {
        const schema = schemaOf(table('parents'));

        renderHook(
            () => useCompositeTableRef(schema, 'parents', ['tenant_id', 'code) { injected']),
            { wrapper: createWrapper() },
        );
        renderHook(
            () => useCompositeTableRef(schema, 'parents', ['tenant_id', 'missing']),
            { wrapper: createWrapper() },
        );

        expect(fetcherQuery).not.toHaveBeenCalled();
    });
});
