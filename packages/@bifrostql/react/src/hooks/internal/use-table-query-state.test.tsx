import { describe, it, expect, vi, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '../../components/bifrost-provider';
import { useBifrostTable } from '../use-bifrost-table';
import type { ColumnConfig } from '../use-bifrost-table.types';

const columns: ColumnConfig[] = [
  { field: 'id', header: 'ID' },
  { field: 'name', header: 'Name', filterable: true },
];

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
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

function mockUsers(rows: unknown[]) {
  globalThis.fetch = vi.fn().mockResolvedValue({
    ok: true,
    status: 200,
    statusText: 'OK',
    json: () => Promise.resolve({ data: { users: rows } }),
  }) as unknown as typeof fetch;
}

describe('useTableQueryState default-option stability', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('does not re-register the popstate listener on every render', async () => {
    // Arrange: no defaultSort / defaultFilter supplied, so the hook's own
    // default parameters are what feed the popstate effect's deps.
    mockUsers([{ id: 1, name: 'Alice' }]);
    const addSpy = vi.spyOn(window, 'addEventListener');

    const { result, rerender } = renderHook(
      () => useBifrostTable({ table: 'users', columns, urlSync: true }),
      { wrapper: createWrapper() },
    );
    await waitFor(() => expect(result.current.loading).toBe(false));

    const countPopstateRegistrations = () =>
      addSpy.mock.calls.filter(([event]) => event === 'popstate').length;
    const before = countPopstateRegistrations();
    expect(before).toBeGreaterThan(0);

    // Act: a render that changes nothing.
    rerender();
    rerender();
    rerender();

    // Assert: fresh `[]` / `{}` defaults each render would tear down and
    // re-add the listener on every one of them.
    expect(countPopstateRegistrations()).toBe(before);
  });
});

describe('useTableQueryState controlled filter', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('applies the controlled filter on mount', async () => {
    // Arrange
    mockUsers([{ id: 1, name: 'Alice' }]);

    // Act
    const { result } = renderHook(
      () =>
        useBifrostTable({
          table: 'users',
          columns,
          urlSync: false,
          filter: { name: { _eq: 'Alice' } },
        }),
      { wrapper: createWrapper() },
    );
    await waitFor(() => expect(result.current.loading).toBe(false));

    // Assert
    expect(result.current.filters.current).toEqual({
      name: { _eq: 'Alice' },
    });
  });

  it('stays in sync when the controlled filter changes', async () => {
    // Arrange: the failure mode is a screen switching data sets (a saved view,
    // an audience segment) and silently keeping the previous filter.
    mockUsers([{ id: 1, name: 'Alice' }]);

    const { result, rerender } = renderHook(
      ({ name }: { name: string }) =>
        useBifrostTable({
          table: 'users',
          columns,
          urlSync: false,
          filter: { name: { _eq: name } },
        }),
      { wrapper: createWrapper(), initialProps: { name: 'Alice' } },
    );
    await waitFor(() => expect(result.current.loading).toBe(false));

    // Act
    rerender({ name: 'Bob' });

    // Assert: the table's own filter state follows the prop, without a remount.
    await waitFor(() =>
      expect(result.current.filters.current).toEqual({
        name: { _eq: 'Bob' },
      }),
    );
  });

  it('returns to the first page when the controlled filter changes', async () => {
    // Arrange
    mockUsers([{ id: 1, name: 'Alice' }]);

    const { result, rerender } = renderHook(
      ({ name }: { name: string }) =>
        useBifrostTable({
          table: 'users',
          columns,
          urlSync: false,
          filter: { name: { _eq: name } },
        }),
      { wrapper: createWrapper(), initialProps: { name: 'Alice' } },
    );
    await waitFor(() => expect(result.current.loading).toBe(false));
    act(() => {
      result.current.pagination.setPage(3);
    });
    expect(result.current.pagination.page).toBe(3);

    // Act
    rerender({ name: 'Bob' });

    // Assert: page 4 of the old result set is meaningless for the new filter.
    await waitFor(() => expect(result.current.pagination.page).toBe(0));
  });

  it('keeps defaultFilter as a mount-only seed the user can edit away', async () => {
    // Arrange
    mockUsers([{ id: 1, name: 'Alice' }]);

    const { result, rerender } = renderHook(
      ({ name }: { name: string }) =>
        useBifrostTable({
          table: 'users',
          columns,
          urlSync: false,
          defaultFilter: { name: { _eq: name } },
        }),
      { wrapper: createWrapper(), initialProps: { name: 'Alice' } },
    );
    await waitFor(() => expect(result.current.loading).toBe(false));

    // Act
    rerender({ name: 'Bob' });

    // Assert: uncontrolled semantics are unchanged — the seed does not
    // reach in and overwrite state after mount.
    expect(result.current.filters.current).toEqual({
      name: { _eq: 'Alice' },
    });
  });

  it('lets local column filters layer on top of the controlled filter', async () => {
    // Arrange
    mockUsers([{ id: 1, name: 'Alice' }]);
    const controlled = { name: { _eq: 'Alice' } };

    const { result } = renderHook(
      () =>
        useBifrostTable({
          table: 'users',
          columns,
          urlSync: false,
          filter: controlled,
        }),
      { wrapper: createWrapper() },
    );
    await waitFor(() => expect(result.current.loading).toBe(false));

    // Act
    act(() => {
      result.current.filters.setColumnFilter('id', { _eq: 5 });
    });

    // Assert: a re-render with an unchanged controlled value must not stomp
    // the user's in-table edit.
    expect(result.current.filters.current).toEqual({
      name: { _eq: 'Alice' },
      id: { _eq: 5 },
    });
  });
});
