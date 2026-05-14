import { useMemo, useState } from 'react';
import type { FormEvent, ReactNode } from 'react';
import { useAppMetadata } from '../metadata/use-app-metadata';
import { FieldControl } from '../fields/field-control';
import type { EntityMetadata, FieldMetadata } from '../metadata/types';

/** The editing mode of an {@link EntityForm}. */
export type EntityFormMode = 'create' | 'edit';

/** Per-field options supplied by the caller for `enum` and `fk` controls. */
export interface EntityFormFieldOptions {
  /** Allowed values for an `enum`/`select` field. */
  enumOptions?: string[];
  /** Candidate rows for an `fk` field. */
  fkOptions?: Array<{ key: string; label: string }>;
  /** Qualified table name of the FK target. */
  fkTargetEntity?: string;
}

/** Props for {@link EntityForm}. */
export interface EntityFormProps {
  /** Qualified table name of the entity (e.g. `dbo.users`). */
  entityKey: string;
  /** Whether the form creates a new row or edits an existing one. */
  mode: EntityFormMode;
  /**
   * Initial field values. Required for `edit`; optional for `create` (defaults
   * to an empty record).
   */
  initialValues?: Record<string, unknown>;
  /**
   * Invoked on submit with the current field values. Persistence (via
   * `useBifrostMutation` / `useBifrostDiff`) is the caller's responsibility so
   * the form stays agnostic of insert-vs-diff strategy.
   */
  onSubmit: (values: Record<string, unknown>) => void;
  /** Invoked when the Cancel button is pressed. */
  onCancel?: () => void;
  /** Per-field options for `enum`/`fk` controls, keyed by field name. */
  fieldOptions?: Record<string, EntityFormFieldOptions>;
  /** Rendered while the app-metadata overlay is still loading. */
  loadingFallback?: ReactNode;
  /** Rendered when the entity is absent from the overlay. */
  notFoundFallback?: ReactNode;
}

/**
 * Order the editable fields of an entity for form rendering.
 *
 * Hidden fields (`visible: false`) are omitted. Read-only fields are kept —
 * the {@link FieldControl} renders them as read-only — so detail-like fields
 * still appear in the form.
 */
export function buildFormFields(
  entity: EntityMetadata,
): Array<[string, FieldMetadata]> {
  const fields: Record<string, FieldMetadata> = entity.fields ?? {};
  return Object.entries(fields).filter(
    ([, field]) => field.visible !== false,
  );
}

/**
 * Metadata-driven create/edit form.
 *
 * Renders one {@link FieldControl} per visible entity field, resolving the
 * control kind from each field's `widget` hint. Field state is held locally;
 * `onSubmit` receives the full value record so the caller can route it through
 * `useBifrostMutation` (create) or `useBifrostDiff` (edit).
 *
 * Must be mounted within a `BifrostProvider` (typically via `AppShellProvider`)
 * and is intended to sit inside `AppLayout` / `ProtectedRoute`.
 *
 * @example
 * ```tsx
 * <EntityForm
 *   entityKey="dbo.users"
 *   mode="edit"
 *   initialValues={row}
 *   onSubmit={(values) => diffMutation.mutate({ id: row.id, original: row, updated: values })}
 * />
 * ```
 */
export function EntityForm({
  entityKey,
  mode,
  initialValues,
  onSubmit,
  onCancel,
  fieldOptions,
  loadingFallback = null,
  notFoundFallback = null,
}: EntityFormProps) {
  const { entities, isLoading } = useAppMetadata();
  const entity = entities[entityKey];

  const [values, setValues] = useState<Record<string, unknown>>(
    () => ({ ...(initialValues ?? {}) }),
  );

  const formFields = useMemo(
    () => (entity ? buildFormFields(entity) : []),
    [entity],
  );

  if (isLoading) {
    return <>{loadingFallback}</>;
  }

  if (!entity) {
    return <>{notFoundFallback}</>;
  }

  const setField = (name: string, value: unknown) => {
    setValues((prev) => ({ ...prev, [name]: value }));
  };

  const handleSubmit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    onSubmit(values);
  };

  const title = `${mode === 'create' ? 'Create' : 'Edit'} ${
    entity.label ?? entityKey
  }`;

  return (
    <form
      className="bifrost-entity-form"
      data-testid={`entity-form-${entityKey}`}
      onSubmit={handleSubmit}
    >
      <h2 className="bifrost-entity-form__title">{title}</h2>
      {formFields.map(([fieldName, field]) => {
        const opts = fieldOptions?.[fieldName];
        return (
          <FieldControl
            key={fieldName}
            name={fieldName}
            field={field}
            label={fieldName}
            value={values[fieldName]}
            onChange={(value) => setField(fieldName, value)}
            enumOptions={opts?.enumOptions}
            fkOptions={opts?.fkOptions}
            fkTargetEntity={opts?.fkTargetEntity}
          />
        );
      })}
      <div className="bifrost-entity-form__actions">
        <button type="submit" className="bifrost-entity-form__submit">
          {mode === 'create' ? 'Create' : 'Save'}
        </button>
        {onCancel ? (
          <button
            type="button"
            className="bifrost-entity-form__cancel"
            onClick={onCancel}
          >
            Cancel
          </button>
        ) : null}
      </div>
    </form>
  );
}
