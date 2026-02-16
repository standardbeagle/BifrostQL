import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import type { InfiniteData, QueryKey } from '@tanstack/react-query';
import { useContext, useCallback } from 'react';
import { BifrostContext } from '../components/bifrost-provider';
import { executeGraphQL, defaultRetryDelay } from '../utils/graphql-client';

export interface UseBifrostInfiniteOptions<TData, TPageParam> {
  enabled?: boolean;
  staleTime?: number;
  gcTime?: number;
  refetchOnWindowFocus?: boolean | 'always';
  retry?: number | false;
  retryDelay?: number;
  initialPageParam: TPageParam;
  getNextPageParam: (
    lastPage: TData,
    allPages: TData[],
  ) => TPageParam | undefined | null;
  getPreviousPageParam?: (
    firstPage: TData,
    allPages: TData[],
  ) => TPageParam | undefined | null;
  maxPages?: number;
}

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
  const queryKey: QueryKey = ['bifrost', 'infinite', query];

  const invalidate = useCallback(
    () =>
      queryClient.invalidateQueries({
        queryKey: ['bifrost', 'infinite', query],
      }),
    [queryClient, query],
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
