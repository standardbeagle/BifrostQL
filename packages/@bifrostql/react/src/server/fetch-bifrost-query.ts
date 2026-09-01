import type { QueryClient } from '@tanstack/react-query';
import { executeGraphQL } from '../utils/graphql-client';
import { buildGraphqlQuery } from '../utils/query-builder';
import type { PagedResult, QueryOptions } from '../types';

export interface FetchBifrostQueryOptions extends QueryOptions {
  endpoint: string;
  headers?: Record<string, string>;
  table: string;
  staleTime?: number;
}

/**
 * Prefetch a BifrostQL query into a QueryClient for server-side rendering.
 *
 * The data is stored under the same query key that `useBifrost` uses on the
 * client, so hydration picks it up automatically with no flash of loading state.
 *
 * Works with both Next.js App Router (server components) and Pages Router
 * (`getServerSideProps` / `getStaticProps`).
 */
export async function fetchBifrostQuery<T = unknown>(
  queryClient: QueryClient,
  options: FetchBifrostQueryOptions,
): Promise<T> {
  const { endpoint, headers = {}, table, staleTime, ...queryOptions } = options;
  const query = buildGraphqlQuery(table, queryOptions);
  const queryKey = ['bifrost', query, {}];

  // The cache stores the raw enveloped response — the same shape useBifrost
  // hydrates from — while the convenience return value is the unwrapped rows.
  await queryClient.prefetchQuery({
    queryKey,
    queryFn: () =>
      executeGraphQL<{ [key: string]: PagedResult }>(endpoint, headers, query),
    staleTime,
  });

  return queryClient.getQueryData<{ [key: string]: PagedResult }>(queryKey)?.[
    table
  ]?.data as T;
}
