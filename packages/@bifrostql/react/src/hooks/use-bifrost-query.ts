import { buildGraphqlQuery } from '../utils/query-builder';
import { useBifrost } from './use-bifrost';
import type { UseBifrostOptions } from './use-bifrost';
import type { QueryOptions } from '../types';

interface UseBifrostQueryOptions extends QueryOptions, UseBifrostOptions {}

export function useBifrostQuery<T = unknown>(
  table: string,
  options: UseBifrostQueryOptions = {},
) {
  const {
    enabled,
    retry,
    retryDelay,
    staleTime,
    gcTime,
    refetchInterval,
    refetchOnWindowFocus,
    ...queryOptions
  } = options;

  const query = buildGraphqlQuery(table, queryOptions);

  const result = useBifrost<{ [key: string]: T }>(query, undefined, {
    enabled,
    retry,
    retryDelay,
    staleTime,
    gcTime,
    refetchInterval,
    refetchOnWindowFocus,
  });

  const data = result.data?.[table] as T | undefined;

  return {
    ...result,
    data,
  };
}
