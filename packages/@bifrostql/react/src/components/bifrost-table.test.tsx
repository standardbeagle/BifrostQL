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
import {
  BifrostTable,
  TableHeader,
  TableBody,
  TableRow,
  TableCell,
  TableFooter,
  TableToolbar,
  ExpandedRow,
  Pagination,
  ColumnSelector,
  FilterBuilder,
  ExportMenu,
} from './bifrost-table';
import type { BifrostTableProps } from './bifrost-table';
import type { ColumnConfig } from '../hooks/use-bifrost-table';
import {
  getTheme,
  createBifrostTheme,
  getThemeTokens,
  themeToCssVariables,
} from './table-theme';
import type { ThemeName, AnyThemeName, DarkThemeName } from './table-theme';

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

  describe('dark mode themes', () => {
    const darkThemeNames: DarkThemeName[] = [
      'modern-dark',
      'classic-dark',
      'minimal-dark',
      'dense-dark',
    ];

    for (const themeName of darkThemeNames) {
      it(`renders with ${themeName} theme`, async () => {
        globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

        renderTable({ theme: themeName });

        await waitFor(() => {
          expect(screen.getByText('Alice')).toBeInTheDocument();
        });

        const table = screen.getByRole('table');
        expect(table).toBeInTheDocument();
      });
    }

    it('modern-dark has dark background on container', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({ theme: 'modern-dark' });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const container = screen.getByTestId('bifrost-table');
      expect(container.style.backgroundColor).not.toBe('');
    });

    it('classic-dark has dark header row', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({ theme: 'classic-dark' });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const headerRow = screen.getAllByRole('row')[0];
      expect(headerRow.style.backgroundColor).not.toBe('');
    });

    it('dense-dark has smaller font size', () => {
      const theme = getTheme('dense-dark');
      expect(theme.table.fontSize).toBe('12px');
    });

    it('minimal-dark has uppercase headers', () => {
      const theme = getTheme('minimal-dark');
      expect(theme.headerCell.textTransform).toBe('uppercase');
    });
  });

  describe('customTheme prop', () => {
    it('accepts a fully custom theme object', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const custom = createBifrostTheme({
        fontSize: '18px',
        borderRadius: '12px',
        headerBg: '#ff0000',
        bodyColor: '#00ff00',
      });

      renderTable({ customTheme: custom });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const table = screen.getByRole('table');
      expect(table.style.fontSize).toBe('18px');
    });

    it('customTheme takes priority over theme name', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const custom = createBifrostTheme({ fontSize: '22px' });

      renderTable({ theme: 'dense', customTheme: custom });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const table = screen.getByRole('table');
      expect(table.style.fontSize).toBe('22px');
    });

    it('themeOverrides still apply on top of customTheme', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const custom = createBifrostTheme({ fontSize: '16px' });

      renderTable({
        customTheme: custom,
        themeOverrides: { table: { fontSize: '24px' } },
      });

      await waitFor(() => {
        expect(screen.getByText('Alice')).toBeInTheDocument();
      });

      const table = screen.getByRole('table');
      expect(table.style.fontSize).toBe('24px');
    });
  });

  describe('renderToolbar prop', () => {
    it('renders custom toolbar when renderToolbar is provided', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({
        exportable: true,
        renderToolbar: (actions) => (
          <div data-testid="custom-toolbar">
            <button type="button" onClick={actions.export} data-testid="custom-export">
              Custom Export
            </button>
          </div>
        ),
      });

      await waitFor(() => {
        expect(screen.getByTestId('custom-toolbar')).toBeInTheDocument();
      });
      expect(screen.getByTestId('custom-export')).toBeInTheDocument();
    });

    it('renderToolbar replaces the default toolbar', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      renderTable({
        exportable: true,
        renderToolbar: () => <div data-testid="custom-toolbar">Custom</div>,
      });

      await waitFor(() => {
        expect(screen.getByTestId('custom-toolbar')).toBeInTheDocument();
      });
      expect(screen.queryByTestId('export-button')).not.toBeInTheDocument();
    });
  });
});

describe('createBifrostTheme', () => {
  it('creates a valid theme from minimal tokens', () => {
    const theme = createBifrostTheme({});
    expect(theme.container).toBeDefined();
    expect(theme.table).toBeDefined();
    expect(theme.headerRow).toBeDefined();
    expect(theme.bodyRow).toBeDefined();
    expect(theme.pagination).toBeDefined();
    expect(theme.toolbar).toBeDefined();
  });

  it('applies custom font family', () => {
    const theme = createBifrostTheme({
      fontFamily: 'monospace',
    });
    expect(theme.table.fontFamily).toBe('monospace');
  });

  it('applies custom font size', () => {
    const theme = createBifrostTheme({
      fontSize: '18px',
    });
    expect(theme.table.fontSize).toBe('18px');
  });

  it('applies custom border radius', () => {
    const theme = createBifrostTheme({
      borderRadius: '12px',
    });
    expect(theme.paginationButton.borderRadius).toBe('12px');
    expect(theme.actionButton.borderRadius).toBe('12px');
  });

  it('applies header colors', () => {
    const theme = createBifrostTheme({
      headerBg: '#333',
      headerColor: '#fff',
    });
    expect(theme.headerRow.backgroundColor).toBe('#333');
    expect(theme.headerCell.color).toBe('#fff');
  });

  it('applies body colors', () => {
    const theme = createBifrostTheme({
      bodyColor: '#123456',
      bodyBorderColor: '#abcdef',
    });
    expect(theme.bodyCell.color).toBe('#123456');
    expect(theme.bodyRow.borderBottom).toContain('#abcdef');
  });

  it('applies hover and striped backgrounds', () => {
    const theme = createBifrostTheme({
      hoverBg: '#eee',
      stripedBg: '#ddd',
    });
    expect(theme.bodyRowHover.backgroundColor).toBe('#eee');
    expect(theme.bodyRowStriped.backgroundColor).toBe('#ddd');
  });

  it('applies error color', () => {
    const theme = createBifrostTheme({
      errorColor: '#ff0000',
    });
    expect(theme.errorContainer.color).toBe('#ff0000');
  });

  it('applies muted color to sort indicator and empty container', () => {
    const theme = createBifrostTheme({
      mutedColor: '#888',
    });
    expect(theme.sortIndicator.color).toBe('#888');
    expect(theme.emptyContainer.color).toBe('#888');
  });

  it('applies button styling', () => {
    const theme = createBifrostTheme({
      buttonBg: '#000',
      buttonColor: '#fff',
      buttonBorder: '#555',
    });
    expect(theme.paginationButton.background).toBe('#000');
    expect(theme.paginationButton.color).toBe('#fff');
    expect(theme.paginationButton.border).toContain('#555');
  });

  it('applies disabled button styling', () => {
    const theme = createBifrostTheme({
      disabledBg: '#ccc',
      disabledColor: '#999',
      disabledBorder: '#aaa',
    });
    expect(theme.paginationButtonDisabled.background).toBe('#ccc');
    expect(theme.paginationButtonDisabled.color).toBe('#999');
    expect(theme.paginationButtonDisabled.border).toContain('#aaa');
  });

  it('applies padding', () => {
    const theme = createBifrostTheme({
      padding: '8px 12px',
    });
    expect(theme.headerCell.padding).toBe('8px 12px');
    expect(theme.bodyCell.padding).toBe('8px 12px');
  });

  it('applies overlay background', () => {
    const theme = createBifrostTheme({
      overlayBg: 'rgba(0, 0, 0, 0.5)',
    });
    expect(theme.loadingOverlay.backgroundColor).toBe('rgba(0, 0, 0, 0.5)');
  });

  it('applies shadow', () => {
    const theme = createBifrostTheme({
      shadow: '0 4px 8px rgba(0,0,0,0.2)',
    });
    expect(theme.container.boxShadow).toBe('0 4px 8px rgba(0,0,0,0.2)');
  });

  it('omits shadow when set to none', () => {
    const theme = createBifrostTheme({ shadow: 'none' });
    expect(theme.container.boxShadow).toBeUndefined();
  });
});

describe('getTheme', () => {
  it('returns all light themes', () => {
    const names: ThemeName[] = ['modern', 'classic', 'minimal', 'dense'];
    for (const name of names) {
      const theme = getTheme(name);
      expect(theme).toBeDefined();
      expect(theme.table).toBeDefined();
    }
  });

  it('returns all dark themes', () => {
    const names: DarkThemeName[] = [
      'modern-dark',
      'classic-dark',
      'minimal-dark',
      'dense-dark',
    ];
    for (const name of names) {
      const theme = getTheme(name);
      expect(theme).toBeDefined();
      expect(theme.table).toBeDefined();
    }
  });

  it('dark themes have darker body colors than light themes', () => {
    const lightTheme = getTheme('modern');
    const darkTheme = getTheme('modern-dark');
    expect(lightTheme.bodyCell.color).not.toBe(darkTheme.bodyCell.color);
  });
});

describe('getThemeTokens', () => {
  it('returns light tokens for light theme names', () => {
    const tokens = getThemeTokens('modern');
    expect(tokens.bodyColor).toBe('#1f2937');
    expect(tokens.surfaceBg).toBe('#fff');
  });

  it('returns dark tokens for dark theme names', () => {
    const tokens = getThemeTokens('modern-dark');
    expect(tokens.bodyColor).toBe('#e5e7eb');
    expect(tokens.surfaceBg).toBe('#1f2937');
  });

  it('returns a copy that can be modified safely', () => {
    const tokens1 = getThemeTokens('modern');
    const tokens2 = getThemeTokens('modern');
    tokens1.fontSize = '999px';
    expect(tokens2.fontSize).toBe('14px');
  });
});

describe('themeToCssVariables', () => {
  it('generates CSS variables from a theme', () => {
    const theme = createBifrostTheme({ fontSize: '16px' });
    const vars = themeToCssVariables(theme);
    expect(typeof vars).toBe('object');
    expect(Object.keys(vars).length).toBeGreaterThan(0);
  });

  it('uses the default prefix', () => {
    const theme = createBifrostTheme({});
    const vars = themeToCssVariables(theme);
    const keys = Object.keys(vars);
    expect(keys.every((k) => k.startsWith('--bfq-'))).toBe(true);
  });

  it('uses a custom prefix', () => {
    const theme = createBifrostTheme({});
    const vars = themeToCssVariables(theme, '--my-table');
    const keys = Object.keys(vars);
    expect(keys.every((k) => k.startsWith('--my-table-'))).toBe(true);
  });

  it('includes color values from the theme', () => {
    const theme = createBifrostTheme({ bodyColor: '#abcdef' });
    const vars = themeToCssVariables(theme);
    const values = Object.values(vars);
    expect(values.some((v) => v === '#abcdef')).toBe(true);
  });

  it('converts camelCase keys to kebab-case', () => {
    const theme = createBifrostTheme({});
    const vars = themeToCssVariables(theme);
    const keys = Object.keys(vars);
    expect(keys.some((k) => k.includes('header-row'))).toBe(true);
    expect(keys.some((k) => k.includes('body-cell'))).toBe(true);
  });
});

describe('composable sub-components', () => {
  describe('TableHeader', () => {
    it('renders column headers', () => {
      render(
        <table>
          <TableHeader columns={defaultColumns} />
        </table>,
      );
      expect(screen.getByText('ID')).toBeInTheDocument();
      expect(screen.getByText('Name')).toBeInTheDocument();
      expect(screen.getByText('Email')).toBeInTheDocument();
    });

    it('renders children when provided', () => {
      render(
        <table>
          <TableHeader columns={defaultColumns}>
            <tr>
              <th>Custom Header</th>
            </tr>
          </TableHeader>
        </table>,
      );
      expect(screen.getByText('Custom Header')).toBeInTheDocument();
    });

    it('renders sort indicators for sortable columns', () => {
      render(
        <table>
          <TableHeader
            columns={defaultColumns}
            sortState={[{ field: 'name', direction: 'asc' }]}
          />
        </table>,
      );
      expect(screen.getByLabelText('sorted ascending')).toBeInTheDocument();
    });

    it('calls onSort when sortable header is clicked', () => {
      const onSort = vi.fn();
      render(
        <table>
          <TableHeader columns={defaultColumns} onSort={onSort} />
        </table>,
      );
      const nameHeader = screen.getByText('Name').closest('th');
      fireEvent.click(nameHeader!);
      expect(onSort).toHaveBeenCalledWith('name');
    });

    it('does not call onSort for non-sortable columns', () => {
      const nonSortableColumns: ColumnConfig[] = [
        { field: 'id', header: 'ID' },
      ];
      const onSort = vi.fn();
      render(
        <table>
          <TableHeader columns={nonSortableColumns} onSort={onSort} />
        </table>,
      );
      const header = screen.getByText('ID').closest('th');
      fireEvent.click(header!);
      expect(onSort).not.toHaveBeenCalled();
    });
  });

  describe('TableBody', () => {
    it('renders children', () => {
      render(
        <table>
          <TableBody>
            <tr>
              <td>Body content</td>
            </tr>
          </TableBody>
        </table>,
      );
      expect(screen.getByText('Body content')).toBeInTheDocument();
    });
  });

  describe('TableRow', () => {
    it('renders children within a row', () => {
      render(
        <table>
          <tbody>
            <TableRow>
              <td>Cell content</td>
            </TableRow>
          </tbody>
        </table>,
      );
      expect(screen.getByText('Cell content')).toBeInTheDocument();
    });

    it('calls onClick when clicked', () => {
      const onClick = vi.fn();
      render(
        <table>
          <tbody>
            <TableRow onClick={onClick}>
              <td>Click me</td>
            </TableRow>
          </tbody>
        </table>,
      );
      fireEvent.click(screen.getByRole('row'));
      expect(onClick).toHaveBeenCalled();
    });

    it('applies custom style', () => {
      render(
        <table>
          <tbody>
            <TableRow style={{ backgroundColor: 'red' }} testId="styled-row">
              <td>Styled</td>
            </TableRow>
          </tbody>
        </table>,
      );
      expect(screen.getByTestId('styled-row').style.backgroundColor).toBe('red');
    });
  });

  describe('TableCell', () => {
    it('renders cell content', () => {
      render(
        <table>
          <tbody>
            <tr>
              <TableCell>Cell value</TableCell>
            </tr>
          </tbody>
        </table>,
      );
      expect(screen.getByText('Cell value')).toBeInTheDocument();
    });

    it('applies colSpan', () => {
      render(
        <table>
          <tbody>
            <tr>
              <TableCell colSpan={3}>Spanning</TableCell>
            </tr>
          </tbody>
        </table>,
      );
      const cell = screen.getByRole('cell');
      expect(cell).toHaveAttribute('colspan', '3');
    });

    it('applies custom style', () => {
      render(
        <table>
          <tbody>
            <tr>
              <TableCell style={{ color: 'blue' }}>Blue text</TableCell>
            </tr>
          </tbody>
        </table>,
      );
      expect(screen.getByRole('cell').style.color).toBe('blue');
    });
  });

  describe('TableFooter', () => {
    it('renders footer content', () => {
      render(<TableFooter>Footer content</TableFooter>);
      expect(screen.getByText('Footer content')).toBeInTheDocument();
    });

    it('has table-footer test id', () => {
      render(<TableFooter>Footer</TableFooter>);
      expect(screen.getByTestId('table-footer')).toBeInTheDocument();
    });
  });

  describe('TableToolbar', () => {
    it('renders toolbar content', () => {
      render(<TableToolbar>Toolbar content</TableToolbar>);
      expect(screen.getByText('Toolbar content')).toBeInTheDocument();
    });

    it('has table-toolbar test id', () => {
      render(<TableToolbar>Toolbar</TableToolbar>);
      expect(screen.getByTestId('table-toolbar')).toBeInTheDocument();
    });

    it('applies custom style', () => {
      render(
        <TableToolbar style={{ padding: '20px' }}>Toolbar</TableToolbar>,
      );
      expect(screen.getByTestId('table-toolbar').style.padding).toBe('20px');
    });
  });

  describe('ExpandedRow', () => {
    it('renders expanded content', () => {
      render(
        <table>
          <tbody>
            <ExpandedRow colSpan={3}>Expanded content</ExpandedRow>
          </tbody>
        </table>,
      );
      expect(screen.getByText('Expanded content')).toBeInTheDocument();
    });

    it('sets colSpan on the cell', () => {
      render(
        <table>
          <tbody>
            <ExpandedRow colSpan={5}>Content</ExpandedRow>
          </tbody>
        </table>,
      );
      const cell = screen.getByRole('cell');
      expect(cell).toHaveAttribute('colspan', '5');
    });

    it('supports test id', () => {
      render(
        <table>
          <tbody>
            <ExpandedRow colSpan={3} testId="my-expanded">
              Content
            </ExpandedRow>
          </tbody>
        </table>,
      );
      expect(screen.getByTestId('my-expanded')).toBeInTheDocument();
    });
  });

  describe('Pagination', () => {
    it('renders page info', () => {
      render(
        <Pagination
          page={0}
          pageSize={25}
          dataLength={25}
          onPrevious={vi.fn()}
          onNext={vi.fn()}
        />,
      );
      expect(screen.getByTestId('pagination-info')).toHaveTextContent('Page 1');
    });

    it('disables Previous on first page', () => {
      render(
        <Pagination
          page={0}
          pageSize={25}
          dataLength={25}
          onPrevious={vi.fn()}
          onNext={vi.fn()}
        />,
      );
      expect(screen.getByTestId('pagination-prev')).toBeDisabled();
    });

    it('enables Previous on subsequent pages', () => {
      render(
        <Pagination
          page={2}
          pageSize={25}
          dataLength={25}
          onPrevious={vi.fn()}
          onNext={vi.fn()}
        />,
      );
      expect(screen.getByTestId('pagination-prev')).not.toBeDisabled();
    });

    it('disables Next when data is less than page size', () => {
      render(
        <Pagination
          page={0}
          pageSize={25}
          dataLength={10}
          onPrevious={vi.fn()}
          onNext={vi.fn()}
        />,
      );
      expect(screen.getByTestId('pagination-next')).toBeDisabled();
    });

    it('enables Next when data fills page', () => {
      render(
        <Pagination
          page={0}
          pageSize={25}
          dataLength={25}
          onPrevious={vi.fn()}
          onNext={vi.fn()}
        />,
      );
      expect(screen.getByTestId('pagination-next')).not.toBeDisabled();
    });

    it('calls onPrevious when Previous is clicked', () => {
      const onPrev = vi.fn();
      render(
        <Pagination
          page={1}
          pageSize={25}
          dataLength={25}
          onPrevious={onPrev}
          onNext={vi.fn()}
        />,
      );
      fireEvent.click(screen.getByTestId('pagination-prev'));
      expect(onPrev).toHaveBeenCalled();
    });

    it('calls onNext when Next is clicked', () => {
      const onNext = vi.fn();
      render(
        <Pagination
          page={0}
          pageSize={25}
          dataLength={25}
          onPrevious={vi.fn()}
          onNext={onNext}
        />,
      );
      fireEvent.click(screen.getByTestId('pagination-next'));
      expect(onNext).toHaveBeenCalled();
    });
  });

  describe('ColumnSelector', () => {
    it('renders the toggle button', () => {
      render(
        <ColumnSelector
          columns={defaultColumns}
          visibleFields={new Set(['id', 'name', 'email'])}
          onToggle={vi.fn()}
        />,
      );
      expect(screen.getByTestId('column-selector-toggle')).toBeInTheDocument();
    });

    it('opens the menu when toggle is clicked', () => {
      render(
        <ColumnSelector
          columns={defaultColumns}
          visibleFields={new Set(['id', 'name', 'email'])}
          onToggle={vi.fn()}
        />,
      );
      fireEvent.click(screen.getByTestId('column-selector-toggle'));
      expect(screen.getByTestId('column-selector-menu')).toBeInTheDocument();
    });

    it('shows all columns in the menu', () => {
      render(
        <ColumnSelector
          columns={defaultColumns}
          visibleFields={new Set(['id', 'name', 'email'])}
          onToggle={vi.fn()}
        />,
      );
      fireEvent.click(screen.getByTestId('column-selector-toggle'));
      expect(screen.getByTestId('column-toggle-id')).toBeInTheDocument();
      expect(screen.getByTestId('column-toggle-name')).toBeInTheDocument();
      expect(screen.getByTestId('column-toggle-email')).toBeInTheDocument();
    });

    it('checks visible columns', () => {
      render(
        <ColumnSelector
          columns={defaultColumns}
          visibleFields={new Set(['id', 'email'])}
          onToggle={vi.fn()}
        />,
      );
      fireEvent.click(screen.getByTestId('column-selector-toggle'));
      expect(screen.getByTestId('column-toggle-id')).toBeChecked();
      expect(screen.getByTestId('column-toggle-name')).not.toBeChecked();
      expect(screen.getByTestId('column-toggle-email')).toBeChecked();
    });

    it('calls onToggle when a column checkbox is changed', () => {
      const onToggle = vi.fn();
      render(
        <ColumnSelector
          columns={defaultColumns}
          visibleFields={new Set(['id', 'name', 'email'])}
          onToggle={onToggle}
        />,
      );
      fireEvent.click(screen.getByTestId('column-selector-toggle'));
      fireEvent.click(screen.getByTestId('column-toggle-name'));
      expect(onToggle).toHaveBeenCalledWith('name');
    });

    it('closes and reopens the menu on toggle', () => {
      render(
        <ColumnSelector
          columns={defaultColumns}
          visibleFields={new Set(['id', 'name', 'email'])}
          onToggle={vi.fn()}
        />,
      );
      const toggle = screen.getByTestId('column-selector-toggle');
      fireEvent.click(toggle);
      expect(screen.getByTestId('column-selector-menu')).toBeInTheDocument();
      fireEvent.click(toggle);
      expect(screen.queryByTestId('column-selector-menu')).not.toBeInTheDocument();
    });
  });

  describe('FilterBuilder', () => {
    const filterableColumns: ColumnConfig[] = [
      { field: 'name', header: 'Name', filterable: true },
      { field: 'email', header: 'Email', filterable: true },
    ];

    it('renders when there are filterable columns', () => {
      render(
        <FilterBuilder columns={filterableColumns} onApply={vi.fn()} />,
      );
      expect(screen.getByTestId('filter-builder')).toBeInTheDocument();
    });

    it('does not render when no filterable columns', () => {
      const cols: ColumnConfig[] = [
        { field: 'id', header: 'ID' },
      ];
      const { container } = render(
        <FilterBuilder columns={cols} onApply={vi.fn()} />,
      );
      expect(container.innerHTML).toBe('');
    });

    it('renders field select with filterable columns', () => {
      render(
        <FilterBuilder columns={filterableColumns} onApply={vi.fn()} />,
      );
      const select = screen.getByTestId('filter-field-select');
      expect(select).toBeInTheDocument();
      expect(select.querySelectorAll('option')).toHaveLength(2);
    });

    it('renders value input', () => {
      render(
        <FilterBuilder columns={filterableColumns} onApply={vi.fn()} />,
      );
      expect(screen.getByTestId('filter-value-input')).toBeInTheDocument();
    });

    it('calls onApply with filter when Apply is clicked', () => {
      const onApply = vi.fn();
      render(
        <FilterBuilder columns={filterableColumns} onApply={onApply} />,
      );
      const input = screen.getByTestId('filter-value-input');
      fireEvent.change(input, { target: { value: 'Alice' } });
      fireEvent.click(screen.getByTestId('filter-apply-button'));
      expect(onApply).toHaveBeenCalledWith({
        name: { _contains: 'Alice' },
      });
    });

    it('calls onApply on Enter key', () => {
      const onApply = vi.fn();
      render(
        <FilterBuilder columns={filterableColumns} onApply={onApply} />,
      );
      const input = screen.getByTestId('filter-value-input');
      fireEvent.change(input, { target: { value: 'test' } });
      fireEvent.keyDown(input, { key: 'Enter' });
      expect(onApply).toHaveBeenCalledWith({
        name: { _contains: 'test' },
      });
    });

    it('does not call onApply when value is empty', () => {
      const onApply = vi.fn();
      render(
        <FilterBuilder columns={filterableColumns} onApply={onApply} />,
      );
      fireEvent.click(screen.getByTestId('filter-apply-button'));
      expect(onApply).not.toHaveBeenCalled();
    });

    it('clears value after apply', () => {
      const onApply = vi.fn();
      render(
        <FilterBuilder columns={filterableColumns} onApply={onApply} />,
      );
      const input = screen.getByTestId('filter-value-input') as HTMLInputElement;
      fireEvent.change(input, { target: { value: 'test' } });
      fireEvent.click(screen.getByTestId('filter-apply-button'));
      expect(input.value).toBe('');
    });

    it('uses selected field in the filter', () => {
      const onApply = vi.fn();
      render(
        <FilterBuilder columns={filterableColumns} onApply={onApply} />,
      );
      const select = screen.getByTestId('filter-field-select');
      fireEvent.change(select, { target: { value: 'email' } });
      const input = screen.getByTestId('filter-value-input');
      fireEvent.change(input, { target: { value: 'test@test.com' } });
      fireEvent.click(screen.getByTestId('filter-apply-button'));
      expect(onApply).toHaveBeenCalledWith({
        email: { _contains: 'test@test.com' },
      });
    });
  });

  describe('ExportMenu', () => {
    const mockData = [
      { id: 1, name: 'Alice' },
      { id: 2, name: 'Bob' },
    ];
    const exportColumns: ColumnConfig[] = [
      { field: 'id', header: 'ID' },
      { field: 'name', header: 'Name' },
    ];

    it('renders the toggle button', () => {
      render(
        <ExportMenu data={mockData} columns={exportColumns} queryName="test" />,
      );
      expect(screen.getByTestId('export-menu-toggle')).toBeInTheDocument();
    });

    it('opens dropdown on toggle click', () => {
      render(
        <ExportMenu data={mockData} columns={exportColumns} queryName="test" />,
      );
      fireEvent.click(screen.getByTestId('export-menu-toggle'));
      expect(screen.getByTestId('export-menu-dropdown')).toBeInTheDocument();
    });

    it('shows CSV and JSON options by default', () => {
      render(
        <ExportMenu data={mockData} columns={exportColumns} queryName="test" />,
      );
      fireEvent.click(screen.getByTestId('export-menu-toggle'));
      expect(screen.getByTestId('export-csv-button')).toBeInTheDocument();
      expect(screen.getByTestId('export-json-button')).toBeInTheDocument();
    });

    it('shows only specified formats', () => {
      render(
        <ExportMenu
          data={mockData}
          columns={exportColumns}
          queryName="test"
          formats={['json']}
        />,
      );
      fireEvent.click(screen.getByTestId('export-menu-toggle'));
      expect(screen.queryByTestId('export-csv-button')).not.toBeInTheDocument();
      expect(screen.getByTestId('export-json-button')).toBeInTheDocument();
    });

    it('closes dropdown after export', () => {
      const origCreateObjectURL = globalThis.URL.createObjectURL;
      const origRevokeObjectURL = globalThis.URL.revokeObjectURL;
      globalThis.URL.createObjectURL = vi.fn().mockReturnValue('blob:test');
      globalThis.URL.revokeObjectURL = vi.fn();

      render(
        <ExportMenu data={mockData} columns={exportColumns} queryName="test" />,
      );

      const origCreate = document.createElement.bind(document);
      const mockClick = vi.fn();
      vi.spyOn(document, 'createElement').mockImplementation((tag: string) => {
        if (tag === 'a') {
          return { href: '', download: '', click: mockClick } as unknown as HTMLAnchorElement;
        }
        return origCreate(tag);
      });

      fireEvent.click(screen.getByTestId('export-menu-toggle'));
      fireEvent.click(screen.getByTestId('export-json-button'));
      expect(screen.queryByTestId('export-menu-dropdown')).not.toBeInTheDocument();

      vi.restoreAllMocks();
      globalThis.URL.createObjectURL = origCreateObjectURL;
      globalThis.URL.revokeObjectURL = origRevokeObjectURL;
    });

    it('toggles dropdown open and closed', () => {
      render(
        <ExportMenu data={mockData} columns={exportColumns} queryName="test" />,
      );
      const toggle = screen.getByTestId('export-menu-toggle');
      fireEvent.click(toggle);
      expect(screen.getByTestId('export-menu-dropdown')).toBeInTheDocument();
      fireEvent.click(toggle);
      expect(screen.queryByTestId('export-menu-dropdown')).not.toBeInTheDocument();
    });
  });
});
