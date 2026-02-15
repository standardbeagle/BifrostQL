import { QueryClient } from '@tanstack/react-query';

function makeQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 60 * 1000,
      },
    },
  });
}

let serverQueryClient: QueryClient | null = null;

/**
 * Returns a QueryClient suitable for server-side rendering.
 *
 * On the server, a singleton is reused across the request to allow
 * multiple prefetch calls to share the same cache. On the client
 * a fresh instance is created each time so React hydration picks up
 * the dehydrated state without stale singletons.
 */
export function getQueryClient(): QueryClient {
  if (typeof window === 'undefined') {
    if (!serverQueryClient) {
      serverQueryClient = makeQueryClient();
    }
    return serverQueryClient;
  }
  return makeQueryClient();
}

/**
 * Reset the server-side singleton. Useful in tests and between requests
 * in long-running server processes.
 */
export function resetServerQueryClient(): void {
  serverQueryClient = null;
}
