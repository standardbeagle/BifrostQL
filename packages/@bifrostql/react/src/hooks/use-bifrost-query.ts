import { buildGraphqlQuery } from '../utils/query-builder';
import { useBifrost } from './use-bifrost';
import type { UseBifrostOptions } from './use-bifrost';
import type {
  FieldNameOf,
  PagedResult,
  QueryOptions,
  SortOptionFor,
} from '../types';

export interface UseBifrostQueryOptions<TRow = unknown>
  extends Omit<QueryOptions, 'sort' | 'fields'>, UseBifrostOptions {
  /** Sort directives; `field` is constrained to `keyof TRow` when typed. */
  sort?: readonly SortOptionFor<TRow>[];
  /** Fields to select; constrained to `keyof TRow` when typed. */
  fields?: readonly FieldNameOf<TRow>[];
}

/**
 * The row type of a query result: unwraps one array level, so
 * `useBifrostQuery<User[]>` constrains `fields`/`sort` to `keyof User`.
 * Non-array result types (including the `unknown` default) pass through and
 * fall back to unconstrained `string` field names via {@link FieldNameOf}.
 */
export type RowOf<T> = T extends readonly (infer TRow)[] ? TRow : T;

/**
 * Table-oriented query hook with declarative filter, sort, pagination, and
 * field-selection support.
 *
 * Builds a GraphQL query from the provided options using {@link buildGraphqlQuery}
 * and executes it via {@link useBifrost}. BifrostQL wraps table results in a
 * paged envelope (`{ users { total data { ... } } }`); the returned `data` is
 * automatically unwrapped through the envelope to the row array, and the
 * envelope's `total` (the unpaged match count) is surfaced alongside it.
 *
 * Must be used within a {@link BifrostProvider}.
 *
 * @typeParam T - The expected row data type.
 * @param table - The database table name to query.
 * @param options - Combined query and TanStack Query options.
 * @returns TanStack Query result with `data` typed as `T | undefined` and
 * `total` carrying the envelope's unpaged match count.
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
  // NoInfer: the row type is opt-in via the explicit type argument; without it
  // TS would reverse-infer T from `fields` literals at untyped call sites.
  options: UseBifrostQueryOptions<RowOf<NoInfer<T>>> = {},
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

  const result = useBifrost<{ [key: string]: PagedResult<RowOf<T>> }>(
    query,
    undefined,
    {
      enabled,
      retry,
      retryDelay,
      staleTime,
      gcTime,
      refetchInterval,
      refetchOnWindowFocus,
    },
  );

  const envelope = result.data?.[table];
  const data = envelope?.data as T | undefined;

  return {
    ...result,
    data,
    /** Unpaged match count from the paged envelope. */
    total: envelope?.total,
  };
}
