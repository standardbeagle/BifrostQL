import { useEffect, useRef } from 'react';
import type { ReactNode } from 'react';
import { useSession } from '../auth/use-session';
import { usePolicy } from '@bifrostql/react';

/** Props for {@link ProtectedRoute}. */
export interface ProtectedRouteProps {
  /** The protected content, rendered only when access is granted. */
  children: ReactNode;
  /**
   * Grant name(s) the server must resolve for the caller (`_grants`) to view
   * the route. When an array, *every* listed grant is required. When omitted,
   * the route only requires an authenticated session. Session claims are not
   * consulted: the answer is the server's, so it agrees with what the server
   * enforces.
   */
  requiredGrants?: string | string[];
  /** @deprecated Use {@link ProtectedRouteProps.requiredGrants}. */
  requirePermission?: string | string[];
  /**
   * Invoked once when an unauthenticated user reaches the route. Apps wire this
   * to their router's navigation (e.g. `() => navigate('/login')`). The route
   * is router-agnostic by design — it does not depend on any router package.
   */
  onUnauthenticated?: () => void;
  /**
   * Rendered while the session bootstrap request is still in flight. Defaults
   * to `null` so nothing flashes before the auth state resolves.
   */
  loadingFallback?: ReactNode;
  /**
   * Rendered when the user is authenticated but lacks a required grant (the
   * 403 case). Defaults to a minimal `role="alert"` 403 message.
   */
  forbiddenFallback?: ReactNode;
  /**
   * Rendered for unauthenticated users *after* {@link onUnauthenticated} has
   * fired — typically a redirect placeholder. Defaults to `null`.
   */
  unauthenticatedFallback?: ReactNode;
}

/** Default 403 view rendered when a grant check fails. */
const defaultForbiddenFallback: ReactNode = (
  <div role="alert">403 — You do not have permission to view this page.</div>
);

/**
 * Normalize the `requiredGrants` prop into a flat list of grant names. An
 * omitted prop yields an empty list (auth-only gate).
 */
function toRequiredList(required: string | string[] | undefined): string[] {
  if (required === undefined) {
    return [];
  }
  return Array.isArray(required) ? required : [required];
}

/**
 * Route guard that gates its `children` on authentication and, optionally,
 * server-resolved grants.
 *
 * Behavior, in order:
 * 1. While the session is loading, renders {@link ProtectedRouteProps.loadingFallback}.
 * 2. When unauthenticated, fires {@link ProtectedRouteProps.onUnauthenticated}
 *    (once) and renders {@link ProtectedRouteProps.unauthenticatedFallback}.
 * 3. When grants are required and the server's `_grants` answer is still
 *    loading, renders {@link ProtectedRouteProps.loadingFallback}.
 * 4. When authenticated but missing any required grant, renders the 403
 *    {@link ProtectedRouteProps.forbiddenFallback}.
 * 5. Otherwise renders `children`.
 *
 * The guard is deliberately router-agnostic: redirects are delegated to the
 * caller through `onUnauthenticated`, so it composes with any router (including
 * the custom router used by `examples/edit-db`) without adding a dependency.
 *
 * Must be mounted within a `SessionProvider` (or `AppShellProvider`).
 *
 * @example
 * ```tsx
 * <ProtectedRoute
 *   requiredGrants="dbo.users.read"
 *   onUnauthenticated={() => navigate('/login')}
 * >
 *   <UsersScreen />
 * </ProtectedRoute>
 * ```
 */
export function ProtectedRoute({
  children,
  requiredGrants,
  requirePermission,
  onUnauthenticated,
  loadingFallback = null,
  forbiddenFallback = defaultForbiddenFallback,
  unauthenticatedFallback = null,
}: ProtectedRouteProps) {
  const { isLoading, isAuthenticated } = useSession();
  const policy = usePolicy();

  const shouldRedirect = !isLoading && !isAuthenticated;

  // Callers write `onUnauthenticated={() => navigate('/login')}`, so the prop
  // is a new function on every render. Keeping it in the effect's deps made
  // the effect re-run on each one, firing the redirect repeatedly for as long
  // as the user stayed unauthenticated. Hold it in a ref so the effect depends
  // only on the auth state, while still calling the latest callback.
  const onUnauthenticatedRef = useRef(onUnauthenticated);
  onUnauthenticatedRef.current = onUnauthenticated;

  useEffect(() => {
    if (shouldRedirect) {
      onUnauthenticatedRef.current?.();
    }
  }, [shouldRedirect]);

  if (isLoading) {
    return <>{loadingFallback}</>;
  }

  if (!isAuthenticated) {
    return <>{unauthenticatedFallback}</>;
  }

  const required = toRequiredList(requiredGrants ?? requirePermission);
  if (required.length > 0 && policy.isLoading) {
    return <>{loadingFallback}</>;
  }
  const granted = new Set(policy.grants);
  const hasAllGrants = required.every((grant) => granted.has(grant));

  if (!hasAllGrants) {
    return <>{forbiddenFallback}</>;
  }

  return <>{children}</>;
}
