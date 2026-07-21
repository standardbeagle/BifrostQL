import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { describe, expect, it, vi } from 'vitest';
import { FetcherProvider, type GraphQLFetcher } from '@/common/fetcher';
import { GroupedGridSummary } from './grouped-grid-summary';
import type { GroupingRow } from '@/lib/grid-grouping';

function renderSummary(fetcher: GraphQLFetcher, rows: GroupingRow[] = [{ value: null, count: 2, sum: undefined }], pageSize = 20) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={client}>
            <FetcherProvider value={fetcher}>
                <GroupedGridSummary
                    label="Status"
                    rows={rows}
                    pageSize={pageSize}
                    memberRequest={(value) => ({
                        query: 'query Members { orders { total data { status } } }',
                        variables: { filter: { status: value === null ? { _null: true } : { _eq: value } } },
                        responseKey: 'orders',
                    })}
                />
            </FetcherProvider>
        </QueryClientProvider>,
    );
}

describe('GroupedGridSummary', () => {
    it('expands a null aggregate bucket through its server-only member request', async () => {
        const query = vi.fn(async () => ({ orders: { total: 2, data: [{ status: null }] } }));
        renderSummary({ query: query as GraphQLFetcher['query'] });

        fireEvent.click(screen.getByRole('button', { name: '(null)' }));

        await waitFor(() => expect(query).toHaveBeenCalledWith(
            expect.any(String),
            { filter: { status: { _null: true } } },
            expect.objectContaining({ signal: expect.any(AbortSignal) }),
        ));
        expect(await screen.findByText('2 matching members')).toBeInTheDocument();
        expect(screen.getByText(/"status": null/)).toBeInTheDocument();
    });

    it('pages aggregate headers by the active grid page size rather than a hard-coded group limit', () => {
        const rows = Array.from({ length: 11 }, (_, index) => ({ value: `status-${index}`, count: 1, sum: undefined }));
        renderSummary({ query: vi.fn() }, rows, 10);

        expect(screen.getByText('status-0')).toBeInTheDocument();
        expect(screen.queryByText('status-10')).not.toBeInTheDocument();
        fireEvent.click(screen.getByRole('button', { name: 'Next groups' }));
        expect(screen.getByText('status-10')).toBeInTheDocument();
        expect(screen.getByText('Group page 2 of 2')).toBeInTheDocument();
    });
});
