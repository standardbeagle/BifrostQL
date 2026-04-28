import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { TableList } from './tableList';
import { abbreviateNumber, calculateBarWidth } from './hooks/useTableStats';

// Mock the hooks
vi.mock('./hooks/useSchema', () => ({
  useSchema: vi.fn(),
}));

vi.mock('./hooks/useTableStats', () => ({
  useTableStats: vi.fn(),
  abbreviateNumber: vi.fn((num: number | null) => {
    if (num === null) return '—';
    if (num === 0) return '0';
    if (num < 1000) return num.toString();
    if (num < 1000000) return `${(num / 1000).toFixed(1)}k`;
    return `${(num / 1000000).toFixed(1)}M`;
  }),
  calculateBarWidth: vi.fn((count: number | null, maxCount: number) => {
    if (count === null || maxCount === 0) return 0;
    const ratio = count / maxCount;
    return Math.max(1, Math.min(10, Math.ceil(ratio * 10)));
  }),
}));

vi.mock('./hooks/usePath', () => ({
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

describe('abbreviateNumber', () => {
  it('returns em dash for null', () => {
    expect(abbreviateNumber(null)).toBe('—');
  });

  it('returns 0 for zero', () => {
    expect(abbreviateNumber(0)).toBe('0');
  });

  it('returns number as string for values under 1000', () => {
    expect(abbreviateNumber(12)).toBe('12');
    expect(abbreviateNumber(999)).toBe('999');
  });

  it('returns k format for thousands', () => {
    expect(abbreviateNumber(1200)).toBe('1.2k');
    expect(abbreviateNumber(45000)).toBe('45.0k');
  });

  it('returns M format for millions', () => {
    expect(abbreviateNumber(1200000)).toBe('1.2M');
    expect(abbreviateNumber(5000000)).toBe('5.0M');
  });
});

describe('calculateBarWidth', () => {
  it('returns 0 for null count', () => {
    expect(calculateBarWidth(null, 100)).toBe(0);
  });

  it('returns 0 when maxCount is 0', () => {
    expect(calculateBarWidth(100, 0)).toBe(0);
  });

  it('calculates correct bar width based on ratio', () => {
    expect(calculateBarWidth(50, 100)).toBe(5);
    expect(calculateBarWidth(100, 100)).toBe(10);
    expect(calculateBarWidth(1, 100)).toBe(1);
  });

  it('caps at 10 for very large ratios', () => {
    expect(calculateBarWidth(200, 100)).toBe(10);
  });

  it('minimum is 1 for non-zero counts', () => {
    expect(calculateBarWidth(1, 1000)).toBe(1);
  });
});
