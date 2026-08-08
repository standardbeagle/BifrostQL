import { describe, it, expect, vi, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
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
