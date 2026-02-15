import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  render,
  screen,
  within,
  fireEvent,
  waitFor,
} from '@testing-library/react';
import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BifrostProvider } from './bifrost-provider';
import { BifrostTable } from './bifrost-table';
import type { BifrostTableProps } from './bifrost-table';
import type { ColumnConfig } from '../hooks/use-bifrost-table';
import { getTheme } from './table-theme';
import type { ThemeName } from './table-theme';

function createFetchMock(response: unknown, ok = true, status = 200) {
  return vi.fn().mockResolvedValue({
    ok,
    status,
    statusText: ok ? 'OK' : 'Internal Server Error',
    json: () => Promise.resolve(response),
  });
}

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
    },
  });

  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <BifrostProvider config={{ endpoint: 'http://localhost:5000/graphql' }}>
          {children}
        </BifrostProvider>
      </QueryClientProvider>
    );
  };
}

const defaultColumns: ColumnConfig[] = [
  { field: 'id', header: 'ID', width: 80, sortable: true },
  { field: 'name', header: 'Name', sortable: true, filterable: true },
  { field: 'email', header: 'Email', sortable: true },
];

const mockUsers = [
  { id: 1, name: 'Alice', email: 'alice@test.com' },
  { id: 2, name: 'Bob', email: 'bob@test.com' },
  { id: 3, name: 'Charlie', email: 'charlie@test.com' },
];

function renderTable(props: Partial<BifrostTableProps> = {}) {
  const Wrapper = createWrapper();
  return render(
    <Wrapper>
      <BifrostTable
        query="users"
        columns={defaultColumns}
        urlSync={false}
        {...props}
      />
    </Wrapper>,
  );
}

describe('BifrostTable', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  describe('rendering', () => {
    it('renders the table container', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable();

      expect(screen.getByTestId('bifrost-table')).toBeInTheDocument();
    });

    it('renders column headers', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByText('ID')).toBeInTheDocument();
      });
      expect(screen.getByText('Name')).toBeInTheDocument();
      expect(screen.getByText('Email')).toBeInTheDocument();
    });

    it('renders data rows', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });
      expect(screen.getByText('Bob')).toBeInTheDocument();
      expect(screen.getByText('Charlie')).toBeInTheDocument();
    });

    it('renders cell values from row data', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByText('alice@test.com')).toBeInTheDocument();
      });
    });

    it('renders empty message when no data', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByText('No data available')).toBeInTheDocument();
      });
    });

    it('renders custom empty message', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      renderTable({ emptyMessage: 'Nothing here' });

      await waitFor(() => {
        expect(screen.getByText('Nothing here')).toBeInTheDocument();
      });
    });

    it('renders loading overlay during fetch', () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      renderTable();

      expect(screen.getByTestId('loading-overlay')).toBeInTheDocument();
      expect(screen.getByText('Loading...')).toBeInTheDocument();
    });

    it('renders custom loading via renderLoading', () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      renderTable({ renderLoading: () => <span>Please wait...</span> });

      expect(screen.getByText('Please wait...')).toBeInTheDocument();
    });

    it('renders error state', async () => {
      globalThis.fetch = createFetchMock({}, false, 500);

      renderTable({ retry: false });

      await waitFor(() => {
        expect(screen.getByRole('alert')).toBeInTheDocument();
      });
    });

    it('renders custom error via renderError', async () => {
      globalThis.fetch = createFetchMock({}, false, 500);

      renderTable({
        retry: false,
        renderError: (err) => (
          <div data-testid="custom-error">{err.message}</div>
        ),
      });

      await waitFor(() => {
        expect(screen.getByTestId('custom-error')).toBeInTheDocument();
      });
    });

    it('renders custom empty via renderEmpty', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      renderTable({
        renderEmpty: () => (
          <span data-testid="custom-empty">No users found</span>
        ),
      });

      await waitFor(() => {
        expect(screen.getByTestId('custom-empty')).toBeInTheDocument();
      });
    });
  });

  describe('theme switching', () => {
    const themeNames: ThemeName[] = ['modern', 'classic', 'minimal', 'dense'];

    for (const themeName of themeNames) {
      it(`renders with ${themeName} theme`, async () => {
        globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

        renderTable({ theme: themeName });

        await waitFor(() => {
          expect(screen.getByText('Alice')).toBeInTheDocument();
        });

        const table = screen.getByRole('table');
        const theme = getTheme(themeName);
        expect(table.style.fontFamily).toBe(
          (theme.table.fontFamily as string) ?? '',
        );
      });
    }

    it('applies theme overrides', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({
        theme: 'modern',
        themeOverrides: {
          table: { fontSize: '20px' },
        },
      });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const table = screen.getByRole('table');
      expect(table.style.fontSize).toBe('20px');
    });

    it('defaults to modern theme', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const container = screen.getByTestId('bifrost-table');
      const modernTheme = getTheme('modern');
      expect(container.style.borderRadius).toBe(
        (modernTheme.container.borderRadius as string) ?? '',
      );
    });
  });

  describe('row actions', () => {
    it('renders action buttons for each row', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const handleEdit = vi.fn();
      const handleDelete = vi.fn();

      renderTable({
        rowActions: [
          { label: 'Edit', onClick: handleEdit },
          { label: 'Delete', onClick: handleDelete },
        ],
      });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const editButtons = screen.getAllByTestId('action-edit');
      const deleteButtons = screen.getAllByTestId('action-delete');

      expect(editButtons).toHaveLength(3);
      expect(deleteButtons).toHaveLength(3);
    });

    it('calls action onClick with the correct row', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const handleEdit = vi.fn();

      renderTable({
        rowActions: [{ label: 'Edit', onClick: handleEdit }],
      });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const editButtons = screen.getAllByTestId('action-edit');
      fireEvent.click(editButtons[1]);

      expect(handleEdit).toHaveBeenCalledWith(mockUsers[1]);
    });

    it('renders Actions column header', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({
        rowActions: [{ label: 'Edit', onClick: vi.fn() }],
      });

      await waitFor(() => {
        expect(screen.getByText('Actions')).toBeInTheDocument();
      });
    });

    it('action click does not trigger onRowClick', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const handleRowClick = vi.fn();
      const handleEdit = vi.fn();

      renderTable({
        onRowClick: handleRowClick,
        rowActions: [{ label: 'Edit', onClick: handleEdit }],
      });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const editButtons = screen.getAllByTestId('action-edit');
      fireEvent.click(editButtons[0]);

      expect(handleEdit).toHaveBeenCalled();
      expect(handleRowClick).not.toHaveBeenCalled();
    });
  });

  describe('column rendering', () => {
    it('renders custom cell content via renderCell', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({
        renderCell: (value, _row, col) => {
          if (col.field === 'name') {
            return <strong data-testid="custom-name">{String(value)}</strong>;
          }
          return String(value ?? '');
        },
      });

      await waitFor(() => {
        expect(screen.getAllByTestId('custom-name')).toHaveLength(3);
      });

      const customNames = screen.getAllByTestId('custom-name');
      expect(customNames[0]).toHaveTextContent('Alice');
    });

    it('formats null values as empty string', async () => {
      const usersWithNull = [{ id: 1, name: null, email: 'test@test.com' }];
      globalThis.fetch = createFetchMock({ data: { users: usersWithNull } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByText('test@test.com')).toBeInTheDocument();
      });

      const row = screen.getByTestId('table-row-1');
      const cells = within(row).getAllByRole('cell');
      expect(cells[1]).toHaveTextContent('');
    });

    it('formats boolean values', async () => {
      const usersWithBool = [{ id: 1, name: 'Alice', email: true }];
      globalThis.fetch = createFetchMock({ data: { users: usersWithBool } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByText('Yes')).toBeInTheDocument();
      });
    });

    it('applies column width', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByText('ID')).toBeInTheDocument();
      });

      const idHeader = screen.getByText('ID').closest('th');
      expect(idHeader?.style.width).toBe('80px');
    });
  });

  describe('pagination controls', () => {
    it('shows pagination when data is present', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByTestId('pagination')).toBeInTheDocument();
      });
    });

    it('displays current page number', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByTestId('pagination-info')).toHaveTextContent(
          'Page 1',
        );
      });
    });

    it('disables Previous button on first page', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByTestId('pagination-prev')).toBeDisabled();
      });
    });

    it('disables Next when data length < pageSize', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({ pagination: { pageSize: 25 } });

      await waitFor(() => {
        expect(screen.getByTestId('pagination-next')).toBeDisabled();
      });
    });

    it('enables Next when data fills the page', async () => {
      const fullPage = Array.from({ length: 25 }, (_, i) => ({
        id: i + 1,
        name: `User ${i + 1}`,
        email: `user${i + 1}@test.com`,
      }));
      globalThis.fetch = createFetchMock({ data: { users: fullPage } });

      renderTable({ pagination: { pageSize: 25 } });

      await waitFor(() => {
        expect(screen.getByTestId('pagination-next')).not.toBeDisabled();
      });
    });
  });

  describe('sorting interaction', () => {
    it('shows sort indicator on sortable columns', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      expect(screen.getAllByLabelText('unsorted').length).toBeGreaterThan(0);
    });

    it('toggles sort on header click', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const nameHeader = screen.getByText('Name').closest('th');
      fireEvent.click(nameHeader!);

      expect(screen.getByLabelText('sorted ascending')).toBeInTheDocument();
    });

    it('sets aria-sort on sortable columns', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const nameHeader = screen.getByText('Name').closest('th');
      expect(nameHeader).toHaveAttribute('aria-sort', 'none');

      fireEvent.click(nameHeader!);
      expect(nameHeader).toHaveAttribute('aria-sort', 'ascending');

      fireEvent.click(nameHeader!);
      expect(nameHeader).toHaveAttribute('aria-sort', 'descending');
    });
  });

  describe('row click', () => {
    it('calls onRowClick with the row data', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const handleRowClick = vi.fn();

      renderTable({ onRowClick: handleRowClick });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const row = screen.getByTestId('table-row-1');
      fireEvent.click(row);

      expect(handleRowClick).toHaveBeenCalledWith(mockUsers[0]);
    });

    it('sets cursor pointer when onRowClick is provided', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({ onRowClick: vi.fn() });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const row = screen.getByTestId('table-row-1');
      expect(row.style.cursor).toBe('pointer');
    });
  });

  describe('striped and hoverable', () => {
    it('applies striped style to odd rows', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({ striped: true });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const firstRow = screen.getByTestId('table-row-1');
      const secondRow = screen.getByTestId('table-row-2');
      // Even rows (index 0) should not have striped bg, odd rows (index 1) should
      const firstBg = firstRow.style.backgroundColor;
      const secondBg = secondRow.style.backgroundColor;
      expect(secondBg).not.toBe('');
      expect(firstBg).not.toBe(secondBg);
    });
  });

  describe('editable', () => {
    it('shows edit input on double-click when editable', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({ editable: true });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const aliceCell = screen.getByText('Alice');
      fireEvent.doubleClick(aliceCell);

      expect(screen.getByTestId('edit-input')).toBeInTheDocument();
    });

    it('dismisses edit on Escape', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({ editable: true });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      fireEvent.doubleClick(screen.getByText('Alice'));
      const input = screen.getByTestId('edit-input');
      fireEvent.keyDown(input, { key: 'Escape' });

      expect(screen.queryByTestId('edit-input')).not.toBeInTheDocument();
    });

    it('does not show edit input when not editable', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({ editable: false });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      fireEvent.doubleClick(screen.getByText('Alice'));
      expect(screen.queryByTestId('edit-input')).not.toBeInTheDocument();
    });
  });

  describe('exportable', () => {
    it('shows export button when exportable and data present', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({ exportable: true });

      await waitFor(() => {
        expect(screen.getByTestId('export-button')).toBeInTheDocument();
      });
    });

    it('does not show export button when not exportable', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({ exportable: false });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      expect(screen.queryByTestId('export-button')).not.toBeInTheDocument();
    });

    it('does not show export button when no data', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      renderTable({ exportable: true });

      await waitFor(() => {
        expect(screen.getByText('No data available')).toBeInTheDocument();
      });

      expect(screen.queryByTestId('export-button')).not.toBeInTheDocument();
    });
  });

  describe('responsive behavior', () => {
    it('table container has overflow-x auto', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const container = screen.getByTestId('bifrost-table');
      expect(container.style.overflowX).toBe('auto');
    });

    it('table has 100% width', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable();

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const table = screen.getByRole('table');
      expect(table.style.width).toBe('100%');
    });
  });
});
