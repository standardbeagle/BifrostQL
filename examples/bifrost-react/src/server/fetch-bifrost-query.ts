import type { QueryClient } from '@tanstack/react-query';
import { executeGraphQL } from '../utils/graphql-client';
import { buildGraphqlQuery } from '../utils/query-builder';
import type { QueryOptions } from '../types';

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

  await queryClient.prefetchQuery({
    queryKey,
    queryFn: () =>
      executeGraphQL<{ [key: string]: T }>(endpoint, headers, query),
    staleTime,
  });

  return queryClient.getQueryData<{ [key: string]: T }>(queryKey)?.[table] as T;
}
