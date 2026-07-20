import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { createElement, type ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { FetcherProvider, type GraphQLFetcher } from '../common/fetcher';
import { FormSubformPanel } from './form-subform';
import type { Column, Table } from '../types/schema';
import type { FormSubform } from '../lib/form-definition';

const column = (name: string, opts: Partial<Column> = {}): Column => ({
  dbName: name, graphQlName: name, name, label: name, paramType: 'Int!', dbType: 'int',
  isPrimaryKey: false, isIdentity: false, isNullable: false, isReadOnly: false, metadata: {}, ...opts,
});
const table = (name: string, columns: Column[], primaryKeys: string[]): Table => ({
  dbName: name, graphQlName: name, name, label: name, labelColumn: primaryKeys.at(0) ?? '', primaryKeys,
  isEditable: true, metadata: {}, columns, multiJoins: [], singleJoins: [],
});

const parent = table('orders', [column('tenant_id'), column('order_id')], ['tenant_id', 'order_id']);
const child = table('order_lines', [column('tenant_id'), column('order_id'), column('line_no', { isPrimaryKey: true }), column('qty')], ['tenant_id', 'order_id', 'line_no']);
const subform: FormSubform = {
  relationship: 'lines', childTable: 'order_lines', parentColumns: ['tenant_id', 'order_id'],
  childColumns: ['tenant_id', 'order_id'], label: 'Lines', mode: 'grid',
};

function wrapper(fetcher: GraphQLFetcher) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return ({ children }: { children: ReactNode }) => createElement(QueryClientProvider, { client }, createElement(FetcherProvider, { value: fetcher }, children));
}

describe('FormSubformPanel', () => {
  it('creates and edits child rows through FormRunnerView with every composite FK prefilled and hidden', async () => {
    const inserted: Record<string, unknown>[] = [];
    const query = vi.fn(async (text: string, variables?: Record<string, unknown>) => {
      if (text.includes('FormSubform_order_lines')) return { order_lines: { data: [{ tenant_id: 7, order_id: 42, line_no: 1, qty: 3 }] } };
      if (text.includes('GetSingleRow_order_lines')) return { value: { data: [{ tenant_id: 7, order_id: 42, line_no: 1, qty: 3 }] } };
      if (text.includes('(insert:')) { inserted.push(variables?.detail as Record<string, unknown>); return { order_lines: 1 }; }
      if (text.includes('(update:')) return { order_lines: 1 };
      throw new Error(`unexpected query: ${text}`);
    });

    render(createElement(FormSubformPanel, { parent, child, subform, row: { tenant_id: 7, order_id: 42 } }), { wrapper: wrapper({ query: query as GraphQLFetcher['query'] }) });

    await screen.findByText('3');
    fireEvent.click(screen.getByRole('button', { name: 'Add Lines' }));
    await screen.findByLabelText('line_no');
    expect(screen.queryByLabelText('tenant_id')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('order_id')).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('line_no'), { target: { value: '2' } });
    fireEvent.change(screen.getByLabelText('qty'), { target: { value: '8' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));
    await waitFor(() => expect(inserted).toEqual([{ tenant_id: 7, order_id: 42, line_no: 2, qty: 8 }]));

    fireEvent.click(screen.getByLabelText('Close form'));
    await screen.findByRole('button', { name: 'Edit' });
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    await screen.findByLabelText('qty');
    expect(screen.queryByLabelText('tenant_id')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('order_id')).not.toBeInTheDocument();
  });
});
