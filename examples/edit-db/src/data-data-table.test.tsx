import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import '@testing-library/jest-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Column, Table } from './types/schema';

// The grid itself is not under test — stub it so the panel logic renders alone.
vi.mock('./components/data-table', () => ({ DataTable: () => <div data-testid="grid" /> }));
vi.mock('./data-edit', () => ({ DataEditDialog: () => null }));

const fetcherQuery = vi.fn();
vi.mock('./common/fetcher', () => ({ useFetcher: () => ({ query: fetcherQuery }) }));

// Controlled useDataTable: rows come from the mutable module-level `mockRows`
// (so a test can simulate a background refetch reordering/removing rows), and
// the onExpandContent callback is captured so tests can open the content panel
// exactly the way a grid cell does — by grid index.
let capturedExpand: ((rowIndex: number, columnName: string) => void) | undefined;
let mockRows: Record<string, unknown>[] = [];
vi.mock('./hooks/useDataTable', () => ({
    useDataTable: (
        _table: unknown,
        _id: unknown,
        _tableFilter: unknown,
        _filterColumn: unknown,
        onExpandContent?: (rowIndex: number, columnName: string) => void,
    ) => {
        capturedExpand = onExpandContent;
        return {
            columns: [],
            sorting: [],
            columnFilters: [],
            primaryKeys: [],
            pageIndex: 0,
            pageSize: 50,
            pageCount: 1,
            rows: mockRows,
            loading: false,
            error: null,
            onSortingChange: vi.fn(),
            onColumnFiltersChange: vi.fn(),
            onPageIndexChange: vi.fn(),
            onPageSizeChange: vi.fn(),
        };
    },
}));

import { DataDataTable } from './data-data-table';

function makeColumn(overrides: Partial<Column> = {}): Column {
    return {
        dbName: 'col',
        graphQlName: 'col',
        name: 'col',
        label: 'Col',
        paramType: 'String',
        dbType: 'nvarchar',
        isPrimaryKey: false,
        isIdentity: false,
        isNullable: true,
        isReadOnly: false,
        metadata: {},
        ...overrides,
    };
}

const table: Table = {
    dbName: 'dbo.docs',
    graphQlName: 'docs',
    name: 'docs',
    label: 'Docs',
    labelColumn: 'name',
    primaryKeys: ['id'],
    isEditable: true,
    metadata: {},
    columns: [
        makeColumn({ name: 'id', label: 'Id', paramType: 'Int!', isPrimaryKey: true, isIdentity: true, isNullable: false }),
        makeColumn({ name: 'name', label: 'Name', paramType: 'String!', isNullable: false }),
        makeColumn({ name: 'note', label: 'Note' }), // nullable, editable — must NOT round-trip
        makeColumn({ name: 'doc', label: 'Doc', dbType: 'text' }), // the panel column
    ],
    multiJoins: [],
    singleJoins: [],
};

const rowA = { id: 1, name: 'Alpha', note: 'note-a', doc: 'hello A' };
const rowB = { id: 2, name: 'Beta', note: 'note-b', doc: 'hello B' };

function ui() {
    return <DataDataTable table={table} />;
}

function renderGrid() {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
    return render(<QueryClientProvider client={client}>{ui()}</QueryClientProvider>);
}

async function openPanelAtIndex(index: number, column = 'doc') {
    act(() => capturedExpand!(index, column));
}

async function editAndSave(newValue: string) {
    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    fireEvent.change(screen.getByRole('textbox'), { target: { value: newValue } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
}

function mockSaveRoundTrip(freshRow: Record<string, unknown>) {
    fetcherQuery.mockImplementation((query: string) => {
        if (query.includes('GetSingleRow_')) {
            return Promise.resolve({ value: { data: [freshRow] } });
        }
        return Promise.resolve({ docs: 1 });
    });
}

describe('DataDataTable content panel row identity (PK snapshot)', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        capturedExpand = undefined;
        mockRows = [rowA, rowB];
    });

    it('saves to the PK-snapshotted row even after a refetch reorders the grid', async () => {
        const { rerender } = renderGrid();
        await openPanelAtIndex(0); // row A (id 1) at index 0
        expect(await screen.findByText('hello A')).toBeInTheDocument();

        // Background refetch reorders the page: index 0 is now row B.
        mockRows = [rowB, rowA];
        rerender(<QueryClientProvider client={new QueryClient()}>{ui()}</QueryClientProvider>);

        // The panel follows the PK, not the index — still row A.
        expect(screen.getByText('hello A')).toBeInTheDocument();
        expect(screen.queryByText('hello B')).not.toBeInTheDocument();

        mockSaveRoundTrip({ name: 'Alpha', doc: 'hello A' });
        await editAndSave('edited A');

        await waitFor(() => expect(fetcherQuery).toHaveBeenCalledTimes(2));

        // Fresh read is keyed by row A's PK (id 1), not by whatever row moved
        // into the opened grid index (row B, id 2).
        const [freshQuery, freshVars] = fetcherQuery.mock.calls[0] as [string, Record<string, unknown>];
        expect(freshQuery).toContain('GetSingleRow_docs');
        expect(freshVars).toEqual({ pk_id: 1 });

        // The update also targets row A.
        const [updateQuery, updateVars] = fetcherQuery.mock.calls[1] as [string, { detail: Record<string, unknown> }];
        expect(updateQuery).toContain('update: $detail');
        expect(updateVars.detail.id).toBe(1);
        expect(updateVars.detail.doc).toBe('edited A');
    });

    it('fetches and echoes only non-nullable editable columns plus the edited column', async () => {
        renderGrid();
        await openPanelAtIndex(0);
        expect(await screen.findByText('hello A')).toBeInTheDocument();

        mockSaveRoundTrip({ name: 'Alpha', doc: 'hello A' });
        await editAndSave('edited A');
        await waitFor(() => expect(fetcherQuery).toHaveBeenCalledTimes(2));

        // Fresh read: id is identity (not editable), note is nullable and
        // untouched — only the required column plus the edited one round-trip.
        const [freshQuery] = fetcherQuery.mock.calls[0] as [string];
        expect(freshQuery).toContain('data { name doc }');
        expect(freshQuery).not.toContain('note');

        const [, updateVars] = fetcherQuery.mock.calls[1] as [string, { detail: Record<string, unknown> }];
        expect(updateVars.detail).not.toHaveProperty('note');
        expect(updateVars.detail.name).toBe('Alpha');
        expect(updateVars.detail.doc).toBe('edited A');
    });

    it('closes the panel when the snapshotted row is no longer on the page', async () => {
        const { rerender } = renderGrid();
        await openPanelAtIndex(0); // row A
        expect(await screen.findByText('hello A')).toBeInTheDocument();

        // Refetch drops row A entirely (deleted / filtered away).
        mockRows = [rowB];
        rerender(<QueryClientProvider client={new QueryClient()}>{ui()}</QueryClientProvider>);

        await waitFor(() => expect(screen.queryByText('hello A')).not.toBeInTheDocument());
        // And it must not silently re-point at row B.
        expect(screen.queryByText('hello B')).not.toBeInTheDocument();
    });

    it('keeps an open edit when a refetch merely moves the row (reset key is PK-based)', async () => {
        const { rerender } = renderGrid();
        await openPanelAtIndex(0); // row A
        expect(await screen.findByText('hello A')).toBeInTheDocument();

        fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
        fireEvent.change(screen.getByRole('textbox'), { target: { value: 'in-progress edit' } });

        // Row A moves from index 0 to index 1 — same row, new position.
        mockRows = [rowB, rowA];
        rerender(<QueryClientProvider client={new QueryClient()}>{ui()}</QueryClientProvider>);

        // The editor (and the unsaved text) survives the move.
        expect(screen.getByRole('textbox')).toHaveValue('in-progress edit');
    });
});
