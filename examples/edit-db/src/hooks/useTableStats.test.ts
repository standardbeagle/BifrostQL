import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React from 'react';
import { useTableStats, buildRowCountQuery } from './useTableStats';

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

