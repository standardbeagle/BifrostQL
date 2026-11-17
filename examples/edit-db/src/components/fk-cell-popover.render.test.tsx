import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { FetcherProvider, type GraphQLFetcher } from '../common/fetcher';
import { FkCellPopover } from './fk-cell-popover';
import type { Table } from '../types/schema';

const customers = {
    dbName: 'customers', graphQlName: 'customers', name: 'customers', label: 'Customers', labelColumn: '',
    primaryKeys: ['id'], isEditable: true, metadata: {}, joins: [], multiJoins: [],
    columns: [
        { dbName: 'id', graphQlName: 'id', name: 'id', label: 'Id', paramType: 'Int!', dbType: 'int', isPrimaryKey: true, isIdentity: true, isNullable: false, isReadOnly: true, metadata: {} },
        { dbName: 'name', graphQlName: 'name', name: 'name', label: 'Name', paramType: 'String', dbType: 'nvarchar', isPrimaryKey: false, isIdentity: false, isNullable: true, isReadOnly: false, metadata: {} },
    ],
} as unknown as Table;

vi.mock('../hooks/useSchema', () => ({
    useSchema: () => ({ findTable: (name: string) => (name === 'customers' ? customers : undefined) }),
}));

function hover(fetcher: GraphQLFetcher) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={client}>
            <FetcherProvider value={fetcher}>
                <FkCellPopover tableName="customers" fkValue={7} filterColumn="id">
                    <button type="button">7</button>
                </FkCellPopover>
            </FetcherProvider>
        </QueryClientProvider>,
    );
    fireEvent.pointerOver(screen.getByRole('button', { name: '7' }));
    fireEvent.pointerEnter(screen.getByRole('button', { name: '7' }));
}

describe('FkCellPopover', () => {
    it('reports a failed lookup instead of calling the key dangling', async () => {
        hover({ query: vi.fn(async () => { throw new Error('Access denied to customers'); }) as GraphQLFetcher['query'] });

        expect(await screen.findByRole('alert', {}, { timeout: 3000 })).toHaveTextContent('Access denied to customers');
        expect(screen.queryByText('Record not found')).not.toBeInTheDocument();
    });

    it('still says "Record not found" when the lookup succeeds with no row', async () => {
        hover({ query: vi.fn(async () => ({ customers: { data: [] } })) as GraphQLFetcher['query'] });

        expect(await screen.findByText('Record not found', {}, { timeout: 3000 })).toBeInTheDocument();
        expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });
});
