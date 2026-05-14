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
  /**
   * Optional async or sync hook invoked when a request fails authentication
   * (HTTP `401` or a GraphQL auth error). Implementations typically call the
   * server's session/login endpoints (e.g. `GET /auth/session`, `/auth/login`)
   * to obtain a fresh credential. When it resolves successfully the request is
   * retried exactly once with a token re-read via {@link getToken}. When it
   * returns a string, that value is used as the bearer token for the retry.
   * When it throws, or returns without producing a usable token, the original
   * auth failure is surfaced as a typed error.
   */
  refreshToken?: () => string | null | void | Promise<string | null | void>;
  /**
   * Optional hook invoked when an authentication failure could not be
   * recovered — either no {@link refreshToken} hook is configured, or the
   * refresh attempt itself failed. Receives the typed auth error.
   */
  onSessionExpired?: (error: Error) => void;
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
