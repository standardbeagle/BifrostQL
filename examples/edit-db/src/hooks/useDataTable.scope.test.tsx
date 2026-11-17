import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { createElement, type ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Column, SchemaContextValue, Table } from '../types/schema';

// The URL, schema and transport are the hook's boundaries; everything between
// them (query builders, filter parsing, grouping) is the real code.
let currentQuery = '';
const navigate = vi.fn();
vi.mock('./usePath', () => ({
    useSearchParams: () => ({ search: new URLSearchParams(currentQuery), hash: '' }),
    useNavigate: () => navigate,
    Link: () => null,
}));
let schema: SchemaContextValue;
vi.mock('./useSchema', () => ({ useSchema: () => schema }));
const query = vi.fn();
vi.mock('../common/fetcher', () => ({ useFetcher: () => ({ query }) }));

import { useDataTable } from './useDataTable';
import { serializeColumnFilters } from '../lib/query-builder';

const col = (name: string, paramType: string, opts: Partial<Column> = {}): Column => ({
    dbName: name, graphQlName: name, name, label: name, paramType,
    dbType: paramType.startsWith('Int') ? 'int' : 'nvarchar',
    isPrimaryKey: false, isIdentity: false, isNullable: true, isReadOnly: false, metadata: {}, ...opts,
});
const tbl = (name: string, columns: Column[], extra: Partial<Table> = {}): Table => ({
    dbName: name, graphQlName: name, name, label: name, labelColumn: 'id', primaryKeys: ['id'],
    isEditable: true, metadata: {}, columns, multiJoins: [], singleJoins: [], ...extra,
});

const orders = tbl('orders', [col('id', 'Int!', { isPrimaryKey: true }), col('customer_id', 'Int'), col('status', 'String')]);
const customers = tbl('customers', [col('id', 'Int!', { isPrimaryKey: true }), col('region', 'String')], {
    multiJoins: [{ name: 'orders', fieldName: 'orders', sourceColumnNames: ['id'], destinationTable: 'orders', destinationColumnNames: ['customer_id'] }],
});

function wrapper({ children }: { children: ReactNode }) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return createElement(QueryClientProvider, { client }, children);
}

beforeEach(() => {
    vi.clearAllMocks();
    schema = { loading: false, error: null, data: [customers, orders], findTable: (n) => [customers, orders].find((t) => t.name === n) };
    query.mockResolvedValue({ orders: { total: 0, data: [] } });
});

describe('useDataTable drill grid filter scope', () => {
    it("ignores the main grid's URL filters and grouping", async () => {
        // The main grid (customers) has a header filter, a column filter and a
        // grouping on `region` — none of which exists on the child table.
        currentQuery = new URLSearchParams({
            filter: '["region", "_eq", "west", "String"]',
            cf: serializeColumnFilters([{ id: 'region', value: { operator: '_eq', value: 'west' } }]),
            gb: 'status',
        }).toString();
        const { result } = renderHook(() => useDataTable(orders, '7', 'customers'), { wrapper });

        await waitFor(() => expect(query).toHaveBeenCalled());
        expect(result.current.error).toBeNull();
        expect(result.current.grouping).toBeNull();
        for (const [text] of query.mock.calls) {
            expect(text).not.toContain('Aggregate');
            expect(text).not.toContain('region');
        }
    });

    it('keeps its own column filters out of the URL and applies them inside the drill', async () => {
        currentQuery = '';
        const { result } = renderHook(() => useDataTable(orders, '7', 'customers'), { wrapper });
        await waitFor(() => expect(query).toHaveBeenCalled());

        act(() => result.current.onColumnFiltersChange([{ id: 'status', value: { operator: '_eq', value: 'open' } }]));

        expect(navigate).not.toHaveBeenCalled();
        await waitFor(() => expect(query.mock.calls.some(([text]) => String(text).includes('status: {_eq: $cf_status_0}'))).toBe(true));
        const [text, variables] = query.mock.calls.find(([t]) => String(t).includes('$cf_status_0'))!;
        expect(text).toContain('customer_id: { _eq: $id }');
        expect(variables).toMatchObject({ id: 7, cf_status_0: 'open' });
        expect(result.current.columnFilters).toEqual([{ id: 'status', value: { operator: '_eq', value: 'open' } }]);
    });
});
