/** Default TanStack Query options applied to all queries within a {@link BifrostProvider}. */
export interface BifrostDefaultQueryOptions {
  /** Number of retry attempts for failed queries, or `false` to disable retries. */
  retry?: number | false;
  /** Duration in milliseconds that cached data is considered fresh. */
  staleTime?: number;
  /** Duration in milliseconds before unused cached data is garbage-collected. */
  gcTime?: number;
}

/**
 * Configuration for a BifrostQL client instance, provided via {@link BifrostProvider}.
 *
 * @example
 * ```ts
 * const config: BifrostConfig = {
 *   endpoint: 'https://api.example.com/graphql',
 *   headers: { 'X-Custom-Header': 'value' },
 *   getToken: () => localStorage.getItem('token'),
 * };
 * ```
 */
export interface BifrostConfig {
  /** The URL of the BifrostQL GraphQL endpoint. */
  endpoint: string;
  /** Static HTTP headers sent with every request. */
  headers?: Record<string, string>;
  /**
   * Async or sync function that returns a bearer token.
   * Called before each request; the returned value is set as the `Authorization` header.
   */
  getToken?: () => string | null | Promise<string | null>;
  /** Default TanStack Query options applied to all queries. */
  defaultQueryOptions?: BifrostDefaultQueryOptions;
  /** Global error handler invoked on any mutation failure. */
  onError?: (error: Error) => void;
}

/**
 * Back-compat re-export. The shared filter/query contract types now live in the
 * neutral `@bifrostql/types` package so they can be shared with non-React
 * clients. This module preserves the existing `./types` import path for
 * `@bifrostql/react` internals and keeps the package's public type surface
 * unchanged.
 */
export type {
  TableFilter,
  FieldFilter,
  CompoundFilter,
  AdvancedFilter,
  PaginationOptions,
  SortOption,
  QueryOptions,
} from '@bifrostql/types';
