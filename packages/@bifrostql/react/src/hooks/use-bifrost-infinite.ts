import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import type { InfiniteData, QueryKey } from '@tanstack/react-query';
import { useContext, useCallback } from 'react';
import { BifrostContext } from '../components/bifrost-provider';
import { executeGraphQL, defaultRetryDelay } from '../utils/graphql-client';

/**
 * Options for the {@link useBifrostInfinite} hook.
 *
 * @typeParam TData - The data type returned by each page.
 * @typeParam TPageParam - The page parameter type (e.g. `number` for offset).
 */
export interface UseBifrostInfiniteOptions<TData, TPageParam> {
  /** Whether the query should execute. Defaults to `true`. */
  enabled?: boolean;
  /** Duration in milliseconds that cached data is considered fresh. */
  staleTime?: number;
  /** Duration in milliseconds before unused cached data is garbage-collected. */
  gcTime?: number;
  /** Whether to refetch when the window regains focus. */
  refetchOnWindowFocus?: boolean | 'always';
  /** Number of retry attempts, or `false` to disable. Defaults to `3`. */
  retry?: number | false;
  /** Base delay in milliseconds between retries. Defaults to `1000`. */
  retryDelay?: number;
  /** The page parameter for the first page. */
  initialPageParam: TPageParam;
  /**
   * Extract the next page parameter from the last fetched page.
   * Return `undefined` or `null` to indicate there are no more pages.
   */
  getNextPageParam: (
    lastPage: TData,
    allPages: TData[],
  ) => TPageParam | undefined | null;
  /**
   * Extract the previous page parameter from the first fetched page.
   * Return `undefined` or `null` to indicate there are no previous pages.
   */
  getPreviousPageParam?: (
    firstPage: TData,
    allPages: TData[],
  ) => TPageParam | undefined | null;
  /** Maximum number of pages to keep in the cache. */
  maxPages?: number;
}

/**
 * Hook for infinite scrolling with cursor or offset-based pagination.
 *
 * Wraps TanStack Query's `useInfiniteQuery` with BifrostQL endpoint
 * configuration. The `buildVariables` function translates page parameters
 * into GraphQL variables for each page fetch.
 *
 * Must be used within a {@link BifrostProvider}.
 *
 * @typeParam TData - The data type returned by each page.
 * @typeParam TPageParam - The page parameter type.
 * @param query - A GraphQL query string with pagination variables.
 * @param buildVariables - Function that converts a page param into query variables.
 * @param options - Infinite query configuration.
 * @returns TanStack infinite query result with an additional `invalidate` method.
 *
 * @example
 * ```tsx
 * const { data, fetchNextPage, hasNextPage } = useBifrostInfinite<
 *   { users: User[] },
 *   number
 * >(
 *   '{ users(limit: $limit, offset: $offset) { id name } }',
 *   (pageParam) => ({ limit: 20, offset: pageParam }),
 *   {
 *     initialPageParam: 0,
 *     getNextPageParam: (lastPage, allPages) =>
 *       lastPage.users.length < 20 ? undefined : allPages.length * 20,
 *   },
 * );
 * ```
 */
export function useBifrostInfinite<TData = unknown, TPageParam = unknown>(
  query: string,
  buildVariables: (pageParam: TPageParam) => Record<string, unknown>,
  options: UseBifrostInfiniteOptions<TData, TPageParam>,
) {
  const config = useContext(BifrostContext);
  if (!config) {
    throw new Error('useBifrostInfinite must be used within a BifrostProvider');
  }

  const {
    enabled = true,
    retry = 3,
    retryDelay = 1000,
    staleTime,
    gcTime,
    refetchOnWindowFocus,
    initialPageParam,
    getNextPageParam,
    getPreviousPageParam,
    maxPages,
  } = options;

  const queryClient = useQueryClient();
  // The cache key must include the endpoint and the query's variables identity
  // (seeded here by `initialPageParam`) alongside the query text. Keying on the
  // query string alone meant two providers pointed at different endpoints — or
  // two components paging the same query from different starting params —
  // collided in the cache and served each other's pages. React Query hashes the
  // key structurally, so the raw `initialPageParam` value participates directly.
  const queryKey: QueryKey = [
    'bifrost',
    'infinite',
    config.endpoint,
    query,
    initialPageParam,
  ];

  const invalidate = useCallback(
    () =>
      queryClient.invalidateQueries({
        queryKey: ['bifrost', 'infinite', config.endpoint, query, initialPageParam],
      }),
    [queryClient, config.endpoint, query, initialPageParam],
  );

  const result = useInfiniteQuery<
    TData,
    Error,
    InfiniteData<TData, TPageParam>,
    QueryKey,
    TPageParam
  >({
    queryKey,
    queryFn: ({ pageParam, signal }) => {
      const variables = buildVariables(pageParam as TPageParam);
      return executeGraphQL<TData>(
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
      );
    },
    enabled,
    retry,
    retryDelay: (attempt) => defaultRetryDelay(attempt, retryDelay),
    staleTime,
    gcTime,
    refetchOnWindowFocus,
    initialPageParam,
    getNextPageParam,
    getPreviousPageParam,
    maxPages,
  });

  return { ...result, invalidate };
}
