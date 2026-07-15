/**
 * Saved-form definition shape consumed by the form runner.
 *
 * A {@link FormDefinition} is the builder's output (see the desktop shell's
 * `forms/form-state.ts`): a bound single-record data-entry form over one table.
 * The runner binds it to live data. The types are re-declared here — rather than
 * imported from the shell — to keep this shipped stack self-contained (the same
 * choice `common/saved-objects.ts` makes for its `SavedObject` contract), and to
 * give the runner a tolerant parser for the `unknown` `SavedObject.definition`
 * payload it is handed.
 */

/** The data-entry widget a field renders as. Mirrors the builder's FormControlType. */
export type FormControlType =
  | 'text'
  | 'textarea'
  | 'number'
  | 'checkbox'
  | 'date'
  | 'datetime'
  | 'select';

export interface FormField {
  /** Column name within the bound table. */
  column: string;
  /** Display label shown beside the control. */
  label: string;
  control: FormControlType;
  /** Read-only controls render disabled. */
  readOnly: boolean;
  /** Required marker (runtime enforces via field validation). */
  required: boolean;
  /** Whether the field appears on the form. */
  include: boolean;
}

export interface FormDefinition {
  /** Identifier of the bound table (qualified "schema.name" or GraphQL name). */
  table: string;
  /** Form caption. */
  title: string;
  /** Layout columns (1–4): how many fields sit side-by-side. */
  columns: number;
  fields: FormField[];
}

const MAX_LAYOUT_COLUMNS = 4;
const FORM_CONTROLS = new Set<FormControlType>([
  'text',
  'textarea',
  'number',
  'checkbox',
  'date',
  'datetime',
  'select',
]);

function parseControl(value: unknown): FormControlType {
  return typeof value === 'string' && FORM_CONTROLS.has(value as FormControlType)
    ? (value as FormControlType)
    : 'text';
}

function parseLayoutColumns(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return 1;
  return Math.min(MAX_LAYOUT_COLUMNS, Math.max(1, Math.trunc(value)));
}

function parseField(value: unknown): FormField | null {
  if (typeof value !== 'object' || value === null) return null;
  const f = value as Record<string, unknown>;
  if (typeof f.column !== 'string' || f.column === '') return null;
  return {
    column: f.column,
    label: typeof f.label === 'string' ? f.label : f.column,
    control: parseControl(f.control),
    readOnly: f.readOnly === true,
    required: f.required === true,
    include: f.include !== false,
  };
}

/**
 * Validates an unknown value (e.g. a `SavedObject.definition`) as a
 * {@link FormDefinition}. Returns null when it cannot be bound — a missing table,
 * or no parseable fields, means there is nothing to render. Unparseable
 * individual fields are dropped (tolerant), mirroring the builder's storage
 * parser, so one bad field never blanks the whole form.
 */
export function parseFormDefinition(value: unknown): FormDefinition | null {
  if (typeof value !== 'object' || value === null) return null;
  const def = value as Record<string, unknown>;
  if (typeof def.table !== 'string' || def.table === '' || !Array.isArray(def.fields)) {
    return null;
  }
  const fields = def.fields
    .map(parseField)
    .filter((f): f is FormField => f !== null);
  if (fields.length === 0) return null;
  return {
    table: def.table,
    title: typeof def.title === 'string' ? def.title : def.table,
    columns: parseLayoutColumns(def.columns),
    fields,
  };
}

/** The included fields, in definition order — what the runner renders. */
export function visibleFields(def: FormDefinition): FormField[] {
  return def.fields.filter((f) => f.include);
}
