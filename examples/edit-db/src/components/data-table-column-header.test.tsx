import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { DataTableColumnHeader } from './data-table-column-header';
import type { Column, Table } from '@tanstack/react-table';
import type { Column as ColumnSchema } from '@/types/schema';

// Mock the UI components
vi.mock('@/components/ui/button', () => ({
  Button: ({ children, ...props }: { children: React.ReactNode }) => (
    <button {...props}>{children}</button>
  ),
}));

vi.mock('@/components/ui/dropdown-menu', () => ({
  DropdownMenu: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  DropdownMenuTrigger: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  DropdownMenuContent: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  DropdownMenuItem: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  DropdownMenuSeparator: () => <hr />,
  DropdownMenuSub: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  DropdownMenuSubTrigger: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  DropdownMenuSubContent: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));

vi.mock('@/components/ui/hover-card', () => ({
  HoverCard: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  HoverCardTrigger: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  HoverCardContent: ({ children }: { children: React.ReactNode }) => (
    <div data-testid="hover-card-content">{children}</div>
  ),
}));

vi.mock('@/components/filters/text-filter', () => ({
  TextFilter: () => <div>TextFilter</div>,
}));

vi.mock('@/components/filters/number-filter', () => ({
  NumberFilter: () => <div>NumberFilter</div>,
}));

vi.mock('@/components/filters/date-filter', () => ({
  DateFilter: () => <div>DateFilter</div>,
}));

vi.mock('@/components/filters/boolean-filter', () => ({
  BooleanFilter: () => <div>BooleanFilter</div>,
}));

vi.mock('@/components/filters/fk-filter', () => ({
  FkFilter: () => <div>FkFilter</div>,
}));

function createMockColumn(overrides: Partial<ColumnSchema> = {}): ColumnSchema {
  return {
    dbName: 'test_column',
    graphQlName: 'testColumn',
    name: 'test_column',
    label: 'Test Column',
    paramType: 'String',
    dbType: 'varchar',
    isPrimaryKey: false,
    isIdentity: false,
    isNullable: true,
    isReadOnly: false,
    metadata: {},
    ...overrides,
  };
}

function createMockTanStackColumn(columnSchema: ColumnSchema | undefined): Column<unknown, unknown> {
  return {
    id: 'test_column',
    columnDef: {
      meta: columnSchema ? { column: columnSchema, paramType: columnSchema.paramType } : undefined,
    },
    getCanSort: () => true,
    getIsSorted: () => false,
    getFilterValue: () => undefined,
    getCanHide: () => true,
    toggleSorting: vi.fn(),
    clearSorting: vi.fn(),
    toggleVisibility: vi.fn(),
  } as unknown as Column<unknown, unknown>;
}

function createMockTable(): Table<unknown> {
  return {
    options: {
      meta: {},
    },
  } as unknown as Table<unknown>;
}

describe('DataTableColumnHeader', () => {
  it('renders the column title', () => {
    const columnSchema = createMockColumn();
    const column = createMockTanStackColumn(columnSchema);
    const table = createMockTable();

    render(<DataTableColumnHeader column={column} table={table} title="Test Column" />);
    
    // Should find the title in the button
    const buttons = screen.getAllByText('Test Column');
    expect(buttons.length).toBeGreaterThan(0);
  });

  it('shows hover card content when column schema is available', () => {
    const columnSchema = createMockColumn();
    const column = createMockTanStackColumn(columnSchema);
    const table = createMockTable();

    render(<DataTableColumnHeader column={column} table={table} title="Test Column" />);
    
    // Hover card content should be rendered when column schema exists
    expect(screen.getByTestId('hover-card-content')).toBeInTheDocument();
  });

  it('does not show hover card when column schema is not available', () => {
    const column = createMockTanStackColumn(undefined);
    const table = createMockTable();

    render(<DataTableColumnHeader column={column} table={table} title="Test Column" />);
    
    // Hover card content should not be rendered
    expect(screen.queryByTestId('hover-card-content')).not.toBeInTheDocument();
  });
});

describe('ColumnMetadataTooltip', () => {
  it('displays correct data type for varchar column', () => {
    const columnSchema = createMockColumn({
      dbType: 'varchar',
      paramType: 'String',
      maxLength: 255,
    });
    const column = createMockTanStackColumn(columnSchema);
    const table = createMockTable();

    render(<DataTableColumnHeader column={column} table={table} title="Test Column" />);
    
    const hoverContent = screen.getByTestId('hover-card-content');
    expect(hoverContent.textContent).toContain('VARCHAR(255)');
  });

  it('displays primary key indicator', () => {
    const columnSchema = createMockColumn({
      isPrimaryKey: true,
      isNullable: false,
    });
    const column = createMockTanStackColumn(columnSchema);
    const table = createMockTable();

    render(<DataTableColumnHeader column={column} table={table} title="ID" />);
    
    const hoverContent = screen.getByTestId('hover-card-content');
    expect(hoverContent.textContent).toContain('Primary Key');
    expect(hoverContent.textContent).toContain('Yes');
  });

  it('displays auto increment indicator', () => {
    const columnSchema = createMockColumn({
      isIdentity: true,
    });
    const column = createMockTanStackColumn(columnSchema);
    const table = createMockTable();

    render(<DataTableColumnHeader column={column} table={table} title="ID" />);
    
    const hoverContent = screen.getByTestId('hover-card-content');
    expect(hoverContent.textContent).toContain('Auto Increment');
  });

  it('displays nullable status', () => {
    const columnSchema = createMockColumn({
      isNullable: false,
    });
    const column = createMockTanStackColumn(columnSchema);
    const table = createMockTable();

    render(<DataTableColumnHeader column={column} table={table} title="Required" />);
    
    const hoverContent = screen.getByTestId('hover-card-content');
    expect(hoverContent.textContent).toContain('Nullable');
    expect(hoverContent.textContent).toContain('No');
  });

  it('displays default value', () => {
    const columnSchema = createMockColumn({
      defaultValue: 'CURRENT_TIMESTAMP',
    });
    const column = createMockTanStackColumn(columnSchema);
    const table = createMockTable();

    render(<DataTableColumnHeader column={column} table={table} title="Created At" />);
    
    const hoverContent = screen.getByTestId('hover-card-content');
    expect(hoverContent.textContent).toContain('Default');
    expect(hoverContent.textContent).toContain('CURRENT_TIMESTAMP');
  });

  it('displays foreign key relationship', () => {
    const columnSchema = createMockColumn({
      paramType: 'Int',
      dbType: 'int',
    });
    const column = {
      ...createMockTanStackColumn(columnSchema),
      columnDef: {
        meta: { 
          column: columnSchema, 
          paramType: 'Int',
          joinTable: 'users',
          joinLabelColumn: 'name',
        },
      },
    } as unknown as Column<unknown, unknown>;
    const table = createMockTable();

    render(<DataTableColumnHeader column={column} table={table} title="User ID" />);
    
    const hoverContent = screen.getByTestId('hover-card-content');
    expect(hoverContent.textContent).toContain('Foreign Key');
    expect(hoverContent.textContent).toContain('users');
  });

  it('displays numeric constraints', () => {
    const columnSchema = createMockColumn({
      paramType: 'Int',
      dbType: 'int',
      min: 0,
      max: 100,
    });
    const column = createMockTanStackColumn(columnSchema);
    const table = createMockTable();

    render(<DataTableColumnHeader column={column} table={table} title="Score" />);
    
    const hoverContent = screen.getByTestId('hover-card-content');
    expect(hoverContent.textContent).toContain('Min');
    expect(hoverContent.textContent).toContain('0');
    expect(hoverContent.textContent).toContain('Max');
    expect(hoverContent.textContent).toContain('100');
  });
});
