import { useMemo } from 'react';
import type { ReactNode } from 'react';
import { BifrostTable } from '@bifrostql/react';
import type { ColumnConfig, RowAction } from '@bifrostql/react';
import { useAppMetadata } from '../metadata/use-app-metadata';
import type { EntityMetadata, FieldMetadata } from '../metadata/types';

/** Props for {@link EntityList}. */
export interface EntityListProps {
  /** Qualified table name of the entity to list (e.g. `dbo.users`). */
  entityKey: string;
  /** Row actions forwarded to the underlying `BifrostTable` (e.g. Edit/View). */
  rowActions?: RowAction[];
  /** Invoked when a row is clicked; receives the row record. */
  onRowClick?: (row: Record<string, unknown>) => void;
  /** Rendered while the app-metadata overlay is still loading. */
  loadingFallback?: ReactNode;
  /** Rendered when the entity is absent from the overlay. */
  notFoundFallback?: ReactNode;
}

/**
 * Convert a qualified app-metadata entity key to its BifrostQL GraphQL query
 * field name.
 *
 * App-metadata entity keys are schema-qualified (`dbo.users`, `sales.orders`),
 * but GraphQL field names cannot contain `.`. BifrostQL names tables in the
 * `dbo` schema by the bare table name and prefixes other schemas with
 * `<schema>_` (mirrors C# `DbTable.FullName`). Keys without a `.` are returned
 * unchanged.
 *
 * @example
 * entityKeyToQueryName('dbo.users') // 'users'
 * entityKeyToQueryName('sales.orders') // 'sales_orders'
 */
export function entityKeyToQueryName(entityKey: string): string {
  const dotIndex = entityKey.indexOf('.');
  if (dotIndex === -1) {
    return entityKey;
  }
  const schema = entityKey.slice(0, dotIndex);
  const table = entityKey.slice(dotIndex + 1);
  return schema === 'dbo' ? table : `${schema}_${table}`;
}

/**
 * Derive the table columns for an entity from its metadata.
 *
 * Column order prefers the grid preset's `defaultColumns`; when absent, every
 * visible field is shown. Hidden fields (`visible: false`) are always omitted.
 * The column `header` falls back to the field name when no label is available.
 */
export function buildColumns(entity: EntityMetadata): ColumnConfig[] {
  const fields: Record<string, FieldMetadata> = entity.fields ?? {};
  const preset = entity.grid?.defaultColumns;

  const fieldNames =
    preset && preset.length > 0 ? preset : Object.keys(fields);

  return fieldNames
    .filter((fieldName) => fields[fieldName]?.visible !== false)
    .map((fieldName) => ({
      field: fieldName,
      header: fieldName,
      sortable: true,
      filterable: true,
    }));
}

/**
 * Metadata-driven list screen.
 *
 * Resolves the entity's columns from {@link useAppMetadata} and renders a
 * `@bifrostql/react` `BifrostTable` against the entity's table. A new app gets
 * a working list view by declaring the entity in app-metadata — no
 * hand-written table wiring.
 *
 * Must be mounted within a `BifrostProvider` (typically via `AppShellProvider`)
 * and is intended to sit inside `AppLayout` / `ProtectedRoute`.
 *
 * @example
 * ```tsx
 * <EntityList entityKey="dbo.users" rowActions={[editAction]} />
 * ```
 */
export function EntityList({
  entityKey,
  rowActions,
  onRowClick,
  loadingFallback = null,
  notFoundFallback = null,
}: EntityListProps) {
  const { entities, isLoading } = useAppMetadata();
  const entity = entities[entityKey];

  const columns = useMemo(
    () => (entity ? buildColumns(entity) : []),
    [entity],
  );

  const queryName = useMemo(
    () => entityKeyToQueryName(entityKey),
    [entityKey],
  );

  if (isLoading) {
    return <>{loadingFallback}</>;
  }

  if (!entity) {
    return <>{notFoundFallback}</>;
  }

  return (
    <section className="bifrost-entity-list" data-testid={`entity-list-${entityKey}`}>
      <h2 className="bifrost-entity-list__title">{entity.label ?? entityKey}</h2>
      <BifrostTable
        query={queryName}
        columns={columns}
        rowActions={rowActions}
        onRowClick={onRowClick}
      />
    </section>
  );
}
