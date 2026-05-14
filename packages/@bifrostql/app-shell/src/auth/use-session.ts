import { useContext } from 'react';
import { SessionContext } from './session-context';
import type { SessionState } from './session-context';

/**
 * Access the current authentication {@link SessionState}.
 *
 * Must be used within a {@link SessionProvider} (or an {@link AppShellProvider},
 * which composes one). Exposes the authenticated `identity`, its `permissions`,
 * and the `isAuthenticated` / `isLoading` / `error` state.
 *
 * @returns The resolved session state.
 * @throws {Error} When used outside a `SessionProvider`.
 *
 * @example
 * ```tsx
 * const { identity, permissions, isAuthenticated, isLoading } = useSession();
 * if (isLoading) return <Spinner />;
 * if (!isAuthenticated) return <LoginPrompt />;
 * return <span>Hello, {identity.displayName}</span>;
 * ```
 */
export function useSession(): SessionState {
  const session = useContext(SessionContext);
  if (!session) {
    throw new Error('useSession must be used within a SessionProvider');
  }
  return session;
}
