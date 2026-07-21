import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { describe, expect, it, vi } from 'vitest';
import { FetcherProvider, type GraphQLFetcher } from '@/common/fetcher';
import { GroupedGridSummary } from './grouped-grid-summary';

function renderSummary(fetcher: GraphQLFetcher, rows = [{ value: null, count: 2, sum: undefined }]) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={client}>
            <FetcherProvider value={fetcher}>
                <GroupedGridSummary
                    label="Status"
                    rows={rows}
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
        renderSummary({ query });

        fireEvent.click(screen.getByRole('button', { name: '(null)' }));

        await waitFor(() => expect(query).toHaveBeenCalledWith(
            expect.any(String),
            { filter: { status: { _null: true } } },
            expect.objectContaining({ signal: expect.any(AbortSignal) }),
        ));
        expect(await screen.findByText('2 matching members')).toBeInTheDocument();
        expect(screen.getByText(/"status": null/)).toBeInTheDocument();
    });

    it('pages aggregate headers rather than the flat row grid', () => {
        const rows = Array.from({ length: 21 }, (_, index) => ({ value: `status-${index}`, count: 1, sum: undefined }));
        renderSummary({ query: vi.fn() }, rows);

        expect(screen.getByText('status-0')).toBeInTheDocument();
        expect(screen.queryByText('status-20')).not.toBeInTheDocument();
        fireEvent.click(screen.getByRole('button', { name: 'Next groups' }));
        expect(screen.getByText('status-20')).toBeInTheDocument();
        expect(screen.getByText('Group page 2 of 2')).toBeInTheDocument();
    });
});
