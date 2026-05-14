import { useContext } from 'react';
import { useQuery } from '@tanstack/react-query';
import { BifrostContext } from '@bifrostql/react';
import type { AppMetadata } from './types';

/** The conventional path of the app-metadata overlay endpoint. */
const APP_METADATA_PATH = '/_app-metadata';

/**
 * Derive the app-metadata endpoint URL from a BifrostQL GraphQL endpoint.
 *
 * The overlay endpoint (`/_app-metadata`) is a sibling of the GraphQL endpoint,
 * served by the same host. The GraphQL path segment is replaced rather than
 * appended so trailing path segments (e.g. `/graphql`) are not retained.
 *
 * @param graphqlEndpoint - The configured GraphQL endpoint URL.
 * @returns The absolute app-metadata endpoint URL.
 */
function resolveMetadataUrl(graphqlEndpoint: string): string {
  const url = new URL(graphqlEndpoint);
  url.pathname = APP_METADATA_PATH;
  url.search = '';
  url.hash = '';
  return url.toString();
}

/**
 * Fetch the app-metadata overlay JSON from the resolved endpoint.
 *
 * @param endpoint - The GraphQL endpoint URL, used to derive the overlay URL.
 * @param headers - Static HTTP headers to include on the request.
 * @param signal - `AbortSignal` for request cancellation.
 * @param getToken - Optional async/sync function returning a bearer token.
 * @returns The parsed {@link AppMetadata} overlay.
 * @throws {Error} On HTTP failure or non-JSON responses.
 */
async function fetchAppMetadata(
  endpoint: string,
  headers: Record<string, string>,
  signal: AbortSignal,
  getToken?: () => string | null | Promise<string | null>,
): Promise<AppMetadata> {
  const mergedHeaders: Record<string, string> = { ...headers };

  if (getToken) {
    const token = await getToken();
    if (token) {
      mergedHeaders.Authorization = `Bearer ${token}`;
    }
  }

  const response = await fetch(resolveMetadataUrl(endpoint), {
    method: 'GET',
    headers: mergedHeaders,
    signal,
  });

  if (!response.ok) {
    throw new Error(
      `App metadata request failed: ${response.status} ${response.statusText}`,
    );
  }

  return (await response.json()) as AppMetadata;
}

/** Result of the {@link useAppMetadata} hook. */
export interface UseAppMetadataResult {
  /** The full app-metadata overlay, or `undefined` until loaded. */
  data: AppMetadata | undefined;
  /** Entity-level metadata keyed by qualified table name. Empty until loaded. */
  entities: NonNullable<AppMetadata['entities']>;
  /** Whether the overlay request is in flight with no cached data. */
  isLoading: boolean;
  /** Whether the overlay request failed. */
  isError: boolean;
  /** The error thrown by the overlay request, if any. */
  error: Error | null;
  /** Refetch the overlay, bypassing the cache. */
  refetch: () => void;
}

/**
 * Fetch and cache the BifrostQL app-metadata overlay served at
 * `/_app-metadata`.
 *
 * The overlay endpoint is derived from the {@link BifrostContext} GraphQL
 * endpoint, so the hook must be used within a `BifrostProvider`. The result is
 * cached by TanStack Query under a stable key; all CRUD screens consuming this
 * hook share a single fetch.
 *
 * Per-entity `fields`, `grid`, and `relationships` are reached through the
 * returned `entities` map (e.g. `entities['dbo.users'].fields`).
 *
 * @returns The overlay data plus loading and error state.
 *
 * @example
 * ```tsx
 * const { entities, isLoading } = useAppMetadata();
 * const users = entities['dbo.users'];
 * ```
 */
export function useAppMetadata(): UseAppMetadataResult {
  const config = useContext(BifrostContext);
  if (!config) {
    throw new Error('useAppMetadata must be used within a BifrostProvider');
  }

  const { endpoint, headers, getToken } = config;

  const query = useQuery<AppMetadata>({
    queryKey: ['bifrost-app-metadata', endpoint],
    queryFn: ({ signal }) =>
      fetchAppMetadata(endpoint, headers ?? {}, signal, getToken),
  });

  return {
    data: query.data,
    entities: query.data?.entities ?? {},
    isLoading: query.isLoading,
    isError: query.isError,
    error: query.error,
    refetch: () => {
      void query.refetch();
    },
  };
}
