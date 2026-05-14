import { useMemo } from 'react';
import type { ReactNode } from 'react';
import type { QueryClient } from '@tanstack/react-query';
import { BifrostProvider } from '@bifrostql/react';
import type { BifrostConfig } from '@bifrostql/react';
import { SessionProvider } from './auth/session-provider';

/**
 * Default GraphQL endpoint path for hosted mode. In hosted mode the SPA is
 * served by the BifrostQL host itself (see `samples/HostedSpa`), so the
 * GraphQL endpoint is a same-origin sibling of the SPA.
 */
const HOSTED_GRAPHQL_PATH = '/graphql';

/**
 * Resolve the hosted-mode GraphQL endpoint from the current document origin.
 *
 * Used when {@link AppShellConfig.endpoint} is omitted: the endpoint defaults
 * to `<window.origin>/graphql`. Throws if called without a `window` (e.g. SSR);
 * callers in non-browser environments must pass an explicit `endpoint`.
 *
 * @returns The absolute hosted-mode GraphQL endpoint URL.
 */
function resolveHostedEndpoint(): string {
  if (typeof window === 'undefined') {
    throw new Error(
      'AppShellProvider could not infer a hosted endpoint: no window. ' +
        'Pass an explicit `endpoint` in non-browser environments.',
    );
  }
  return new URL(HOSTED_GRAPHQL_PATH, window.location.origin).toString();
}

/**
 * Single configuration object for {@link AppShellProvider}.
 *
 * Extends {@link BifrostConfig}, but `endpoint` is optional: when omitted it is
 * inferred from the document origin for hosted mode. All other `BifrostConfig`
 * fields (`headers`, `getToken`, `defaultQueryOptions`, `onError`) are passed
 * through to the underlying `BifrostProvider`.
 */
export interface AppShellConfig extends Omit<BifrostConfig, 'endpoint'> {
  /**
   * The GraphQL endpoint URL. Optional in hosted mode: defaults to
   * `<window.origin>/graphql`.
   */
  endpoint?: string;
}

/** Props for {@link AppShellProvider}. */
export interface AppShellProviderProps {
  /** App-shell configuration; a superset of `BifrostConfig` with optional `endpoint`. */
  config?: AppShellConfig;
  /**
   * Optional externally-owned `QueryClient`. Passed through to `BifrostProvider`;
   * when omitted, `BifrostProvider` manages its own client.
   */
  queryClient?: QueryClient;
  children: ReactNode;
}

/**
 * Single entry-point wrapper for a BifrostQL app shell.
 *
 * Composes, from the outside in:
 * 1. {@link BifrostProvider} — wired for hosted mode (endpoint inferred from the
 *    document origin when not supplied).
 * 2. {@link SessionProvider} — bootstraps the authentication session so
 *    {@link useSession} works anywhere beneath this provider.
 *
 * App authors mount this once at the root and pass a single `config` object.
 *
 * @example
 * ```tsx
 * // Hosted mode — endpoint inferred from window.origin:
 * <AppShellProvider>
 *   <App />
 * </AppShellProvider>
 *
 * // Explicit endpoint:
 * <AppShellProvider config={{ endpoint: 'https://api.example.com/graphql' }}>
 *   <App />
 * </AppShellProvider>
 * ```
 */
export function AppShellProvider({
  config,
  queryClient,
  children,
}: AppShellProviderProps) {
  const bifrostConfig = useMemo<BifrostConfig>(
    () => ({
      ...config,
      endpoint: config?.endpoint ?? resolveHostedEndpoint(),
    }),
    [config],
  );

  return (
    <BifrostProvider config={bifrostConfig} queryClient={queryClient}>
      <SessionProvider>{children}</SessionProvider>
    </BifrostProvider>
  );
}
