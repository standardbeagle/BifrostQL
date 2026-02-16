import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '../components/bifrost-provider';
import { useBifrostTable } from './use-bifrost-table';
import type {
  ColumnConfig,
  AggregateConfig,
  UrlSyncConfig,
  CustomSortFn,
} from './use-bifrost-table';

function createFetchMock(response: unknown, ok = true, status = 200) {
  return vi.fn().mockResolvedValue({
    ok,
    status,
    statusText: ok ? 'OK' : 'Internal Server Error',
    json: () => Promise.resolve(response),
  });
}

function createWrapper(
  endpoint = 'http://localhost:5000/graphql',
  headers?: Record<string, string>,
) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
    },
  });

  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <BifrostProvider config={{ endpoint, headers }}>
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

describe('useBifrostTable', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  describe('column configuration', () => {
    it('returns configured columns', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      expect(result.current.columns).toEqual(defaultColumns);
    });

    it('derives fields from columns when fields not provided', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>)
        .mock.calls[0];
      const body = JSON.parse(fetchOptions.body);
      expect(body.query).toContain('id');
      expect(body.query).toContain('name');
      expect(body.query).toContain('email');
    });

    it('uses explicit fields when provided', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            fields: ['id', 'name'],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>)
        .mock.calls[0];
      const body = JSON.parse(fetchOptions.body);
      expect(body.query).toContain('id');
      expect(body.query).toContain('name');
      expect(body.query).not.toContain('email');
    });
  });

  describe('data fetching', () => {
    it('fetches data and returns rows', async () => {
      const mockUsers = [
        { id: 1, name: 'Alice', email: 'alice@test.com' },
        { id: 2, name: 'Bob', email: 'bob@test.com' },
      ];
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.data).toEqual(mockUsers);
    });

    it('returns empty array when no data', async () => {
      globalThis.fetch = createFetchMock({ data: { other: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.data).toEqual([]);
    });

    it('exposes loading state', () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      expect(result.current.loading).toBe(true);
    });

    it('exposes error state', async () => {
      globalThis.fetch = createFetchMock({}, false, 500);

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            retry: false,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.error).not.toBeNull());

      expect(result.current.error?.message).toContain('500');
    });

    it('exposes refetch function', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(typeof result.current.refetch).toBe('function');
    });
  });

  describe('sorting', () => {
    it('applies default sort', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'asc' },
      ]);

      const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>)
        .mock.calls[0];
      const body = JSON.parse(fetchOptions.body);
      expect(body.query).toContain('sort:');
      expect(body.query).toContain('name asc');
    });

    it('toggleSort cycles asc -> desc -> none', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.toggleSort('name');
      });
      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'asc' },
      ]);

      act(() => {
        result.current.sorting.toggleSort('name');
      });
      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'desc' },
      ]);

      act(() => {
        result.current.sorting.toggleSort('name');
      });
      expect(result.current.sorting.current).toEqual([]);
    });

    it('ignores toggleSort for non-sortable columns', async () => {
      const columns: ColumnConfig[] = [
        { field: 'id', header: 'ID', sortable: false },
        { field: 'name', header: 'Name', sortable: true },
      ];
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.toggleSort('id');
      });
      expect(result.current.sorting.current).toEqual([]);
    });

    it('replaces sort in single-sort mode', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            multiSort: false,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.toggleSort('name');
      });
      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'asc' },
      ]);

      act(() => {
        result.current.sorting.toggleSort('email');
      });
      expect(result.current.sorting.current).toEqual([
        { field: 'email', direction: 'asc' },
      ]);
    });

    it('accumulates sorts in multi-sort mode', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            multiSort: true,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.toggleSort('name');
      });
      act(() => {
        result.current.sorting.toggleSort('email');
      });
      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'asc' },
        { field: 'email', direction: 'asc' },
      ]);
    });

    it('setSorting replaces all sorts', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.setSorting([
          { field: 'email', direction: 'desc' },
        ]);
      });
      expect(result.current.sorting.current).toEqual([
        { field: 'email', direction: 'desc' },
      ]);
    });

    it('resets page to 0 when sorting changes', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.pagination.setPage(3);
      });
      expect(result.current.pagination.page).toBe(3);

      act(() => {
        result.current.sorting.toggleSort('name');
      });
      expect(result.current.pagination.page).toBe(0);
    });
  });

  describe('filtering', () => {
    it('applies filters to query', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultFilters: { name: { _contains: 'alice' } },
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>)
        .mock.calls[0];
      const body = JSON.parse(fetchOptions.body);
      expect(body.query).toContain('filter:');
      expect(body.query).toContain('_contains');
    });

    it('setColumnFilter adds a filter for a specific field', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.filters.setColumnFilter('name', { _eq: 'Alice' });
      });
      expect(result.current.filters.current).toEqual({
        name: { _eq: 'Alice' },
      });
    });

    it('setColumnFilter removes filter when value is null', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultFilters: { name: { _eq: 'Alice' } },
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.filters.setColumnFilter('name', null);
      });
      expect(result.current.filters.current).toEqual({});
    });

    it('setColumnFilter removes filter when value is empty string', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultFilters: { name: 'Alice' },
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.filters.setColumnFilter('name', '');
      });
      expect(result.current.filters.current).toEqual({});
    });

    it('setFilters replaces all filters', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultFilters: { name: 'Alice' },
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.filters.setFilters({ email: { _contains: 'test' } });
      });
      expect(result.current.filters.current).toEqual({
        email: { _contains: 'test' },
      });
    });

    it('clearFilters removes all filters', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultFilters: { name: 'Alice', email: 'test' },
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.filters.clearFilters();
      });
      expect(result.current.filters.current).toEqual({});
    });

    it('resets page to 0 when filters change', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.pagination.setPage(2);
      });
      expect(result.current.pagination.page).toBe(2);

      act(() => {
        result.current.filters.setColumnFilter('name', 'test');
      });
      expect(result.current.pagination.page).toBe(0);
    });
  });

  describe('pagination', () => {
    it('uses default page size of 25', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      expect(result.current.pagination.pageSize).toBe(25);

      await waitFor(() => expect(result.current.loading).toBe(false));

      const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>)
        .mock.calls[0];
      const body = JSON.parse(fetchOptions.body);
      expect(body.query).toContain('limit: 25');
      expect(body.query).toContain('offset: 0');
    });

    it('uses configured page size', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            pagination: { pageSize: 10 },
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.pagination.pageSize).toBe(10);

      await waitFor(() => expect(result.current.loading).toBe(false));

      const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>)
        .mock.calls[0];
      const body = JSON.parse(fetchOptions.body);
      expect(body.query).toContain('limit: 10');
    });

    it('setPage updates offset in query', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            pagination: { pageSize: 10 },
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.pagination.setPage(2);
      });
      expect(result.current.pagination.page).toBe(2);
    });

    it('setPage ignores negative values', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.pagination.setPage(-1);
      });
      expect(result.current.pagination.page).toBe(0);
    });

    it('nextPage increments page', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.pagination.nextPage();
      });
      expect(result.current.pagination.page).toBe(1);

      act(() => {
        result.current.pagination.nextPage();
      });
      expect(result.current.pagination.page).toBe(2);
    });

    it('previousPage decrements page but not below 0', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.pagination.setPage(2);
      });

      act(() => {
        result.current.pagination.previousPage();
      });
      expect(result.current.pagination.page).toBe(1);

      act(() => {
        result.current.pagination.previousPage();
      });
      expect(result.current.pagination.page).toBe(0);

      act(() => {
        result.current.pagination.previousPage();
      });
      expect(result.current.pagination.page).toBe(0);
    });

    it('setPageSize resets page to 0', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.pagination.setPage(3);
      });

      act(() => {
        result.current.pagination.setPageSize(50);
      });
      expect(result.current.pagination.pageSize).toBe(50);
      expect(result.current.pagination.page).toBe(0);
    });
  });

  describe('row selection', () => {
    it('starts with empty selection', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      expect(result.current.selection.selectedRows).toEqual([]);
    });

    it('toggleRow adds a row to selection', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      const row = { id: 1, name: 'Alice', email: 'alice@test.com' };

      act(() => {
        result.current.selection.toggleRow(row);
      });
      expect(result.current.selection.selectedRows).toEqual([row]);
    });

    it('toggleRow removes a previously selected row', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      const row = { id: 1, name: 'Alice', email: 'alice@test.com' };

      act(() => {
        result.current.selection.toggleRow(row);
      });
      expect(result.current.selection.selectedRows).toHaveLength(1);

      act(() => {
        result.current.selection.toggleRow(row);
      });
      expect(result.current.selection.selectedRows).toEqual([]);
    });

    it('selectAll sets all provided rows as selected', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      const rows = [
        { id: 1, name: 'Alice', email: 'alice@test.com' },
        { id: 2, name: 'Bob', email: 'bob@test.com' },
      ];

      act(() => {
        result.current.selection.selectAll(rows);
      });
      expect(result.current.selection.selectedRows).toEqual(rows);
    });

    it('clearSelection removes all selected rows', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      const rows = [
        { id: 1, name: 'Alice', email: 'alice@test.com' },
        { id: 2, name: 'Bob', email: 'bob@test.com' },
      ];

      act(() => {
        result.current.selection.selectAll(rows);
      });
      expect(result.current.selection.selectedRows).toHaveLength(2);

      act(() => {
        result.current.selection.clearSelection();
      });
      expect(result.current.selection.selectedRows).toEqual([]);
    });

    it('uses custom rowKey for identity comparison', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            rowKey: 'email',
          }),
        { wrapper: createWrapper() },
      );

      const row1 = { id: 1, name: 'Alice', email: 'alice@test.com' };
      const row1Copy = {
        id: 99,
        name: 'Alice Updated',
        email: 'alice@test.com',
      };

      act(() => {
        result.current.selection.toggleRow(row1);
      });
      expect(result.current.selection.selectedRows).toHaveLength(1);

      act(() => {
        result.current.selection.toggleRow(row1Copy);
      });
      expect(result.current.selection.selectedRows).toEqual([]);
    });
  });

  describe('integration', () => {
    it('combines sorting, filtering, and pagination in query', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'asc' }],
            defaultFilters: { name: { _contains: 'a' } },
            pagination: { pageSize: 10 },
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>)
        .mock.calls[0];
      const body = JSON.parse(fetchOptions.body);
      expect(body.query).toContain('filter:');
      expect(body.query).toContain('sort:');
      expect(body.query).toContain('limit: 10');
      expect(body.query).toContain('offset: 0');
    });
  });

  describe('URL sync', () => {
    let replaceStateSpy: ReturnType<typeof vi.spyOn>;

    beforeEach(() => {
      Object.defineProperty(window, 'location', {
        writable: true,
        configurable: true,
        value: { href: 'http://localhost/users', search: '' },
      });
      replaceStateSpy = vi
        .spyOn(window.history, 'replaceState')
        .mockImplementation(() => {});
    });

    afterEach(() => {
      replaceStateSpy.mockRestore();
    });

    it('syncs state to URL when urlSync is enabled (default)', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.toggleSort('name');
      });

      await waitFor(() => {
        expect(replaceStateSpy).toHaveBeenCalled();
      });

      const lastCall =
        replaceStateSpy.mock.calls[replaceStateSpy.mock.calls.length - 1];
      const url = lastCall[2] as string;
      expect(url).toContain('table_sort=');
    });

    it('does not sync to URL when urlSync is false', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            urlSync: false,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.toggleSort('name');
      });

      // Give time for any debounced calls
      await new Promise((r) => setTimeout(r, 600));

      expect(replaceStateSpy).not.toHaveBeenCalled();
    });

    it('reads initial state from URL on mount', async () => {
      Object.defineProperty(window, 'location', {
        writable: true,
        configurable: true,
        value: {
          href: 'http://localhost/users?table_sort=name:desc&table_page=2&table_size=50',
          search: '?table_sort=name:desc&table_page=2&table_size=50',
        },
      });

      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'desc' },
      ]);
      expect(result.current.pagination.page).toBe(2);
      expect(result.current.pagination.pageSize).toBe(50);
    });

    it('URL state overrides defaults on mount', async () => {
      Object.defineProperty(window, 'location', {
        writable: true,
        configurable: true,
        value: {
          href: 'http://localhost/users?table_sort=email:asc',
          search: '?table_sort=email:asc',
        },
      });

      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'desc' }],
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.sorting.current).toEqual([
        { field: 'email', direction: 'asc' },
      ]);
    });

    it('uses custom prefix for URL params', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const urlSyncConfig: UrlSyncConfig = {
        enabled: true,
        prefix: 'grid',
      };

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            urlSync: urlSyncConfig,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.pagination.setPage(3);
      });

      await waitFor(() => {
        expect(replaceStateSpy).toHaveBeenCalled();
      });

      const lastCall =
        replaceStateSpy.mock.calls[replaceStateSpy.mock.calls.length - 1];
      const url = lastCall[2] as string;
      expect(url).toContain('grid_page=');
      expect(url).not.toContain('table_page=');
    });

    it('debounces URL updates', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const urlSyncConfig: UrlSyncConfig = {
        enabled: true,
        debounceMs: 100,
      };

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            urlSync: urlSyncConfig,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      replaceStateSpy.mockClear();

      act(() => {
        result.current.pagination.setPage(1);
      });
      act(() => {
        result.current.pagination.setPage(2);
      });
      act(() => {
        result.current.pagination.setPage(3);
      });

      // Before debounce fires, no calls should have been made
      expect(replaceStateSpy).not.toHaveBeenCalled();

      await waitFor(
        () => {
          expect(replaceStateSpy).toHaveBeenCalledTimes(1);
        },
        { timeout: 500 },
      );

      const url = replaceStateSpy.mock.calls[0][2] as string;
      expect(url).toContain('table_page=3');
    });

    it('responds to popstate events (back/forward)', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.pagination.setPage(5);
      });
      expect(result.current.pagination.page).toBe(5);

      // Simulate back button by changing URL and firing popstate
      Object.defineProperty(window, 'location', {
        writable: true,
        configurable: true,
        value: {
          href: 'http://localhost/users?table_page=2',
          search: '?table_page=2',
        },
      });

      act(() => {
        window.dispatchEvent(new PopStateEvent('popstate'));
      });

      expect(result.current.pagination.page).toBe(2);
    });

    it('restores defaults on popstate when URL has no params', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.setSorting([
          { field: 'email', direction: 'desc' },
        ]);
      });
      expect(result.current.sorting.current).toEqual([
        { field: 'email', direction: 'desc' },
      ]);

      // Simulate navigating to URL without sort params
      Object.defineProperty(window, 'location', {
        writable: true,
        configurable: true,
        value: {
          href: 'http://localhost/users',
          search: '',
        },
      });

      act(() => {
        window.dispatchEvent(new PopStateEvent('popstate'));
      });

      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'asc' },
      ]);
    });

    it('does not read URL on mount when urlSync is false', async () => {
      Object.defineProperty(window, 'location', {
        writable: true,
        configurable: true,
        value: {
          href: 'http://localhost/users?table_page=5&table_sort=name:desc',
          search: '?table_page=5&table_sort=name:desc',
        },
      });

      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            urlSync: false,
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.pagination.page).toBe(0);
      expect(result.current.sorting.current).toEqual([]);
    });

    it('reads filter from URL on mount', async () => {
      const filter = encodeURIComponent('{"name":{"_contains":"alice"}}');
      Object.defineProperty(window, 'location', {
        writable: true,
        configurable: true,
        value: {
          href: `http://localhost/users?table_filter=${filter}`,
          search: `?table_filter=${filter}`,
        },
      });

      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.filters.current).toEqual({
        name: { _contains: 'alice' },
      });
    });
  });

  describe('computed columns', () => {
    it('adds computed values to row data', async () => {
      const mockUsers = [
        { id: 1, first_name: 'Alice', last_name: 'Smith' },
        { id: 2, first_name: 'Bob', last_name: 'Jones' },
      ];
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const columns: ColumnConfig[] = [
        { field: 'id', header: 'ID' },
        { field: 'first_name', header: 'First' },
        { field: 'last_name', header: 'Last' },
        {
          field: 'full_name',
          header: 'Full Name',
          computed: (row) => `${row.first_name} ${row.last_name}`,
        },
      ];

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.data).toEqual([
        {
          id: 1,
          first_name: 'Alice',
          last_name: 'Smith',
          full_name: 'Alice Smith',
        },
        {
          id: 2,
          first_name: 'Bob',
          last_name: 'Jones',
          full_name: 'Bob Jones',
        },
      ]);
    });

    it('excludes computed columns from query fields', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const columns: ColumnConfig[] = [
        { field: 'id', header: 'ID' },
        { field: 'first_name', header: 'First' },
        {
          field: 'display',
          header: 'Display',
          computed: (row) => row.first_name,
        },
      ];

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>)
        .mock.calls[0];
      const body = JSON.parse(fetchOptions.body);
      expect(body.query).toContain('id');
      expect(body.query).toContain('first_name');
      expect(body.query).not.toContain('display');
    });

    it('returns raw data when no computed columns exist', async () => {
      const mockUsers = [{ id: 1, name: 'Alice' }];
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.data).toEqual(mockUsers);
    });
  });

  describe('aggregates', () => {
    const mockOrders = [
      { id: 1, amount: 100, quantity: 2 },
      { id: 2, amount: 200, quantity: 3 },
      { id: 3, amount: 150, quantity: 1 },
    ];

    const orderColumns: ColumnConfig[] = [
      { field: 'id', header: 'ID' },
      { field: 'amount', header: 'Amount' },
      { field: 'quantity', header: 'Qty' },
    ];

    it('computes sum aggregate', async () => {
      globalThis.fetch = createFetchMock({ data: { orders: mockOrders } });

      const aggregates: Record<string, AggregateConfig> = {
        total: { field: 'amount', fn: 'sum' },
      };

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'orders',
            columns: orderColumns,
            aggregates,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.aggregates.total).toBe(450);
    });

    it('computes avg aggregate', async () => {
      globalThis.fetch = createFetchMock({ data: { orders: mockOrders } });

      const aggregates: Record<string, AggregateConfig> = {
        average: { field: 'amount', fn: 'avg' },
      };

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'orders',
            columns: orderColumns,
            aggregates,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.aggregates.average).toBe(150);
    });

    it('computes min and max aggregates', async () => {
      globalThis.fetch = createFetchMock({ data: { orders: mockOrders } });

      const aggregates: Record<string, AggregateConfig> = {
        lowest: { field: 'amount', fn: 'min' },
        highest: { field: 'amount', fn: 'max' },
      };

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'orders',
            columns: orderColumns,
            aggregates,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.aggregates.lowest).toBe(100);
      expect(result.current.aggregates.highest).toBe(200);
    });

    it('computes count aggregate', async () => {
      globalThis.fetch = createFetchMock({ data: { orders: mockOrders } });

      const aggregates: Record<string, AggregateConfig> = {
        count: { fn: 'count' },
      };

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'orders',
            columns: orderColumns,
            aggregates,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.aggregates.count).toBe(3);
    });

    it('supports custom aggregate functions', async () => {
      globalThis.fetch = createFetchMock({ data: { orders: mockOrders } });

      const aggregates: Record<string, AggregateConfig> = {
        product: {
          field: 'quantity',
          fn: (values) => (values as number[]).reduce((a, b) => a * b, 1),
        },
      };

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'orders',
            columns: orderColumns,
            aggregates,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.aggregates.product).toBe(6);
    });

    it('returns empty object when no aggregates configured', async () => {
      globalThis.fetch = createFetchMock({ data: { orders: mockOrders } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'orders', columns: orderColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.aggregates).toEqual({});
    });

    it('returns 0 for aggregates on empty data', async () => {
      globalThis.fetch = createFetchMock({ data: { orders: [] } });

      const aggregates: Record<string, AggregateConfig> = {
        total: { field: 'amount', fn: 'sum' },
        count: { fn: 'count' },
      };

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'orders',
            columns: orderColumns,
            aggregates,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.aggregates.total).toBe(0);
      expect(result.current.aggregates.count).toBe(0);
    });
  });

  describe('expansion state', () => {
    it('starts with no rows expanded', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            expandable: true,
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.expansion.expandedRows.size).toBe(0);
    });

    it('toggleExpand adds a row id', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            expandable: true,
          }),
        { wrapper: createWrapper() },
      );

      act(() => {
        result.current.expansion.toggleExpand('1');
      });
      expect(result.current.expansion.expandedRows.has('1')).toBe(true);
      expect(result.current.expansion.expandedRows.size).toBe(1);
    });

    it('toggleExpand removes a previously expanded row', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            expandable: true,
          }),
        { wrapper: createWrapper() },
      );

      act(() => {
        result.current.expansion.toggleExpand('1');
      });
      expect(result.current.expansion.expandedRows.has('1')).toBe(true);

      act(() => {
        result.current.expansion.toggleExpand('1');
      });
      expect(result.current.expansion.expandedRows.has('1')).toBe(false);
      expect(result.current.expansion.expandedRows.size).toBe(0);
    });

    it('expandAll sets all provided row ids', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            expandable: true,
          }),
        { wrapper: createWrapper() },
      );

      act(() => {
        result.current.expansion.expandAll(['1', '2', '3']);
      });
      expect(result.current.expansion.expandedRows.size).toBe(3);
      expect(result.current.expansion.expandedRows.has('1')).toBe(true);
      expect(result.current.expansion.expandedRows.has('2')).toBe(true);
      expect(result.current.expansion.expandedRows.has('3')).toBe(true);
    });

    it('collapseAll clears all expanded rows', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            expandable: true,
          }),
        { wrapper: createWrapper() },
      );

      act(() => {
        result.current.expansion.expandAll(['1', '2']);
      });
      expect(result.current.expansion.expandedRows.size).toBe(2);

      act(() => {
        result.current.expansion.collapseAll();
      });
      expect(result.current.expansion.expandedRows.size).toBe(0);
    });

    it('toggleExpand does nothing when expandable is false', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            expandable: false,
          }),
        { wrapper: createWrapper() },
      );

      act(() => {
        result.current.expansion.toggleExpand('1');
      });
      expect(result.current.expansion.expandedRows.size).toBe(0);
    });
  });

  describe('column management', () => {
    it('initializes visibleColumns from column config', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      expect(result.current.columnManagement.visibleColumns).toEqual([
        'id',
        'name',
        'email',
      ]);
    });

    it('toggleColumn hides a visible column', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      act(() => {
        result.current.columnManagement.toggleColumn('email');
      });
      expect(result.current.columnManagement.visibleColumns).toEqual([
        'id',
        'name',
      ]);
    });

    it('toggleColumn shows a hidden column', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      act(() => {
        result.current.columnManagement.toggleColumn('email');
      });
      expect(result.current.columnManagement.visibleColumns).not.toContain(
        'email',
      );

      act(() => {
        result.current.columnManagement.toggleColumn('email');
      });
      expect(result.current.columnManagement.visibleColumns).toContain('email');
    });

    it('initializes columnOrder from column config', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      expect(result.current.columnManagement.columnOrder).toEqual([
        'id',
        'name',
        'email',
      ]);
    });

    it('reorderColumn moves a column to a new position', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      act(() => {
        result.current.columnManagement.reorderColumn(0, 2);
      });
      expect(result.current.columnManagement.columnOrder).toEqual([
        'name',
        'email',
        'id',
      ]);
    });

    it('reorderColumn ignores out-of-bounds indices', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      act(() => {
        result.current.columnManagement.reorderColumn(-1, 2);
      });
      expect(result.current.columnManagement.columnOrder).toEqual([
        'id',
        'name',
        'email',
      ]);

      act(() => {
        result.current.columnManagement.reorderColumn(0, 5);
      });
      expect(result.current.columnManagement.columnOrder).toEqual([
        'id',
        'name',
        'email',
      ]);
    });
  });

  describe('multi-column sort helpers', () => {
    it('toggleSort with multi=true appends sort even when multiSort option is false', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            multiSort: false,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.toggleSort('name');
      });
      act(() => {
        result.current.sorting.toggleSort('email', true);
      });
      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'asc' },
        { field: 'email', direction: 'asc' },
      ]);
    });

    it('toggleSort with multi=false replaces sort even when multiSort option is true', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            multiSort: true,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.toggleSort('name');
      });
      act(() => {
        result.current.sorting.toggleSort('email', false);
      });
      expect(result.current.sorting.current).toEqual([
        { field: 'email', direction: 'asc' },
      ]);
    });

    it('addSort appends a new field with specified direction', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.addSort('name', 'desc');
      });
      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'desc' },
      ]);
    });

    it('addSort replaces direction if field already sorted', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.addSort('name', 'desc');
      });
      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'desc' },
      ]);
    });

    it('addSort ignores non-sortable columns', async () => {
      const columns: ColumnConfig[] = [
        { field: 'id', header: 'ID', sortable: false },
        { field: 'name', header: 'Name', sortable: true },
      ];
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.addSort('id', 'asc');
      });
      expect(result.current.sorting.current).toEqual([]);
    });

    it('addSort resets page to 0', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.pagination.setPage(3);
      });
      expect(result.current.pagination.page).toBe(3);

      act(() => {
        result.current.sorting.addSort('name', 'asc');
      });
      expect(result.current.pagination.page).toBe(0);
    });

    it('removeSort removes a specific field from sort', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [
              { field: 'name', direction: 'asc' },
              { field: 'email', direction: 'desc' },
            ],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.removeSort('name');
      });
      expect(result.current.sorting.current).toEqual([
        { field: 'email', direction: 'desc' },
      ]);
    });

    it('removeSort does nothing for non-existent field', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.removeSort('nonexistent');
      });
      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'asc' },
      ]);
    });

    it('removeSort resets page to 0', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.pagination.setPage(5);
      });

      act(() => {
        result.current.sorting.removeSort('name');
      });
      expect(result.current.pagination.page).toBe(0);
    });

    it('clearSort removes all sorts', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [
              { field: 'name', direction: 'asc' },
              { field: 'email', direction: 'desc' },
            ],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.clearSort();
      });
      expect(result.current.sorting.current).toEqual([]);
    });

    it('clearSort resets page to 0', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.pagination.setPage(2);
      });

      act(() => {
        result.current.sorting.clearSort();
      });
      expect(result.current.pagination.page).toBe(0);
    });
  });

  describe('sort indicators and priority', () => {
    it('getSortIndicator returns up arrow for asc', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.sorting.getSortIndicator('name')).toBe('\u25B2');
    });

    it('getSortIndicator returns down arrow for desc', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'desc' }],
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.sorting.getSortIndicator('name')).toBe('\u25BC');
    });

    it('getSortIndicator returns empty string for unsorted field', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      expect(result.current.sorting.getSortIndicator('name')).toBe('');
    });

    it('getSortPriority returns 1-based index for sorted fields', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [
              { field: 'name', direction: 'asc' },
              { field: 'email', direction: 'desc' },
            ],
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.sorting.getSortPriority('name')).toBe(1);
      expect(result.current.sorting.getSortPriority('email')).toBe(2);
    });

    it('getSortPriority returns -1 for unsorted fields', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.sorting.getSortPriority('email')).toBe(-1);
      expect(result.current.sorting.getSortPriority('id')).toBe(-1);
    });

    it('indicators update when sort changes', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () => useBifrostTable({ query: 'users', columns: defaultColumns }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.sorting.getSortIndicator('name')).toBe('');
      expect(result.current.sorting.getSortPriority('name')).toBe(-1);

      act(() => {
        result.current.sorting.toggleSort('name');
      });

      expect(result.current.sorting.getSortIndicator('name')).toBe('\u25B2');
      expect(result.current.sorting.getSortPriority('name')).toBe(1);

      act(() => {
        result.current.sorting.toggleSort('name');
      });

      expect(result.current.sorting.getSortIndicator('name')).toBe('\u25BC');
      expect(result.current.sorting.getSortPriority('name')).toBe(1);
    });
  });

  describe('client-side sorting', () => {
    const mockUsers = [
      { id: 3, name: 'Charlie', email: 'charlie@test.com' },
      { id: 1, name: 'Alice', email: 'alice@test.com' },
      { id: 2, name: 'Bob', email: 'bob@test.com' },
    ];

    it('sorts data client-side when clientSideSort is true', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            clientSideSort: true,
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.data.map((r: Record<string, unknown>) => r.name)).toEqual([
        'Alice',
        'Bob',
        'Charlie',
      ]);
    });

    it('does not send sort to server when clientSideSort is enabled', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            clientSideSort: true,
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>)
        .mock.calls[0];
      const body = JSON.parse(fetchOptions.body);
      expect(body.query).not.toContain('sort:');
    });

    it('sorts descending client-side', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            clientSideSort: true,
            defaultSort: [{ field: 'name', direction: 'desc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.data.map((r: Record<string, unknown>) => r.name)).toEqual([
        'Charlie',
        'Bob',
        'Alice',
      ]);
    });

    it('supports multi-column client-side sort', async () => {
      const data = [
        { id: 1, name: 'Alice', email: 'z@test.com' },
        { id: 2, name: 'Alice', email: 'a@test.com' },
        { id: 3, name: 'Bob', email: 'm@test.com' },
      ];
      globalThis.fetch = createFetchMock({ data: { users: data } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            clientSideSort: true,
            multiSort: true,
            defaultSort: [
              { field: 'name', direction: 'asc' },
              { field: 'email', direction: 'asc' },
            ],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.data.map((r: Record<string, unknown>) => r.email)).toEqual([
        'a@test.com',
        'z@test.com',
        'm@test.com',
      ]);
    });

    it('handles numeric sorting correctly', async () => {
      const data = [
        { id: 10, name: 'Ten', email: 'ten@test.com' },
        { id: 2, name: 'Two', email: 'two@test.com' },
        { id: 1, name: 'One', email: 'one@test.com' },
      ];
      globalThis.fetch = createFetchMock({ data: { users: data } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            clientSideSort: true,
            defaultSort: [{ field: 'id', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.data.map((r: Record<string, unknown>) => r.id)).toEqual([
        1, 2, 10,
      ]);
    });

    it('handles null values by sorting them last', async () => {
      const data = [
        { id: 1, name: null, email: 'a@test.com' },
        { id: 2, name: 'Bob', email: 'b@test.com' },
        { id: 3, name: 'Alice', email: 'c@test.com' },
      ];
      globalThis.fetch = createFetchMock({ data: { users: data } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            clientSideSort: true,
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.data.map((r: Record<string, unknown>) => r.name)).toEqual([
        'Alice',
        'Bob',
        null,
      ]);
    });

    it('respects threshold - skips client sort when data exceeds threshold', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            clientSideSort: { enabled: true, threshold: 2 },
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      // Data should NOT be sorted client-side since 3 > threshold of 2
      expect(result.current.data.map((r: Record<string, unknown>) => r.name)).toEqual([
        'Charlie',
        'Alice',
        'Bob',
      ]);
    });

    it('applies client sort when data is within threshold', async () => {
      const data = [
        { id: 2, name: 'Bob', email: 'bob@test.com' },
        { id: 1, name: 'Alice', email: 'alice@test.com' },
      ];
      globalThis.fetch = createFetchMock({ data: { users: data } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            clientSideSort: { enabled: true, threshold: 5 },
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.data.map((r: Record<string, unknown>) => r.name)).toEqual([
        'Alice',
        'Bob',
      ]);
    });

    it('returns unsorted data when no sort is active', async () => {
      globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            clientSideSort: true,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.data.map((r: Record<string, unknown>) => r.id)).toEqual([
        3, 1, 2,
      ]);
    });
  });

  describe('custom sort functions', () => {
    it('uses column customSort when provided', async () => {
      const data = [
        { id: 1, name: 'high', email: 'a@test.com' },
        { id: 2, name: 'low', email: 'b@test.com' },
        { id: 3, name: 'medium', email: 'c@test.com' },
      ];
      globalThis.fetch = createFetchMock({ data: { users: data } });

      const priorityOrder: Record<string, number> = {
        low: 0,
        medium: 1,
        high: 2,
      };
      const customSort: CustomSortFn = (a, b, direction) => {
        const aVal = priorityOrder[a as string] ?? 0;
        const bVal = priorityOrder[b as string] ?? 0;
        return direction === 'asc' ? aVal - bVal : bVal - aVal;
      };

      const columns: ColumnConfig[] = [
        { field: 'id', header: 'ID', sortable: true },
        {
          field: 'name',
          header: 'Priority',
          sortable: true,
          customSort,
        },
        { field: 'email', header: 'Email', sortable: true },
      ];

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns,
            clientSideSort: true,
            defaultSort: [{ field: 'name', direction: 'asc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.data.map((r: Record<string, unknown>) => r.name)).toEqual([
        'low',
        'medium',
        'high',
      ]);
    });

    it('custom sort respects desc direction', async () => {
      const data = [
        { id: 1, name: 'high', email: 'a@test.com' },
        { id: 2, name: 'low', email: 'b@test.com' },
        { id: 3, name: 'medium', email: 'c@test.com' },
      ];
      globalThis.fetch = createFetchMock({ data: { users: data } });

      const priorityOrder: Record<string, number> = {
        low: 0,
        medium: 1,
        high: 2,
      };
      const customSort: CustomSortFn = (a, b, direction) => {
        const aVal = priorityOrder[a as string] ?? 0;
        const bVal = priorityOrder[b as string] ?? 0;
        return direction === 'asc' ? aVal - bVal : bVal - aVal;
      };

      const columns: ColumnConfig[] = [
        { field: 'id', header: 'ID', sortable: true },
        {
          field: 'name',
          header: 'Priority',
          sortable: true,
          customSort,
        },
        { field: 'email', header: 'Email', sortable: true },
      ];

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns,
            clientSideSort: true,
            defaultSort: [{ field: 'name', direction: 'desc' }],
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      expect(result.current.data.map((r: Record<string, unknown>) => r.name)).toEqual([
        'high',
        'medium',
        'low',
      ]);
    });
  });

  describe('localStorage persistence', () => {
    let getItemSpy: ReturnType<typeof vi.fn>;
    let setItemSpy: ReturnType<typeof vi.fn>;
    let removeItemSpy: ReturnType<typeof vi.fn>;

    beforeEach(() => {
      getItemSpy = vi.fn().mockReturnValue(null);
      setItemSpy = vi.fn();
      removeItemSpy = vi.fn();
      Object.defineProperty(window, 'localStorage', {
        value: {
          getItem: getItemSpy,
          setItem: setItemSpy,
          removeItem: removeItemSpy,
        },
        writable: true,
        configurable: true,
      });
    });

    it('reads sort state from localStorage on mount', async () => {
      getItemSpy.mockReturnValue(
        JSON.stringify([{ field: 'name', direction: 'desc' }]),
      );
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            localStorage: { key: 'test-table-sort' },
            urlSync: false,
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'desc' },
      ]);
    });

    it('localStorage sort overrides defaultSort', async () => {
      getItemSpy.mockReturnValue(
        JSON.stringify([{ field: 'email', direction: 'asc' }]),
      );
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'desc' }],
            localStorage: { key: 'test-table-sort' },
            urlSync: false,
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.sorting.current).toEqual([
        { field: 'email', direction: 'asc' },
      ]);
    });

    it('URL state takes precedence over localStorage', async () => {
      getItemSpy.mockReturnValue(
        JSON.stringify([{ field: 'email', direction: 'asc' }]),
      );
      Object.defineProperty(window, 'location', {
        writable: true,
        configurable: true,
        value: {
          href: 'http://localhost/users?table_sort=id:desc',
          search: '?table_sort=id:desc',
        },
      });

      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            localStorage: { key: 'test-table-sort' },
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.sorting.current).toEqual([
        { field: 'id', direction: 'desc' },
      ]);
    });

    it('writes sort state to localStorage when sort changes', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            localStorage: { key: 'test-table-sort' },
            urlSync: false,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.toggleSort('name');
      });

      expect(setItemSpy).toHaveBeenCalledWith(
        'test-table-sort',
        JSON.stringify([{ field: 'name', direction: 'asc' }]),
      );
    });

    it('removes localStorage entry when sort is cleared', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'asc' }],
            localStorage: { key: 'test-table-sort' },
            urlSync: false,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.clearSort();
      });

      expect(removeItemSpy).toHaveBeenCalledWith('test-table-sort');
    });

    it('handles invalid JSON in localStorage gracefully', async () => {
      getItemSpy.mockReturnValue('not-json');
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'asc' }],
            localStorage: { key: 'test-table-sort' },
            urlSync: false,
          }),
        { wrapper: createWrapper() },
      );

      // Falls back to defaultSort since localStorage is invalid
      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'asc' },
      ]);
    });

    it('handles non-array JSON in localStorage gracefully', async () => {
      getItemSpy.mockReturnValue('{"not":"array"}');
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            defaultSort: [{ field: 'name', direction: 'asc' }],
            localStorage: { key: 'test-table-sort' },
            urlSync: false,
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'asc' },
      ]);
    });

    it('does not use localStorage when config is not provided', async () => {
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
          }),
        { wrapper: createWrapper() },
      );

      await waitFor(() => expect(result.current.loading).toBe(false));

      act(() => {
        result.current.sorting.toggleSort('name');
      });

      expect(setItemSpy).not.toHaveBeenCalled();
    });

    it('filters invalid sort entries from localStorage', async () => {
      getItemSpy.mockReturnValue(
        JSON.stringify([
          { field: 'name', direction: 'asc' },
          { field: 'bad', direction: 'invalid' },
          { field: 'email', direction: 'desc' },
        ]),
      );
      globalThis.fetch = createFetchMock({ data: { users: [] } });

      const { result } = renderHook(
        () =>
          useBifrostTable({
            query: 'users',
            columns: defaultColumns,
            localStorage: { key: 'test-table-sort' },
            urlSync: false,
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.sorting.current).toEqual([
        { field: 'name', direction: 'asc' },
        { field: 'email', direction: 'desc' },
      ]);
    });
  });
});
