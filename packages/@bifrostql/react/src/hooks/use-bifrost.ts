import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useContext, useCallback } from 'react';
import { BifrostContext } from '../components/bifrost-provider';
import { executeGraphQL, defaultRetryDelay } from '../utils/graphql-client';

export interface UseBifrostOptions {
  enabled?: boolean;
  staleTime?: number;
  gcTime?: number;
  refetchInterval?: number | false;
  refetchOnWindowFocus?: boolean | 'always';
  retry?: number | false;
  retryDelay?: number;
}

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
    retry = 3,
    retryDelay = 1000,
    staleTime,
    gcTime,
    refetchInterval,
    refetchOnWindowFocus,
  } = options;

  const queryClient = useQueryClient();
  const queryKey = ['bifrost', query, variables ?? {}];

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
      ),
    enabled,
    retry,
    retryDelay: (attempt) => defaultRetryDelay(attempt, retryDelay),
    staleTime,
    gcTime,
    refetchInterval,
    refetchOnWindowFocus,
  });

  return { ...result, invalidate };
}
