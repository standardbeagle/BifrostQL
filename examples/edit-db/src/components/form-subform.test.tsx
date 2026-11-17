import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
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

  it('keeps the child form browsing inside the selected parent', async () => {
    const browses: { text: string; variables?: Record<string, unknown> }[] = [];
    const line = { tenant_id: 7, order_id: 42, line_no: 1, qty: 3 };
    const query = vi.fn(async (text: string, variables?: Record<string, unknown>) => {
      if (text.includes('FormSubform_order_lines')) return { order_lines: { data: [line] } };
      if (text.includes('GetSingleRow_order_lines')) return { value: { data: [line] } };
      if (text.includes('FormBrowse_order_lines')) { browses.push({ text, variables }); return { order_lines: { total: 1, data: [line] } }; }
      throw new Error(`unexpected query: ${text}`);
    });

    render(createElement(FormSubformPanel, { parent, child, subform, row: { tenant_id: 7, order_id: 42 } }), { wrapper: wrapper({ query: query as GraphQLFetcher['query'] }) });
    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    await screen.findByLabelText('qty');
    expect(screen.getByLabelText('Next record')).toBeDisabled();

    fireEvent.click(screen.getByLabelText('First record'));
    await waitFor(() => expect(browses.length).toBeGreaterThan(0));
    for (const b of browses) {
      expect(b.text).toContain('filter: {and: [{tenant_id: {_eq: $parent_tenant_id}}, {order_id: {_eq: $parent_order_id}}]}');
      expect(b.variables).toMatchObject({ parent_tenant_id: 7, parent_order_id: 42, limit: 1, offset: 0 });
    }
  });

  it('asks before deleting a child row and shows a failed delete instead of dropping it', async () => {
    const deletes: unknown[] = [];
    const query = vi.fn(async (text: string, variables?: Record<string, unknown>) => {
      if (text.includes('FormSubform_order_lines')) return { order_lines: { data: [{ tenant_id: 7, order_id: 42, line_no: 1, qty: 3 }] } };
      if (text.includes('(delete:')) { deletes.push(variables); throw new Error('conflicted with the REFERENCE constraint'); }
      throw new Error(`unexpected query: ${text}`);
    });
    render(createElement(FormSubformPanel, { parent, child, subform, row: { tenant_id: 7, order_id: 42 } }), { wrapper: wrapper({ query: query as GraphQLFetcher['query'] }) });

    await screen.findByText('3');
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    const dialog = await screen.findByRole('dialog');
    expect(deletes).toEqual([]);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(deletes).toHaveLength(1));
    expect(await screen.findByRole('alert')).toHaveTextContent('Delete failed: conflicted with the REFERENCE constraint');
  });
});
