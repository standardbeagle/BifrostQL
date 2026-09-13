import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useContext, useCallback } from 'react';
import { BifrostContext } from '../components/bifrost-provider';
import { executeGraphQL, defaultRetryDelay } from '../utils/graphql-client';

/** Options for the {@link useBifrost} hook, forwarded to TanStack Query. */
export interface UseBifrostOptions {
  /** Whether the query should execute. Defaults to `true`. */
  enabled?: boolean;
  /** Duration in milliseconds that cached data is considered fresh. */
  staleTime?: number;
  /** Duration in milliseconds before unused cached data is garbage-collected. */
  gcTime?: number;
  /** Interval in milliseconds for automatic refetching, or `false` to disable. */
  refetchInterval?: number | false;
  /** Whether to refetch when the window regains focus. */
  refetchOnWindowFocus?: boolean | 'always';
  /**
   * Number of retry attempts, or `false` to disable. When omitted, the
   * QueryClient's own setting applies (TanStack Query's default is 3).
   */
  retry?: number | false;
  /**
   * Base delay in milliseconds between retries (exponential backoff). When
   * omitted, the QueryClient's own setting applies.
   */
  retryDelay?: number;
  /**
   * Extra query-key element for session-sensitive caches (e.g. the caller's
   * identity). Appended only when supplied, so an unsuffixed key stays the
   * three-element key `fetchBifrostQuery` prefetches under for SSR hydration.
   */
  queryKeySuffix?: string;
}

/**
 * Low-level hook for executing raw GraphQL queries against a BifrostQL endpoint.
 *
 * All other query hooks (`useBifrostQuery`, `useBifrostInfinite`, etc.) build
 * on this hook. Returns standard TanStack Query result fields plus an
 * `invalidate` function for manually invalidating the query cache.
 *
 * Must be used within a {@link BifrostProvider}.
 *
 * @typeParam T - The expected response data type.
 * @param query - A raw GraphQL query string.
 * @param variables - Optional GraphQL variables.
 * @param options - TanStack Query options (enabled, staleTime, retry, etc.).
 * @returns TanStack Query result with an additional `invalidate` method.
 *
 * @example
 * ```tsx
 * const { data, isLoading, error, invalidate } = useBifrost<{ users: User[] }>(
 *   '{ users { id name } }',
 *   undefined,
 *   { staleTime: 5000 },
 * );
 * ```
 */
export function useBifrost<T = unknown>(
  query: string,
  variables?: Record<string, unknown>,
  options: UseBifrostOptions = {},
) {
  const config = useContext(BifrostContext);
  if (!config) {
    throw new Error('useBifrost must be used within a BifrostProvider');
  }

  const {
    enabled = true,
    retry,
    retryDelay,
    staleTime,
    gcTime,
    refetchInterval,
    refetchOnWindowFocus,
    queryKeySuffix,
  } = options;

  const queryClient = useQueryClient();
  const queryKey =
    queryKeySuffix === undefined
      ? ['bifrost', query, variables ?? {}]
      : ['bifrost', query, variables ?? {}, queryKeySuffix];

  const invalidate = useCallback(
    () => queryClient.invalidateQueries({ queryKey: ['bifrost', query] }),
    [queryClient, query],
  );

  const result = useQuery<T>({
    queryKey,
    queryFn: ({ signal }) =>
      executeGraphQL<T>(
        config.endpoint,
        config.headers ?? {},
        query,
        variables,
        signal,
        config.getToken,
        {
          refreshToken: config.refreshToken,
          onSessionExpired: config.onSessionExpired,
        },
      ),
    enabled,
    // Only forward these when the caller set them. Passing a value
    // unconditionally — even `undefined` — overrides whatever the host
    // configured on its QueryClient: `retry: false`, which a caller cannot
    // otherwise turn off, and the `staleTime` that keeps server-prefetched
    // data from refetching on hydration.
    ...(retry !== undefined ? { retry } : {}),
    ...(retryDelay !== undefined
      ? {
          retryDelay: (attempt: number) =>
            defaultRetryDelay(attempt, retryDelay),
        }
      : {}),
    ...(staleTime !== undefined ? { staleTime } : {}),
    ...(gcTime !== undefined ? { gcTime } : {}),
    ...(refetchInterval !== undefined ? { refetchInterval } : {}),
    ...(refetchOnWindowFocus !== undefined ? { refetchOnWindowFocus } : {}),
  });

  return { ...result, invalidate };
}
