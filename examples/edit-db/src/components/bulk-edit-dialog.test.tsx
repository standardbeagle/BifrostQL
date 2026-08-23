import { describe, it, expect, vi } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createElement, type ReactNode } from 'react';
import { BulkEditDialog } from './bulk-edit-dialog';
import { FetcherProvider, type GraphQLFetcher } from '../common/fetcher';
import { ToastProvider } from '../hooks/useToast';
import type { Column, Table } from '../types/schema';

function col(name: string, paramType: string, opts: Partial<Column> = {}): Column {
    return {
        dbName: name,
        graphQlName: name,
        name,
        label: name,
        paramType,
        dbType: '',
        isPrimaryKey: false,
        isIdentity: false,
        isNullable: false,
        isReadOnly: false,
        metadata: {},
        ...opts,
    } as Column;
}

const orders: Table = {
    dbName: 'orders',
    graphQlName: 'orders',
    name: 'orders',
    label: 'Orders',
    labelColumn: 'status',
    primaryKeys: ['order_id', 'line_no'],
    isEditable: true,
    metadata: {},
    columns: [
        col('order_id', 'BigInt!', { isPrimaryKey: true }),
        col('line_no', 'Int!', { isPrimaryKey: true }),
        col('status', 'String!'),
        col('note', 'String', { isNullable: true }),
    ],
    multiJoins: [],
    singleJoins: [],
} as Table;

function harness(queryImpl: (q: string, v?: Record<string, unknown>) => Promise<unknown>) {
    const query = vi.fn(queryImpl);
    const fetcher: GraphQLFetcher = { query: query as unknown as GraphQLFetcher['query'] };
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    const wrapper = ({ children }: { children: ReactNode }) =>
        createElement(
            QueryClientProvider,
            { client: queryClient },
            createElement(ToastProvider, null,
                createElement(FetcherProvider, { value: fetcher }, children)),
        );
    return { query, wrapper };
}

describe('BulkEditDialog', () => {
    it('re-reads the selected rows fresh, merges the change, and saves ONE delta document', async () => {
        const { query, wrapper } = harness(async (q: string) => {
            if (q.includes('GetRowsByPk_orders')) {
                return {
                    value: {
                        data: [
                            { order_id: '9007199254740993', line_no: 1, status: 'new' },
                            { order_id: '9007199254740993', line_no: 2, status: 'old' },
                        ],
                    },
                };
            }
            return 2;
        });

        render(createElement(BulkEditDialog, {
            table: orders,
            pks: [
                { order_id: '9007199254740993', line_no: 1 },
                { order_id: '9007199254740993', line_no: 2 },
            ],
            onClose: vi.fn(),
        }), { wrapper: wrapper as never });

        fireEvent.click(screen.getByLabelText('status'));
        const inputs = screen.getAllByRole('textbox').filter((el) => !(el as HTMLInputElement).disabled);
        fireEvent.change(inputs[0], { target: { value: 'archived' } });
        fireEvent.click(screen.getByRole('button', { name: /Apply to 2 rows/ }));

        await waitFor(() => expect(query).toHaveBeenCalledTimes(2));
        // Call 1: one fresh-read query over BOTH composite keys.
        expect(query.mock.calls[0][0]).toContain('GetRowsByPk_orders');
        // Call 2: ONE delta document — every payload echoes required columns, keys
        // stay composite and BigInt-string, the change overlays each row.
        expect(query.mock.calls[1][0]).toContain('mutation saveDelta($delta: orders_delta)');
        expect(query.mock.calls[1][1]).toEqual({
            delta: {
                updated: [
                    { order_id: '9007199254740993', line_no: 1, status: 'archived' },
                    { order_id: '9007199254740993', line_no: 2, status: 'archived' },
                ],
            },
        });
    });

    it('refuses to save when a selected row no longer exists — nothing is written', async () => {
        const { query, wrapper } = harness(async (q: string) => {
            if (q.includes('GetRowsByPk_orders')) {
                return { value: { data: [{ order_id: '1', line_no: 1, status: 'new' }] } };
            }
            return 0;
        });

        render(createElement(BulkEditDialog, {
            table: orders,
            pks: [{ order_id: '1', line_no: 1 }, { order_id: '1', line_no: 2 }],
            onClose: vi.fn(),
        }), { wrapper: wrapper as never });

        fireEvent.click(screen.getByLabelText('status'));
        const inputs = screen.getAllByRole('textbox').filter((el) => !(el as HTMLInputElement).disabled);
        fireEvent.change(inputs[0], { target: { value: 'x' } });
        fireEvent.click(screen.getByRole('button', { name: /Apply to 2 rows/ }));

        // The message shows twice — inline in the dialog AND as the error toast.
        await waitFor(() => expect(screen.getAllByText(/still exist/).length).toBeGreaterThan(0));
        // Only the fresh read fired; no delta mutation was sent.
        expect(query).toHaveBeenCalledTimes(1);
    });

    it('requires at least one chosen field', async () => {
        const { query, wrapper } = harness(async () => 0);
        render(createElement(BulkEditDialog, {
            table: orders,
            pks: [{ order_id: '1', line_no: 1 }],
            onClose: vi.fn(),
        }), { wrapper: wrapper as never });

        fireEvent.click(screen.getByRole('button', { name: /Apply to 1 row/ }));

        expect(await screen.findByText(/at least one field/)).toBeInTheDocument();
        expect(query).not.toHaveBeenCalled();
    });
});
