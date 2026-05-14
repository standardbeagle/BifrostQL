import { useMemo } from 'react';
import type { ReactNode } from 'react';
import { useAppMetadata } from '../metadata/use-app-metadata';
import type { EntityMetadata, FieldMetadata } from '../metadata/types';

/** Props for {@link EntityDetail}. */
export interface EntityDetailProps {
  /** Qualified table name of the entity (e.g. `dbo.users`). */
  entityKey: string;
  /** The row record to display. */
  row: Record<string, unknown>;
  /** Rendered while the app-metadata overlay is still loading. */
  loadingFallback?: ReactNode;
  /** Rendered when the entity is absent from the overlay. */
  notFoundFallback?: ReactNode;
}

/**
 * Order the displayable fields of an entity for the detail view.
 *
 * Prefers the entity's `displayFields` ordering when present; otherwise shows
 * every field. Hidden fields (`visible: false`) are always omitted.
 */
export function buildDetailFields(
  entity: EntityMetadata,
): Array<[string, FieldMetadata]> {
  const fields: Record<string, FieldMetadata> = entity.fields ?? {};
  const order = entity.displayFields;

  const fieldNames =
    order && order.length > 0 ? order : Object.keys(fields);

  return fieldNames
    .filter((fieldName) => fields[fieldName]?.visible !== false)
    .map(
      (fieldName): [string, FieldMetadata] => [
        fieldName,
        fields[fieldName] ?? {},
      ],
    );
}

/** Render a single field value as display text. */
function formatValue(value: unknown): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}

/**
 * Metadata-driven read-only detail screen.
 *
 * Renders a definition list of the entity's displayable fields for a single
 * row, ordered by the entity's `displayFields` metadata. A new app gets a
 * working detail view by declaring the entity in app-metadata.
 *
 * Must be mounted within a `BifrostProvider` (typically via `AppShellProvider`)
 * and is intended to sit inside `AppLayout` / `ProtectedRoute`.
 *
 * @example
 * ```tsx
 * <EntityDetail entityKey="dbo.users" row={selectedRow} />
 * ```
 */
export function EntityDetail({
  entityKey,
  row,
  loadingFallback = null,
  notFoundFallback = null,
}: EntityDetailProps) {
  const { entities, isLoading } = useAppMetadata();
  const entity = entities[entityKey];

  const detailFields = useMemo(
    () => (entity ? buildDetailFields(entity) : []),
    [entity],
  );

  if (isLoading) {
    return <>{loadingFallback}</>;
  }

  if (!entity) {
    return <>{notFoundFallback}</>;
  }

  return (
    <section
      className="bifrost-entity-detail"
      data-testid={`entity-detail-${entityKey}`}
    >
      <h2 className="bifrost-entity-detail__title">
        {entity.label ?? entityKey}
      </h2>
      <dl className="bifrost-entity-detail__list">
        {detailFields.map(([fieldName, field]) => (
          <div
            key={fieldName}
            className="bifrost-entity-detail__row"
            data-testid={`detail-field-${fieldName}`}
          >
            <dt className="bifrost-entity-detail__label">{fieldName}</dt>
            <dd className="bifrost-entity-detail__value">
              {formatValue(row[fieldName])}
            </dd>
            {field.helpText ? (
              <dd className="bifrost-entity-detail__help">{field.helpText}</dd>
            ) : null}
          </div>
        ))}
      </dl>
    </section>
  );
}
