/**
 * Pure state model for the Access-style form builder.
 *
 * Mirrors the query designer's split (see designer/designer-state.ts): all the
 * logic lives here as immutable transforms so the React shell can stay thin and
 * the behaviour is unit-testable without a DOM.
 *
 * A {@link FormDefinition} is a bound single-record data-entry form over one
 * table — the Access "form" primitive. This slice owns the design surface
 * (which columns become fields, their control type, label, order, layout). The
 * record runtime (load a row, write it back through a GraphQL mutation) is a
 * later slice; the definition shape is already what that runtime will bind to.
 */

import type { BuilderSchema, BuilderColumn } from "../lib/builder-bridge";

/** The data-entry widget a field renders as. */
export type FormControlType =
  | "text"
  | "textarea"
  | "number"
  | "checkbox"
  | "date"
  | "datetime"
  | "select";

export interface FormField {
  /** Column name within the bound table. */
  column: string;
  /** Display label shown beside the control. */
  label: string;
  control: FormControlType;
  /** Read-only controls render disabled (PK / identity columns default to this). */
  readOnly: boolean;
  /** NOT NULL columns are required (marker only; runtime enforces). */
  required: boolean;
  /** Whether the field appears on the form. */
  include: boolean;
}

export interface FormDefinition {
  /** Qualified "schema.name" of the bound table. */
  table: string;
  /** Form caption. */
  title: string;
  /** Layout columns (1–4): how many fields sit side-by-side. */
  columns: number;
  fields: FormField[];
}

const MAX_LAYOUT_COLUMNS = 4;

/** Lowercased SQL type with size/precision and brackets stripped: `varchar(50)` → `varchar`. */
function baseType(type: string): string {
  return type.toLowerCase().replace(/[[\]]/g, "").replace(/\(.*/, "").trim();
}

/**
 * Picks a sensible control for a column from its SQL type. Conservative: anything
 * unrecognised falls through to a plain text box, which is always editable.
 */
export function inferControlType(col: BuilderColumn): FormControlType {
  const t = baseType(col.type);

  if (/^(bit|bool|boolean)$/.test(t)) return "checkbox";
  // Date-only vs date+time: a bare `date` gets the lighter picker.
  if (t === "date") return "date";
  if (/(datetime|timestamp|smalldatetime)/.test(t)) return "datetime";
  if (/(int|decimal|numeric|float|real|double|money|number|bigint|smallint|tinyint)/.test(t)) {
    return "number";
  }
  // Unbounded / large text → multi-line.
  if (/(^text$|ntext|longtext|mediumtext|clob)/.test(t)) return "textarea";
  return "text";
}

/** `customer_id` / `customerId` → `Customer Id`. Best-effort, deterministic. */
export function humanizeLabel(column: string): string {
  const spaced = column
    .replace(/[_-]+/g, " ")
    .replace(/([a-z\d])([A-Z])/g, "$1 $2")
    .trim();
  return spaced
    .split(/\s+/)
    .filter((w) => w.length > 0)
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join(" ");
}

function unqualifiedName(qualified: string): string {
  const dot = qualified.indexOf(".");
  return dot >= 0 ? qualified.slice(dot + 1) : qualified;
}

/**
 * Generates a form from a table's columns. Every column becomes an included
 * field in schema order; the control is inferred from the type, primary keys
 * are read-only, and NOT NULL columns are marked required.
 */
export function buildFormFromTable(schema: BuilderSchema, qualified: string): FormDefinition {
  const cols = schema.columns.filter((c) => c.table === qualified);
  const fields: FormField[] = cols.map((c) => ({
    column: c.name,
    label: humanizeLabel(c.name),
    control: inferControlType(c),
    readOnly: c.isPrimaryKey,
    required: !c.nullable && !c.isPrimaryKey,
    include: true,
  }));

  return {
    table: qualified,
    title: humanizeLabel(unqualifiedName(qualified)),
    columns: 1,
    fields,
  };
}

function mapField(
  def: FormDefinition,
  column: string,
  fn: (f: FormField) => FormField
): FormDefinition {
  return { ...def, fields: def.fields.map((f) => (f.column === column ? fn(f) : f)) };
}

/** Toggles whether a field appears on the form. */
export function toggleField(def: FormDefinition, column: string): FormDefinition {
  return mapField(def, column, (f) => ({ ...f, include: !f.include }));
}

export function setFieldLabel(def: FormDefinition, column: string, label: string): FormDefinition {
  return mapField(def, column, (f) => ({ ...f, label }));
}

export function setFieldControl(
  def: FormDefinition,
  column: string,
  control: FormControlType
): FormDefinition {
  return mapField(def, column, (f) => ({ ...f, control }));
}

export function setFieldReadOnly(def: FormDefinition, column: string, readOnly: boolean): FormDefinition {
  return mapField(def, column, (f) => ({ ...f, readOnly }));
}

export function setTitle(def: FormDefinition, title: string): FormDefinition {
  return { ...def, title };
}

/** Clamps layout columns into the supported 1–4 range. */
export function setLayoutColumns(def: FormDefinition, columns: number): FormDefinition {
  const clamped = Math.min(MAX_LAYOUT_COLUMNS, Math.max(1, Math.trunc(columns)));
  return { ...def, columns: clamped };
}

/**
 * Moves a field one slot earlier (dir = -1) or later (dir = +1) in the overall
 * field order. Out-of-range moves are no-ops so callers can wire buttons without
 * bounds-checking.
 */
export function moveField(def: FormDefinition, column: string, dir: -1 | 1): FormDefinition {
  const idx = def.fields.findIndex((f) => f.column === column);
  if (idx < 0) return def;
  const target = idx + dir;
  if (target < 0 || target >= def.fields.length) return def;
  const fields = def.fields.slice();
  [fields[idx], fields[target]] = [fields[target], fields[idx]];
  return { ...def, fields };
}

/** The included fields, in form order — what the preview/runtime renders. */
export function visibleFields(def: FormDefinition): FormField[] {
  return def.fields.filter((f) => f.include);
}
