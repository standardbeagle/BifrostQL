import { useContext } from 'react';
import type { ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { BifrostContext } from '@bifrostql/react';
import { SessionContext } from './session-context';
import type { AppIdentity, SessionState } from './session-context';

/**
 * Conventional path of the read-session endpoint, a sibling of the GraphQL
 * endpoint. As of this sub-task the hosted server exposes `/auth/login` and
 * `/auth/logout` (cookie auth) but no read-session route; see the package
 * follow-up note. The client is designed against this documented expectation:
 * a `GET` returning the `AppIdentity` JSON for the current cookie session, or
 * `401` when there is no session.
 */
const SESSION_PATH = '/auth/session';

/** Sentinel returned by the query function to represent "no authenticated user". */
const UNAUTHENTICATED = null;

/**
 * Derive the read-session endpoint URL from a BifrostQL GraphQL endpoint.
 *
 * In hosted mode the session endpoint is a same-origin sibling of the GraphQL
 * endpoint, so the GraphQL path segment is replaced rather than appended.
 *
 * @param graphqlEndpoint - The configured GraphQL endpoint URL.
 * @returns The absolute read-session endpoint URL.
 */
function resolveSessionUrl(graphqlEndpoint: string): string {
  const url = new URL(graphqlEndpoint);
  url.pathname = SESSION_PATH;
  url.search = '';
  url.hash = '';
  return url.toString();
}

/**
 * Fetch the current authenticated {@link AppIdentity} from the read-session
 * endpoint.
 *
 * The request is sent with `credentials: 'include'` so the auth cookie issued
 * by `/auth/login` is honored. A `401` response is an expected, non-error
 * outcome and resolves to `null` (unauthenticated). Any other non-OK status
 * throws so the caller can surface it as an error.
 *
 * @param endpoint - The GraphQL endpoint URL, used to derive the session URL.
 * @param headers - Static HTTP headers to include on the request.
 * @param signal - `AbortSignal` for request cancellation.
 * @param getToken - Optional async/sync function returning a bearer token.
 * @returns The authenticated identity, or `null` when unauthenticated.
 * @throws {Error} On HTTP failures other than `401`.
 */
async function fetchSession(
  endpoint: string,
  headers: Record<string, string>,
  signal: AbortSignal,
  getToken?: () => string | null | Promise<string | null>,
): Promise<AppIdentity | null> {
  const mergedHeaders: Record<string, string> = { ...headers };

  if (getToken) {
    const token = await getToken();
    if (token) {
      mergedHeaders.Authorization = `Bearer ${token}`;
    }
  }

  const response = await fetch(resolveSessionUrl(endpoint), {
    method: 'GET',
    headers: mergedHeaders,
    credentials: 'include',
    signal,
  });

  if (response.status === 401) {
    return UNAUTHENTICATED;
  }

  if (!response.ok) {
    throw new Error(
      `Session request failed: ${response.status} ${response.statusText}`,
    );
  }

  return (await response.json()) as AppIdentity;
}

/** Props for {@link SessionProvider}. */
export interface SessionProviderProps {
  children: ReactNode;
}

/**
 * Bootstraps the authentication session and exposes it via {@link SessionContext}.
 *
 * The read-session endpoint is derived from the {@link BifrostContext} GraphQL
 * endpoint, so this provider must be mounted within a `BifrostProvider`
 * (the {@link AppShellProvider} wrapper does this automatically). The result is
 * cached by TanStack Query under a stable key.
 *
 * Graceful degradation: an expected `401` is treated as unauthenticated rather
 * than an error. If the read-session endpoint is unreachable or returns another
 * failure, the error is surfaced on {@link SessionState.error} while the session
 * degrades to unauthenticated, so the app shell can still render a login path.
 *
 * @example
 * ```tsx
 * <BifrostProvider config={config}>
 *   <SessionProvider>
 *     <App />
 *   </SessionProvider>
 * </BifrostProvider>
 * ```
 */
export function SessionProvider({ children }: SessionProviderProps) {
  const config = useContext(BifrostContext);
  if (!config) {
    throw new Error('SessionProvider must be used within a BifrostProvider');
  }

  const { endpoint, headers, getToken } = config;

  const query = useQuery<AppIdentity | null>({
    queryKey: ['bifrost-session', endpoint],
    queryFn: ({ signal }) =>
      fetchSession(endpoint, headers ?? {}, signal, getToken),
  });

  const identity = query.data ?? null;

  const value: SessionState = {
    identity,
    permissions: identity?.permissions ?? [],
    isAuthenticated: identity !== null,
    isLoading: query.isLoading,
    error: query.error,
    refresh: () => {
      void query.refetch();
    },
  };

  return (
    <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
  );
}
