import { useMemo } from 'react';
import type { ReactNode } from 'react';
import { useAppMetadata } from '../metadata/use-app-metadata';
import { useSession } from '../auth/use-session';
import type { EntityMetadata } from '../metadata/types';

/**
 * A single resolved navigation entry derived from an app-metadata entity.
 *
 * One {@link NavItem} is produced per entity in the app-metadata overlay that
 * the current session is permitted to see. The `key` is the qualified table
 * name (e.g. `dbo.users`); `label` falls back to the key when the entity has no
 * explicit `label`.
 */
export interface NavItem {
  /** Qualified table name of the entity (e.g. `dbo.users`). Stable and unique. */
  key: string;
  /** Human-readable label; the entity `label`, or the `key` when unset. */
  label: string;
  /** Icon hint copied from the entity metadata, if any. */
  icon?: string;
  /** Navigation placement hint copied from the entity metadata, if any. */
  navPlacement?: string;
  /** The permission string this entry was gated on (for diagnostics/testing). */
  permission: string;
}

/**
 * Resolve the permission string required to see an entity's nav entry.
 *
 * The default convention is `<entityKey>.read` (e.g. `dbo.users.read`). Apps
 * with a different permission taxonomy can override this via
 * {@link AppNavProps.permissionFor}.
 */
export type PermissionResolver = (
  entityKey: string,
  entity: EntityMetadata,
) => string;

/** Default {@link PermissionResolver}: `<entityKey>.read`. */
const defaultPermissionFor: PermissionResolver = (entityKey) =>
  `${entityKey}.read`;

/** Props for {@link AppNav}. */
export interface AppNavProps {
  /**
   * Render-prop for the navigation list. Receives the permission-filtered,
   * sorted {@link NavItem}s. When omitted, {@link AppNav} renders a default
   * `<nav>` with one `<a>` per item linking to `#/<entityKey>`.
   */
  children?: (items: NavItem[]) => ReactNode;
  /**
   * Override the permission required per entity. Defaults to
   * `<entityKey>.read`.
   */
  permissionFor?: PermissionResolver;
  /** Rendered while the app-metadata overlay is still loading. */
  loadingFallback?: ReactNode;
}

/**
 * Build the permission-filtered, sorted list of {@link NavItem}s from the
 * app-metadata overlay and the current session permissions.
 *
 * Entities the session lacks permission for are omitted. Items are sorted by
 * `label` for a stable render order.
 */
function buildNavItems(
  entities: Record<string, EntityMetadata>,
  permissions: string[],
  permissionFor: PermissionResolver,
): NavItem[] {
  const granted = new Set(permissions);

  return Object.entries(entities)
    .map(([key, entity]): NavItem => {
      const permission = permissionFor(key, entity);
      return {
        key,
        label: entity.label ?? key,
        icon: entity.icon,
        navPlacement: entity.navPlacement,
        permission,
      };
    })
    .filter((item) => granted.has(item.permission))
    .sort((a, b) => a.label.localeCompare(b.label));
}

/**
 * Permission-aware navigation driven by the app-metadata overlay.
 *
 * Lists one entry per app-metadata entity, hiding entries the current
 * {@link useSession} identity lacks permission for. Must be mounted within an
 * {@link import('../app-shell-provider').AppShellProvider} (or an equivalent
 * `BifrostProvider` + `SessionProvider` pair).
 *
 * Consumers can fully control rendering via the `children` render-prop; when
 * omitted, a minimal default `<nav>` is rendered.
 *
 * @example
 * ```tsx
 * <AppNav>
 *   {(items) => (
 *     <ul>
 *       {items.map((i) => (
 *         <li key={i.key}>
 *           <Link to={`/${i.key}`}>{i.label}</Link>
 *         </li>
 *       ))}
 *     </ul>
 *   )}
 * </AppNav>
 * ```
 */
export function AppNav({
  children,
  permissionFor = defaultPermissionFor,
  loadingFallback = null,
}: AppNavProps) {
  const { entities, isLoading } = useAppMetadata();
  const { permissions } = useSession();

  const items = useMemo(
    () => buildNavItems(entities, permissions, permissionFor),
    [entities, permissions, permissionFor],
  );

  if (isLoading) {
    return <>{loadingFallback}</>;
  }

  if (children) {
    return <>{children(items)}</>;
  }

  return (
    <nav aria-label="Application navigation">
      <ul>
        {items.map((item) => (
          <li key={item.key}>
            <a href={`#/${item.key}`}>{item.label}</a>
          </li>
        ))}
      </ul>
    </nav>
  );
}
