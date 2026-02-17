import { buildGraphqlQuery } from '../utils/query-builder';
import { useBifrost } from './use-bifrost';
import type { UseBifrostOptions } from './use-bifrost';
import type { QueryOptions } from '../types';

interface UseBifrostQueryOptions extends QueryOptions, UseBifrostOptions {}

/**
 * Table-oriented query hook with declarative filter, sort, pagination, and
 * field-selection support.
 *
 * Builds a GraphQL query from the provided options using {@link buildGraphqlQuery}
 * and executes it via {@link useBifrost}. The returned `data` is automatically
 * unwrapped from the table-keyed response.
 *
 * Must be used within a {@link BifrostProvider}.
 *
 * @typeParam T - The expected row data type.
 * @param table - The database table name to query.
 * @param options - Combined query and TanStack Query options.
 * @returns TanStack Query result with `data` typed as `T | undefined`.
 *
 * @example
 * ```tsx
 * const { data, isLoading } = useBifrostQuery<User[]>('users', {
 *   fields: ['id', 'name', 'email'],
 *   filter: { active: true },
 *   sort: [{ field: 'name', direction: 'asc' }],
 *   pagination: { limit: 25 },
 * });
 * ```
 */
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
