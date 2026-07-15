import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createElement, type ReactNode } from 'react';
import { FormRunnerView, resolveDefinitionTable } from './form-runner';
import { FetcherProvider, type GraphQLFetcher } from '../common/fetcher';
import type { Column, Schema, Table } from '../types/schema';
import type { FormDefinition } from '../lib/form-definition';

function col(name: string, paramType: string, opts: Partial<Column> = {}): Column {
  return {
    dbName: name,
    graphQlName: name,
    name,
    label: name,
    paramType,
    dbType: paramType === 'Int' || paramType === 'Int!' ? 'int' : 'nvarchar',
    isPrimaryKey: false,
    isIdentity: false,
    isNullable: true,
    isReadOnly: false,
    metadata: {},
    ...opts,
  };
}

function tbl(name: string, primaryKeys: string[], columns: Column[]): Table {
  return {
    dbName: name,
    graphQlName: name,
    name,
    label: name,
    labelColumn: columns[0]?.name ?? 'id',
    primaryKeys,
    isEditable: true,
    metadata: {},
    columns,
    multiJoins: [],
    singleJoins: [],
  };
}

/**
 * A stateful in-memory fetcher backing one table. Dispatches on the query text
 * the runner's builders produce, so a test exercises the real hooks, real query
 * builders, and real row-id/composite-PK handling end to end — not mocks.
 */
function makeDb(table: Table, seed: Record<string, unknown>[]) {
  const pkCols = table.primaryKeys;
  const identity = table.columns.find((c) => c.isIdentity)?.name;
  let rows = seed.map((r) => ({ ...r }));
  let nextId = Math.max(0, ...rows.map((r) => Number(r[identity ?? ''] ?? 0))) + 1;

  const matchesPk = (row: Record<string, unknown>, key: Record<string, unknown>, prefix = '') =>
    pkCols.every((c) => String(row[c]) === String(key[`${prefix}${c}`]));

  const query = vi.fn(async (text: string, vars?: Record<string, unknown>) => {
    const v = vars ?? {};
    if (text.includes('FormBrowse_')) {
      const offset = Number(v.offset ?? 0);
      const row = rows[offset];
      return { [table.name]: { total: rows.length, data: row ? [row] : [] } };
    }
    if (text.includes('GetSingleRow_')) {
      const match = rows.find((r) => matchesPk(r, v, 'pk_'));
      return { value: { data: match ? [match] : [] } };
    }
    if (text.includes('(insert:')) {
      const detail = v.detail as Record<string, unknown>;
      const row = { ...detail };
      if (identity) row[identity] = nextId++;
      rows.push(row);
      return { [table.name]: identity ? row[identity] : rows.length };
    }
    if (text.includes('(update:')) {
      const detail = v.detail as Record<string, unknown>;
      const idx = rows.findIndex((r) => matchesPk(r, detail));
      if (idx >= 0) rows[idx] = { ...rows[idx], ...detail };
      return { [table.name]: pkCols.length === 1 ? detail[pkCols[0]] : 1 };
    }
    if (text.includes('(delete:')) {
      const detail = v.detail as Record<string, unknown>;
      rows = rows.filter((r) => !matchesPk(r, detail));
      return { [table.name]: 1 };
    }
    throw new Error(`fake db: unexpected query: ${text}`);
  });

  return { query, getRows: () => rows };
}

function harness(fetcher: GraphQLFetcher) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const wrapper = ({ children }: { children: ReactNode }) =>
    createElement(
      QueryClientProvider,
      { client: queryClient },
      createElement(FetcherProvider, { value: fetcher }, children),
    );
  return { wrapper };
}

// ---- single-PK table ----

const usersTable = tbl('users', ['id'], [
  col('id', 'Int!', { isPrimaryKey: true, isIdentity: true, isNullable: false }),
  col('name', 'String', { isNullable: false }),
  col('active', 'Boolean'),
]);

const usersDef: FormDefinition = {
  table: 'dbo.users',
  title: 'User',
  columns: 1,
  fields: [
    { column: 'id', label: 'Id', control: 'number', readOnly: true, required: false, include: true },
    { column: 'name', label: 'Name', control: 'text', readOnly: false, required: true, include: true },
    { column: 'active', label: 'Active', control: 'checkbox', readOnly: false, required: false, include: true },
  ],
};

describe('FormRunnerView — single PK', () => {
  beforeEach(() => vi.clearAllMocks());

  it('navigates records and edits + saves a value that persists on re-query', async () => {
    const db = makeDb(usersTable, [
      { id: 1, name: 'Ada', active: true },
      { id: 2, name: 'Grace', active: false },
    ]);
    const { wrapper } = harness({ query: db.query as GraphQLFetcher['query'] });
    render(createElement(FormRunnerView, { table: usersTable, definition: usersDef }), { wrapper });

    // First record loads.
    await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue('Ada'));
    expect(screen.getByLabelText('Record position')).toHaveTextContent('1 of 2');

    // Navigate to the second record.
    fireEvent.click(screen.getByLabelText('Next record'));
    await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue('Grace'));
    expect(screen.getByLabelText('Record position')).toHaveTextContent('2 of 2');

    // Edit and save.
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Grace Hopper' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    // Persisted in the backing store, and re-read into the displayed input.
    await waitFor(() => expect(db.getRows()[1].name).toBe('Grace Hopper'));
    await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue('Grace Hopper'));
  });

  it('creates a new record and lands on the created row using the PK from the mutation result', async () => {
    const db = makeDb(usersTable, [{ id: 1, name: 'Ada', active: true }]);
    const { wrapper } = harness({ query: db.query as GraphQLFetcher['query'] });
    render(createElement(FormRunnerView, { table: usersTable, definition: usersDef }), { wrapper });

    await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue('Ada'));

    fireEvent.click(screen.getByLabelText('New record'));
    await waitFor(() => expect(screen.getByLabelText('Record position')).toHaveTextContent('New record'));
    // The identity PK is not editable when creating.
    expect(screen.getByLabelText('Id')).toBeDisabled();

    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Katherine' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    // Inserted (identity assigned) and landed on the created row (key 2).
    await waitFor(() => expect(db.getRows().some((r) => r.name === 'Katherine')).toBe(true));
    await waitFor(() => expect(screen.getByLabelText('Record position')).toHaveTextContent('key 2'));
    await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue('Katherine'));
  });

  it('blocks save and shows a per-field error when a required field is empty', async () => {
    const db = makeDb(usersTable, [{ id: 1, name: 'Ada', active: true }]);
    const { wrapper } = harness({ query: db.query as GraphQLFetcher['query'] });
    render(createElement(FormRunnerView, { table: usersTable, definition: usersDef }), { wrapper });

    await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue('Ada'));
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(screen.getByText(/name is required/i)).toBeInTheDocument());
    // No update reached the store.
    expect(db.getRows()[0].name).toBe('Ada');
  });

  it('respects visible=false from an app-metadata widget hint', async () => {
    const db = makeDb(usersTable, [{ id: 1, name: 'Ada', active: true }]);
    const { wrapper } = harness({ query: db.query as GraphQLFetcher['query'] });
    render(
      createElement(FormRunnerView, {
        table: usersTable,
        definition: usersDef,
        fieldMetadata: { active: { visible: false } },
      }),
      { wrapper },
    );
    await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue('Ada'));
    expect(screen.queryByLabelText('Active')).not.toBeInTheDocument();
  });
});

// ---- composite-PK table (2-column PK) ----

const orderItemsTable = tbl('order_items', ['order_id', 'line_no'], [
  col('order_id', 'Int!', { isPrimaryKey: true, isNullable: false }),
  col('line_no', 'Int!', { isPrimaryKey: true, isNullable: false }),
  col('qty', 'Int'),
]);

const orderItemsDef: FormDefinition = {
  table: 'sales.order_items',
  title: 'Order Item',
  columns: 2,
  fields: [
    { column: 'order_id', label: 'Order', control: 'number', readOnly: false, required: true, include: true },
    { column: 'line_no', label: 'Line', control: 'number', readOnly: false, required: true, include: true },
    { column: 'qty', label: 'Qty', control: 'number', readOnly: false, required: false, include: true },
  ],
};

describe('FormRunnerView — composite (2-column) PK', () => {
  beforeEach(() => vi.clearAllMocks());

  it('edits and saves against a composite key, then re-reads the row', async () => {
    const db = makeDb(orderItemsTable, [
      { order_id: 10, line_no: 1, qty: 5 },
      { order_id: 10, line_no: 2, qty: 8 },
    ]);
    const { wrapper } = harness({ query: db.query as GraphQLFetcher['query'] });
    render(createElement(FormRunnerView, { table: orderItemsTable, definition: orderItemsDef }), { wrapper });

    await waitFor(() => expect(screen.getByLabelText('Qty')).toHaveValue(5));
    // PK columns are locked while browsing.
    expect(screen.getByLabelText('Order')).toBeDisabled();
    expect(screen.getByLabelText('Line')).toBeDisabled();

    fireEvent.click(screen.getByLabelText('Next record'));
    await waitFor(() => expect(screen.getByLabelText('Qty')).toHaveValue(8));

    fireEvent.change(screen.getByLabelText('Qty'), { target: { value: '12' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    // Exactly the (10,2) row changed — the composite WHERE targeted one row.
    await waitFor(() => {
      const r = db.getRows().find((x) => x.order_id === 10 && x.line_no === 2);
      expect(r?.qty).toBe(12);
    });
    expect(db.getRows().find((x) => x.order_id === 10 && x.line_no === 1)?.qty).toBe(5);
  });

  it('creates a composite-key row (client-supplied key) and lands on it', async () => {
    const db = makeDb(orderItemsTable, [{ order_id: 10, line_no: 1, qty: 5 }]);
    const { wrapper } = harness({ query: db.query as GraphQLFetcher['query'] });
    render(createElement(FormRunnerView, { table: orderItemsTable, definition: orderItemsDef }), { wrapper });

    await waitFor(() => expect(screen.getByLabelText('Qty')).toHaveValue(5));

    fireEvent.click(screen.getByLabelText('New record'));
    await waitFor(() => expect(screen.getByLabelText('Record position')).toHaveTextContent('New record'));
    // A non-identity composite key is editable when creating.
    expect(screen.getByLabelText('Order')).not.toBeDisabled();
    expect(screen.getByLabelText('Line')).not.toBeDisabled();

    fireEvent.change(screen.getByLabelText('Order'), { target: { value: '10' } });
    fireEvent.change(screen.getByLabelText('Line'), { target: { value: '3' } });
    fireEvent.change(screen.getByLabelText('Qty'), { target: { value: '9' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() =>
      expect(db.getRows().some((r) => r.order_id === 10 && r.line_no === 3 && r.qty === 9)).toBe(true),
    );
    // Landed on the created composite row via its route key.
    await waitFor(() => expect(screen.getByLabelText('Record position')).toHaveTextContent('key 10::3'));
    await waitFor(() => expect(screen.getByLabelText('Qty')).toHaveValue(9));
  });

  it('jumps to a composite key and deletes the exact row', async () => {
    const db = makeDb(orderItemsTable, [
      { order_id: 10, line_no: 1, qty: 5 },
      { order_id: 10, line_no: 2, qty: 8 },
    ]);
    const { wrapper } = harness({ query: db.query as GraphQLFetcher['query'] });
    render(createElement(FormRunnerView, { table: orderItemsTable, definition: orderItemsDef }), { wrapper });

    await waitFor(() => expect(screen.getByLabelText('Qty')).toHaveValue(5));

    // Jump straight to the (10,2) row by its composite route key.
    fireEvent.change(screen.getByLabelText('Jump to key'), { target: { value: '10::2' } });
    fireEvent.click(screen.getByLabelText('Go to key'));
    await waitFor(() => expect(screen.getByLabelText('Qty')).toHaveValue(8));

    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(db.getRows()).toHaveLength(1));
    expect(db.getRows()[0]).toMatchObject({ order_id: 10, line_no: 1 });
  });
});

describe('resolveDefinitionTable', () => {
  const schema: Schema = {
    loading: false,
    error: null,
    data: [usersTable],
    findTable: (n) => (n === 'users' ? usersTable : undefined),
  };

  it('resolves a qualified definition table against the GraphQL name', () => {
    expect(resolveDefinitionTable(schema, 'dbo.users')).toBe(usersTable);
    expect(resolveDefinitionTable(schema, 'users')).toBe(usersTable);
    expect(resolveDefinitionTable(schema, 'dbo.missing')).toBeUndefined();
  });
});
