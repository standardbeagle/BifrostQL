/**
 * localStorage persistence for saved form definitions.
 *
 * Forms are keyed by a stable id so the builder can list, reopen, overwrite and
 * delete them. Mirrors connection/recent-connections.ts: SSR-safe guards, defensive
 * try/catch, and a tolerant load that drops anything that no longer parses.
 */

import type { FormControlType, FormDefinition, FormField } from "./form-state";

const FORMS_KEY = "bifrostql_saved_forms";
const MAX_LAYOUT_COLUMNS = 4;
const FORM_CONTROLS = new Set<FormControlType>([
  "text",
  "textarea",
  "number",
  "checkbox",
  "date",
  "datetime",
  "select",
]);

/** A persisted form: its definition plus list/identity metadata. */
export interface SavedForm {
  id: string;
  name: string;
  /** ISO timestamp of the last save. */
  updatedAt: string;
  definition: FormDefinition;
}

function parseSavedForm(v: unknown): SavedForm | null {
  if (typeof v !== "object" || v === null) return null;
  const f = v as Record<string, unknown>;
  if (
    typeof f.id !== "string" ||
    typeof f.name !== "string" ||
    typeof f.updatedAt !== "string"
  ) {
    return null;
  }

  const definition = parseFormDefinition(f.definition);
  if (!definition) return null;

  return {
    id: f.id,
    name: f.name,
    updatedAt: f.updatedAt,
    definition,
  };
}

export function loadForms(): SavedForm[] {
  if (typeof localStorage === "undefined") return [];
  try {
    const stored = localStorage.getItem(FORMS_KEY);
    if (!stored) return [];
    const parsed: unknown = JSON.parse(stored);
    if (!Array.isArray(parsed)) return [];
    return parsed
      .map(parseSavedForm)
      .filter((form): form is SavedForm => form !== null);
  } catch (error) {
    console.warn("Failed to load saved forms:", error);
    return [];
  }
}

function writeForms(forms: SavedForm[]): void {
  if (typeof localStorage === "undefined") return;
  try {
    localStorage.setItem(FORMS_KEY, JSON.stringify(forms));
  } catch (error) {
    console.warn("Failed to save forms:", error);
  }
}

/**
 * Inserts or replaces a form (by id) and persists. Returns the updated list,
 * most-recently-updated first. `now` is injected so callers/tests stay
 * deterministic (the bridge layer forbids Date.now in some contexts).
 */
export function upsertForm(
  forms: SavedForm[],
  entry: { id: string; name: string; definition: FormDefinition },
  now: string
): SavedForm[] {
  const saved: SavedForm = { ...entry, updatedAt: now };
  const without = forms.filter((f) => f.id !== entry.id);
  const next = [saved, ...without];
  writeForms(next);
  return next;
}

/** Removes a form by id and persists. Returns the updated list. */
export function deleteForm(forms: SavedForm[], id: string): SavedForm[] {
  const next = forms.filter((f) => f.id !== id);
  writeForms(next);
  return next;
}

function parseFormDefinition(value: unknown): FormDefinition | null {
  if (typeof value !== "object" || value === null) return null;

  const def = value as Record<string, unknown>;
  if (
    typeof def.table !== "string" ||
    typeof def.title !== "string" ||
    !Array.isArray(def.fields)
  ) {
    return null;
  }

  const fields = def.fields
    .map(parseFormField)
    .filter((field): field is FormField => field !== null);

  return {
    table: def.table,
    title: def.title,
    columns: parseLayoutColumns(def.columns),
    fields,
  };
}

function parseFormField(value: unknown): FormField | null {
  if (typeof value !== "object" || value === null) return null;

  const field = value as Record<string, unknown>;
  if (typeof field.column !== "string") return null;

  return {
    column: field.column,
    label: typeof field.label === "string" ? field.label : field.column,
    control: parseControl(field.control),
    readOnly: field.readOnly === true,
    required: field.required === true,
    include: field.include !== false,
  };
}

function parseLayoutColumns(value: unknown): number {
  if (typeof value !== "number" || !Number.isFinite(value)) return 1;
  return Math.min(MAX_LAYOUT_COLUMNS, Math.max(1, Math.trunc(value)));
}

function parseControl(value: unknown): FormControlType {
  return typeof value === "string" && FORM_CONTROLS.has(value as FormControlType)
    ? (value as FormControlType)
    : "text";
}
