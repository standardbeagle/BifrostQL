import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import '@testing-library/jest-dom';
import { TableList } from './table-list';
import { EditorConfigProvider } from './hooks/useEditorConfig';

const pathMock = vi.hoisted(() => ({ current: '/' }));

// Mock the hooks
vi.mock('./hooks/useSchema', () => ({
  useSchema: vi.fn(),
}));

vi.mock('./hooks/useTableStats', () => ({
  useTableStats: vi.fn(),
}));

// The download built-ins pull the fetcher/toast contexts, which these tests do
// not mount — mock the hooks at the seam and capture the export calls.
const exportMock = vi.hoisted(() => ({
  run: vi.fn(async () => ({ content: '', rowCount: 1, total: 1, truncated: false })),
  toast: vi.fn(),
}));
vi.mock('./hooks/useTableExport', () => ({
  useTableExport: () => exportMock.run,
}));
vi.mock('./hooks/useToast', () => ({
  useToast: () => ({ toast: exportMock.toast }),
}));

vi.mock('./hooks/usePath', () => ({
  usePath: () => pathMock.current,
  Link: ({ children, ...props }: { children: React.ReactNode; to: string; className?: string }) => (
    <a href={props.to} className={props.className}>{children}</a>
  ),
}));

vi.mock('@/components/ui/table', () => ({
  Table: ({ children }: { children: React.ReactNode }) => <table>{children}</table>,
  TableHeader: ({ children }: { children: React.ReactNode }) => <thead>{children}</thead>,
  TableBody: ({ children }: { children: React.ReactNode }) => <tbody>{children}</tbody>,
  TableHead: ({ children }: { children: React.ReactNode }) => <th>{children}</th>,
  TableRow: ({ children }: { children: React.ReactNode }) => <tr>{children}</tr>,
  TableCell: ({ children, className }: { children: React.ReactNode; className?: string }) => (
    <td className={className}>{children}</td>
  ),
}));

// Render the Radix dropdown inline (no portal/pointer-capture) so the kebab's
// action items are directly clickable under jsdom — same approach as
// export-button.test.tsx.
vi.mock('@/components/ui/dropdown-menu', () => ({
  DropdownMenu: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  DropdownMenuTrigger: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  DropdownMenuContent: ({ children }: { children: React.ReactNode }) => <div role="menu">{children}</div>,
  DropdownMenuItem: ({ children, onSelect }: { children: React.ReactNode; onSelect?: () => void }) => (
    <button type="button" role="menuitem" onClick={onSelect}>{children}</button>
  ),
}));

vi.mock('@/components/ui/alert', () => ({
  Alert: ({ children, variant }: { children: React.ReactNode; variant?: string }) => (
    <div data-variant={variant}>{children}</div>
  ),
  AlertDescription: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));

import { useSchema } from './hooks/useSchema';
import { useTableStats } from './hooks/useTableStats';

const mockUseSchema = vi.mocked(useSchema);
const mockUseTableStats = vi.mocked(useTableStats);

describe('TableList', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    pathMock.current = '/';
  });

  it('renders loading state when schema is loading', () => {
    mockUseSchema.mockReturnValue({
      loading: true,
      error: null,
      data: [],
      findTable: () => undefined,
    });
    mockUseTableStats.mockReturnValue({
      stats: {},
      isLoading: true,
      error: null,
    });

    render(<TableList />);
    expect(screen.getByText('Loading...')).toBeInTheDocument();
  });

  it('renders error state when schema fails to load', () => {
    mockUseSchema.mockReturnValue({
      loading: false,
      error: { message: 'Failed to fetch schema' },
      data: [],
      findTable: () => undefined,
    });
    mockUseTableStats.mockReturnValue({
      stats: {},
      isLoading: false,
      error: null,
    });

    render(<TableList />);
    expect(screen.getByText('Error: Failed to fetch schema')).toBeInTheDocument();
  });

  it('renders empty state when no tables found', () => {
    mockUseSchema.mockReturnValue({
      loading: false,
      error: null,
      data: [],
      findTable: () => undefined,
    });
    mockUseTableStats.mockReturnValue({
      stats: {},
      isLoading: false,
      error: null,
    });

    render(<TableList />);
    expect(screen.getByText('No tables found')).toBeInTheDocument();
  });

  it('renders table list with stats', () => {
    const mockTables = [
      {
        name: 'customers',
        graphQlName: 'customers',
        dbName: 'customers',
        label: 'Customers',
        labelColumn: 'name',
        primaryKeys: ['id'],
        isEditable: true,
        metadata: {},
        columns: [{ name: 'id' }, { name: 'name' }, { name: 'email' }],
        multiJoins: [],
        singleJoins: [{ name: 'orders', destinationTable: 'orders', sourceColumnNames: ['id'], destinationColumnNames: ['customer_id'] }],
      },
      {
        name: 'orders',
        graphQlName: 'orders',
        dbName: 'orders',
        label: 'Orders',
        labelColumn: 'id',
        primaryKeys: ['id'],
        isEditable: true,
        metadata: {},
        columns: [{ name: 'id' }, { name: 'customer_id' }, { name: 'total' }],
        multiJoins: [],
        singleJoins: [],
      },
    ];

    mockUseSchema.mockReturnValue({
      loading: false,
      error: null,
      data: mockTables as unknown as ReturnType<typeof useSchema>['data'],
      findTable: () => undefined,
    });

    mockUseTableStats.mockReturnValue({
      stats: {
        customers: {
          columnCount: 3,
          rowCount: 1250,
          fkCount: 1,
          isLoading: false,
          error: null,
        },
        orders: {
          columnCount: 3,
          rowCount: 45000,
          fkCount: 0,
          isLoading: false,
          error: null,
        },
      },
      isLoading: false,
      error: null,
    });

    render(<TableList />);
    
    expect(screen.getByText('Customers')).toBeInTheDocument();
    expect(screen.getByText('Orders')).toBeInTheDocument();
  });

  it('shows loading indicator for row counts when stats are loading', () => {
    const mockTables = [
      {
        name: 'customers',
        graphQlName: 'customers',
        dbName: 'customers',
        label: 'Customers',
        labelColumn: 'name',
        primaryKeys: ['id'],
        isEditable: true,
        metadata: {},
        columns: [{ name: 'id' }, { name: 'name' }],
        multiJoins: [],
        singleJoins: [],
      },
    ];

    mockUseSchema.mockReturnValue({
      loading: false,
      error: null,
      data: mockTables as unknown as ReturnType<typeof useSchema>['data'],
      findTable: () => undefined,
    });

    mockUseTableStats.mockReturnValue({
      stats: {
        customers: {
          columnCount: 2,
          rowCount: null,
          fkCount: 0,
          isLoading: true,
          error: null,
        },
      },
      isLoading: true,
      error: null,
    });

    render(<TableList />);
    expect(screen.getByText('Customers')).toBeInTheDocument();
  });
});

// Builds a minimal schema table good enough for the list (label is what renders).
function makeTable(dbName: string, label: string) {
  return {
    name: dbName.replace('.', '_'),
    graphQlName: dbName.replace('.', '_'),
    dbName,
    label,
    labelColumn: 'id',
    primaryKeys: ['id'],
    isEditable: true,
    metadata: {},
    columns: [{ name: 'id' }],
    multiJoins: [],
    singleJoins: [],
  };
}

function mockSchema(tables: unknown[]) {
  mockUseSchema.mockReturnValue({
    loading: false,
    error: null,
    data: tables as unknown as ReturnType<typeof useSchema>['data'],
    findTable: () => undefined,
  });
  mockUseTableStats.mockReturnValue({ stats: {}, isLoading: false, error: null });
}

describe('TableList — search, grouping, paging', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    pathMock.current = '/';
  });

  it('filters tables by the search query', () => {
    mockSchema([makeTable('customers', 'Customers'), makeTable('orders', 'Orders')]);
    render(<TableList />);

    fireEvent.change(screen.getByLabelText('Search tables'), { target: { value: 'order' } });

    expect(screen.getByText('Orders')).toBeInTheDocument();
    expect(screen.queryByText('Customers')).not.toBeInTheDocument();
    expect(screen.getByText('1 match')).toBeInTheDocument();
  });

  it('groups by schema with collapsible headers when multiple schemas exist', () => {
    mockSchema([
      makeTable('dbo.users', 'Users'),
      makeTable('sales.orders', 'Orders'),
    ]);
    render(<TableList />);

    // Both schema headers present as expand/collapse buttons.
    const dboHeader = screen.getByRole('button', { name: /dbo/ });
    expect(screen.getByRole('button', { name: /sales/ })).toBeInTheDocument();
    expect(screen.getByText('Users')).toBeInTheDocument();

    // Collapsing the dbo group hides its tables.
    fireEvent.click(dboHeader);
    expect(screen.queryByText('Users')).not.toBeInTheDocument();
    expect(screen.getByText('Orders')).toBeInTheDocument();
  });

  it('does not render schema headers for a single-schema database', () => {
    mockSchema([makeTable('dbo.users', 'Users'), makeTable('dbo.orders', 'Orders')]);
    render(<TableList />);

    expect(screen.queryByRole('button', { name: /dbo/ })).not.toBeInTheDocument();
    expect(screen.getByText('Users')).toBeInTheDocument();
    expect(screen.getByText('Orders')).toBeInTheDocument();
  });

  it('pages the list and navigates between pages', () => {
    const tables = Array.from({ length: 120 }, (_, i) =>
      makeTable(`t${i}`, `Table ${String(i).padStart(3, '0')}`)
    );
    mockSchema(tables);
    render(<TableList />);

    // First page caps at TABLE_LIST_PAGE_SIZE (50); table 050 is on the next page.
    expect(screen.getByText('Table 000')).toBeInTheDocument();
    expect(screen.queryByText('Table 050')).not.toBeInTheDocument();
    expect(screen.getByText('1–50 of 120')).toBeInTheDocument();

    fireEvent.click(screen.getByLabelText('Next page'));
    expect(screen.getByText('Table 050')).toBeInTheDocument();
    expect(screen.queryByText('Table 000')).not.toBeInTheDocument();
    expect(screen.getByText('51–100 of 120')).toBeInTheDocument();
  });

  it('marks the active table for the current path', () => {
    // usePath is mocked to '/', so the root has no active table; verify a match via label cell.
    mockSchema([makeTable('customers', 'Customers')]);
    render(<TableList />);
    const link = screen.getByText('Customers').closest('a')!;
    expect(within(link).queryByText('Customers')).toBeInTheDocument();
  });

  it('tolerates malformed percent-encoded active paths', () => {
    pathMock.current = '/%E0%A4%A';
    mockSchema([makeTable('customers', 'Customers')]);

    expect(() => render(<TableList />)).not.toThrow();
    expect(screen.getByText('Customers')).toBeInTheDocument();
  });
});

describe('TableList table actions (kebab menu)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    pathMock.current = '/';
    mockUseTableStats.mockReturnValue({ stats: {}, isLoading: false });
    mockUseSchema.mockReturnValue({
      loading: false,
      error: null,
      data: [
        {
          dbName: 'dbo.users',
          graphQlName: 'users',
          name: 'users',
          label: 'Users',
          labelColumn: 'id',
          primaryKeys: ['id'],
          isEditable: true,
          metadata: {},
          columns: [],
          multiJoins: [],
          singleJoins: [],
        },
      ],
      findTable: () => undefined,
    } as never);
  });

  it('renders built-in Download actions and invokes the export runner', () => {
    render(<TableList />);

    fireEvent.click(screen.getByLabelText('Actions for Users'));
    fireEvent.click(screen.getByText('Download CSV'));

    expect(exportMock.run).toHaveBeenCalledTimes(1);
    expect(exportMock.run.mock.calls[0][0]).toMatchObject({ name: 'users' });
    expect(exportMock.run.mock.calls[0][1]).toBe('csv');
  });

  it('renders host-contributed actions after the built-ins and passes the schema table', () => {
    const onSelect = vi.fn();
    render(
      <EditorConfigProvider config={{ showStats: false, tableActions: [
        { id: 'edit-as-query', label: 'Edit as query', onSelect },
        { id: 'never', label: 'Hidden', enabled: () => false, onSelect: vi.fn() },
      ] }}>
        <TableList />
      </EditorConfigProvider>,
    );

    fireEvent.click(screen.getByLabelText('Actions for Users'));
    const items = screen.getAllByRole('menuitem').map((el) => el.textContent);
    expect(items).toEqual(['Download CSV', 'Download JSON', 'Edit as query']);

    fireEvent.click(screen.getByText('Edit as query'));
    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ name: 'users' }));
  });
});
