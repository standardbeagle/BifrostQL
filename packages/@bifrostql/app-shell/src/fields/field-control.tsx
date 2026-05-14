import type { FieldMetadata } from '../metadata/types';
import {
  ScalarControl,
  DateControl,
  BooleanControl,
  EnumSelectControl,
  JsonTextControl,
  FkLookupControl,
} from './controls';

/**
 * The resolved kind of control to render for a field. Derived from a field's
 * {@link FieldMetadata} by {@link resolveFieldKind}.
 */
export type FieldKind =
  | 'scalar'
  | 'date'
  | 'boolean'
  | 'enum'
  | 'json'
  | 'fk';

/**
 * Map a field's `widget` hint to a {@link FieldKind}. The `widget` strings are
 * client-interpreted (see `FieldMetadata.widget`); the recognized set below
 * mirrors the reusable control set. Unknown or absent widgets fall back to
 * `scalar`, so a field with no metadata still renders an editable input.
 */
export function resolveFieldKind(field: FieldMetadata | undefined): FieldKind {
  const widget = field?.widget?.toLowerCase().trim();
  switch (widget) {
    case 'date':
    case 'datetime':
      return 'date';
    case 'boolean':
    case 'checkbox':
      return 'boolean';
    case 'select':
    case 'enum':
      return 'enum';
    case 'json':
    case 'text':
    case 'textarea':
      return 'json';
    case 'fk':
    case 'fk-lookup':
    case 'foreignkey':
      return 'fk';
    default:
      return 'scalar';
  }
}

/** Props for {@link FieldControl}. */
export interface FieldControlProps {
  /** Field name; used for `id`/`name` and label association. */
  name: string;
  /** Field-level metadata driving widget selection and presentation. */
  field?: FieldMetadata;
  /** Current field value. */
  value: unknown;
  /** Change handler receiving the next value. */
  onChange: (value: unknown) => void;
  /**
   * Options for `enum` fields. Required when the resolved kind is `enum`;
   * ignored otherwise.
   */
  enumOptions?: string[];
  /**
   * Candidate rows for `fk` fields. Required when the resolved kind is `fk`;
   * ignored otherwise.
   */
  fkOptions?: Array<{ key: string; label: string }>;
  /**
   * Qualified table name of the FK target. Required when the resolved kind is
   * `fk`; ignored otherwise.
   */
  fkTargetEntity?: string;
  /** Overrides the label derived from `name`. */
  label?: string;
}

/**
 * Metadata-driven field-control dispatcher.
 *
 * Resolves a {@link FieldKind} from the field's `widget` hint and renders the
 * matching control from the reusable control set. The label falls back to the
 * field `name` when no explicit `label` is given; `readOnly` and `helpText`
 * are forwarded from the field metadata.
 *
 * Screens (`entity-form`, `entity-detail`) use this so a new app gets working
 * inputs for every field with no hand-written control wiring.
 *
 * @example
 * ```tsx
 * <FieldControl
 *   name="status"
 *   field={{ widget: 'select' }}
 *   enumOptions={['open', 'closed']}
 *   value={row.status}
 *   onChange={(v) => setField('status', v)}
 * />
 * ```
 */
export function FieldControl({
  name,
  field,
  value,
  onChange,
  enumOptions,
  fkOptions,
  fkTargetEntity,
  label,
}: FieldControlProps) {
  const kind = resolveFieldKind(field);
  const resolvedLabel = label ?? name;
  const readOnly = field?.readOnly ?? false;
  const helpText = field?.helpText;

  const shared = {
    name,
    label: resolvedLabel,
    value,
    onChange,
    readOnly,
    helpText,
  };

  switch (kind) {
    case 'date':
      return <DateControl {...shared} />;
    case 'boolean':
      return <BooleanControl {...shared} />;
    case 'enum':
      return <EnumSelectControl {...shared} options={enumOptions ?? []} />;
    case 'json':
      return <JsonTextControl {...shared} />;
    case 'fk':
      return (
        <FkLookupControl
          {...shared}
          options={fkOptions ?? []}
          targetEntity={fkTargetEntity ?? ''}
        />
      );
    case 'scalar':
    default:
      return <ScalarControl {...shared} />;
  }
}
