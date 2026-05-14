import { useEffect } from 'react';
import type { ReactNode } from 'react';
import { useSession } from '../auth/use-session';

/** Props for {@link ProtectedRoute}. */
export interface ProtectedRouteProps {
  /** The protected content, rendered only when access is granted. */
  children: ReactNode;
  /**
   * Permission string(s) required to view the route. When an array, the
   * session must hold *every* listed permission. When omitted, the route only
   * requires an authenticated session (no specific permission).
   */
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
   * Rendered when the user is authenticated but lacks a required permission
   * (the 403 case). Defaults to a minimal `role="alert"` 403 message.
   */
  forbiddenFallback?: ReactNode;
  /**
   * Rendered for unauthenticated users *after* {@link onUnauthenticated} has
   * fired — typically a redirect placeholder. Defaults to `null`.
   */
  unauthenticatedFallback?: ReactNode;
}

/** Default 403 view rendered when a permission check fails. */
const defaultForbiddenFallback: ReactNode = (
  <div role="alert">403 — You do not have permission to view this page.</div>
);

/**
 * Normalize the `requirePermission` prop into a flat list of required
 * permission strings. An omitted prop yields an empty list (auth-only gate).
 */
function toRequiredList(
  requirePermission: string | string[] | undefined,
): string[] {
  if (requirePermission === undefined) {
    return [];
  }
  return Array.isArray(requirePermission)
    ? requirePermission
    : [requirePermission];
}

/**
 * Route guard that gates its `children` on authentication and, optionally,
 * fine-grained permissions.
 *
 * Behavior, in order:
 * 1. While the session is loading, renders {@link ProtectedRouteProps.loadingFallback}.
 * 2. When unauthenticated, fires {@link ProtectedRouteProps.onUnauthenticated}
 *    (once) and renders {@link ProtectedRouteProps.unauthenticatedFallback}.
 * 3. When authenticated but missing any required permission, renders the 403
 *    {@link ProtectedRouteProps.forbiddenFallback}.
 * 4. Otherwise renders `children`.
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
 *   requirePermission="dbo.users.read"
 *   onUnauthenticated={() => navigate('/login')}
 * >
 *   <UsersScreen />
 * </ProtectedRoute>
 * ```
 */
export function ProtectedRoute({
  children,
  requirePermission,
  onUnauthenticated,
  loadingFallback = null,
  forbiddenFallback = defaultForbiddenFallback,
  unauthenticatedFallback = null,
}: ProtectedRouteProps) {
  const { isLoading, isAuthenticated, permissions } = useSession();

  const shouldRedirect = !isLoading && !isAuthenticated;

  useEffect(() => {
    if (shouldRedirect) {
      onUnauthenticated?.();
    }
  }, [shouldRedirect, onUnauthenticated]);

  if (isLoading) {
    return <>{loadingFallback}</>;
  }

  if (!isAuthenticated) {
    return <>{unauthenticatedFallback}</>;
  }

  const required = toRequiredList(requirePermission);
  const granted = new Set(permissions);
  const hasAllPermissions = required.every((perm) => granted.has(perm));

  if (!hasAllPermissions) {
    return <>{forbiddenFallback}</>;
  }

  return <>{children}</>;
}
