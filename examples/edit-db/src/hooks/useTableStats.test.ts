import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React from 'react';
import { useTableStats, buildRowCountQuery, abbreviateNumber, calculateBarWidth } from './useTableStats';

// Mock the dependencies
vi.mock('./useSchema', () => ({
  useSchema: vi.fn(),
}));

vi.mock('../common/fetcher', () => ({
  useFetcher: vi.fn(),
}));

import { useSchema } from './useSchema';
import { useFetcher } from '../common/fetcher';

const mockUseSchema = vi.mocked(useSchema);
const mockUseFetcher = vi.mocked(useFetcher);

const createWrapper = () => {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  return ({ children }: { children: React.ReactNode }) => (
    React.createElement(QueryClientProvider, { client: queryClient }, children)
  );
};

describe('useTableStats', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('returns empty stats when schema is loading', () => {
    mockUseSchema.mockReturnValue({
      loading: true,
      error: null,
      data: [],
      findTable: () => undefined,
    });

    mockUseFetcher.mockReturnValue({
      query: vi.fn(),
    });

    const { result } = renderHook(() => useTableStats(), {
      wrapper: createWrapper(),
    });

    expect(result.current.isLoading).toBe(true);
    expect(result.current.stats).toEqual({});
  });

  it('returns stats with schema data even before row counts load', async () => {
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
    ];

    mockUseSchema.mockReturnValue({
      loading: false,
      error: null,
      data: mockTables as unknown as ReturnType<typeof useSchema>['data'],
      findTable: () => undefined,
    });

    const mockQuery = vi.fn().mockResolvedValue({
      customers: { total: 1250 },
    });

    mockUseFetcher.mockReturnValue({
      query: mockQuery,
    });

    const { result } = renderHook(() => useTableStats(), {
      wrapper: createWrapper(),
    });

    // Wait for the query to be called
    await waitFor(() => {
      expect(mockQuery).toHaveBeenCalled();
    });

    // After data loads
    await waitFor(() => {
      expect(result.current.stats.customers).toBeDefined();
    });

    expect(result.current.stats.customers.columnCount).toBe(3);
    expect(result.current.stats.customers.fkCount).toBe(1);
  });

  it('skips unsafe table names in the row-count query without dropping schema stats', async () => {
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
        columns: [{ name: 'id' }],
        multiJoins: [],
        singleJoins: [],
      },
      {
        name: 'bad) { injected',
        graphQlName: 'bad) { injected',
        dbName: 'bad',
        label: 'Bad',
        labelColumn: 'id',
        primaryKeys: ['id'],
        isEditable: true,
        metadata: {},
        columns: [{ name: 'id' }],
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

    const mockQuery = vi.fn().mockResolvedValue({ customers: { total: 12 } });
    mockUseFetcher.mockReturnValue({ query: mockQuery });

    const { result } = renderHook(() => useTableStats(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(mockQuery).toHaveBeenCalled());
    expect(mockQuery.mock.calls[0][0]).toBe('query GetTableRowCounts { customers: customers(limit: 1) { total } }');

    await waitFor(() => expect(result.current.stats.customers.rowCount).toBe(12));
    expect(result.current.stats['bad) { injected']).toMatchObject({
      columnCount: 1,
      rowCount: null,
    });
  });
});

describe('buildRowCountQuery', () => {
  it('builds a row-count query for valid GraphQL table names', () => {
    expect(buildRowCountQuery(['customers', '_audit2'])).toBe(
      'query GetTableRowCounts { customers: customers(limit: 1) { total } _audit2: _audit2(limit: 1) { total } }',
    );
  });

  it('skips invalid GraphQL names and returns null when none remain', () => {
    expect(buildRowCountQuery(['customers', 'bad) { injected'])).toBe(
      'query GetTableRowCounts { customers: customers(limit: 1) { total } }',
    );
    expect(buildRowCountQuery(['bad) { injected'])).toBeNull();
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
    expect(abbreviateNumber(45000)).toBe('45k');
  });

  it('returns M format for millions', () => {
    expect(abbreviateNumber(1200000)).toBe('1.2M');
  });

  it('handles exact thousands without decimal', () => {
    expect(abbreviateNumber(1000)).toBe('1k');
  });

  it('handles exact millions without decimal', () => {
    expect(abbreviateNumber(1000000)).toBe('1M');
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
  });

  it('caps at 10 for very large ratios', () => {
    expect(calculateBarWidth(200, 100)).toBe(10);
  });

  it('minimum is 1 for non-zero counts', () => {
    expect(calculateBarWidth(1, 1000)).toBe(1);
  });

  it('returns 10 for equal counts', () => {
    expect(calculateBarWidth(500, 500)).toBe(10);
  });
});
