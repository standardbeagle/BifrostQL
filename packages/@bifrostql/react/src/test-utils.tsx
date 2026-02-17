import { vi, beforeEach, afterEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from './components/bifrost-provider';

/**
 * Creates a mock fetch function that resolves with the given response.
 * Assign the return value to `globalThis.fetch` in your test.
 */
export function createFetchMock(
  response: unknown,
  ok = true,
  status = 200,
) {
  return vi.fn().mockResolvedValue({
    ok,
    status,
    statusText: ok ? 'OK' : 'Internal Server Error',
    json: () => Promise.resolve(response),
  });
}

/**
 * Creates a mock fetch that returns different responses on successive calls.
 * Each entry in `responses` is used in order; the last entry repeats for any
 * additional calls beyond the array length.
 */
export function createSequentialFetchMock(
  responses: Array<{ response: unknown; ok?: boolean; status?: number }>,
) {
  let callIndex = 0;
  return vi.fn().mockImplementation(() => {
    const entry = responses[callIndex] ?? responses[responses.length - 1];
    callIndex++;
    const ok = entry.ok ?? true;
    const status = entry.status ?? 200;
    return Promise.resolve({
      ok,
      status,
      statusText: ok ? 'OK' : 'Internal Server Error',
      json: () => Promise.resolve(entry.response),
    });
  });
}

/**
 * Creates a React wrapper component that provides both QueryClient and
 * BifrostProvider context. Use as the `wrapper` option for `renderHook`.
 */
export function createWrapper(
  endpoint = 'http://localhost:5000/graphql',
  headers?: Record<string, string>,
  queryClientOptions?: {
    mutations?: { retry: boolean };
  },
) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      ...(queryClientOptions?.mutations
        ? { mutations: queryClientOptions.mutations }
        : {}),
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

/**
 * Saves and restores `globalThis.fetch` around tests. Call in a `describe`
 * block to set up `beforeEach` / `afterEach` automatically.
 *
 * @example
 * ```ts
 * describe('my hook', () => {
 *   useFetchMock();
 *   it('works', () => { ... });
 * });
 * ```
 */
export function useFetchMock() {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });
}

/**
 * Extracts the request body from a mocked fetch call at the given index.
 */
export function getFetchBody(
  fetchMock: ReturnType<typeof vi.fn>,
  callIndex = 0,
): Record<string, unknown> {
  const [, options] = fetchMock.mock.calls[callIndex];
  return JSON.parse(options.body);
}

/**
 * Extracts the request headers from a mocked fetch call at the given index.
 */
export function getFetchHeaders(
  fetchMock: ReturnType<typeof vi.fn>,
  callIndex = 0,
): Record<string, string> {
  const [, options] = fetchMock.mock.calls[callIndex];
  return options.headers;
}
