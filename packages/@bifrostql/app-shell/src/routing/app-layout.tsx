import type { ReactNode } from 'react';
import { useSession } from '../auth/use-session';
import { AppNav } from '../nav/app-nav';
import type { AppNavProps } from '../nav/app-nav';

/** Props for {@link AppLayout}. */
export interface AppLayoutProps {
  /**
   * The routed page content, rendered in the layout's main content region.
   * This is the "outlet" — apps pass their router's outlet element here (e.g.
   * `<Routes>...</Routes>` for the custom router, or `<Outlet />` for others).
   */
  children: ReactNode;
  /**
   * Optional override for the navigation render. When omitted, {@link AppLayout}
   * renders a default {@link AppNav}. Pass a node to fully replace the nav, or
   * an {@link AppNavProps} render-prop by composing your own `<AppNav>`.
   */
  nav?: ReactNode;
  /** Props forwarded to the default {@link AppNav} when `nav` is not supplied. */
  navProps?: AppNavProps;
  /**
   * Optional header/branding content rendered above the nav in the layout
   * chrome. Receives no props; compose with {@link useSession} yourself if it
   * needs identity.
   */
  header?: ReactNode;
}

/**
 * Shell layout that composes the permission-aware {@link AppNav} with a routed
 * content outlet and auth-aware chrome.
 *
 * Structure:
 * - A `<header>` containing optional branding plus an identity summary derived
 *   from {@link useSession} (display name / email when authenticated).
 * - An `<aside>` holding the navigation ({@link AppNav} by default, or the
 *   `nav` override).
 * - A `<main>` holding `children` — the router outlet.
 *
 * Like the other routing primitives, {@link AppLayout} is router-agnostic: it
 * renders whatever outlet node the caller passes as `children` and never
 * imports a router package. Must be mounted within an `AppShellProvider` (or an
 * equivalent `BifrostProvider` + `SessionProvider` pair) so {@link AppNav} and
 * the identity summary can resolve.
 *
 * @example
 * ```tsx
 * <AppLayout header={<Brand />}>
 *   <Routes>
 *     <Route path="/" element={<Home />} />
 *   </Routes>
 * </AppLayout>
 * ```
 */
export function AppLayout({ children, nav, navProps, header }: AppLayoutProps) {
  const { identity, isAuthenticated } = useSession();

  const identityLabel = identity
    ? (identity.displayName ?? identity.email ?? identity.id)
    : null;

  return (
    <div className="bifrost-app-layout">
      <header className="bifrost-app-layout__header">
        {header}
        {isAuthenticated && identityLabel ? (
          <span
            className="bifrost-app-layout__identity"
            data-testid="app-layout-identity"
          >
            {identityLabel}
          </span>
        ) : null}
      </header>
      <aside className="bifrost-app-layout__nav">
        {nav ?? <AppNav {...navProps} />}
      </aside>
      <main className="bifrost-app-layout__content">{children}</main>
    </div>
  );
}
