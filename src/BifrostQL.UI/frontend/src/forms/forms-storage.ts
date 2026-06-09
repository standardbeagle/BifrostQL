/**
 * localStorage persistence for saved form definitions.
 *
 * Forms are keyed by a stable id so the builder can list, reopen, overwrite and
 * delete them. Mirrors connection/recent-connections.ts: SSR-safe guards, defensive
 * try/catch, and a tolerant load that drops anything that no longer parses.
 */

import type { FormDefinition } from "./form-state";

const FORMS_KEY = "bifrostql_saved_forms";

/** A persisted form: its definition plus list/identity metadata. */
export interface SavedForm {
  id: string;
  name: string;
  /** ISO timestamp of the last save. */
  updatedAt: string;
  definition: FormDefinition;
}

function isSavedForm(v: unknown): v is SavedForm {
  if (typeof v !== "object" || v === null) return false;
  const f = v as Record<string, unknown>;
  return (
    typeof f.id === "string" &&
    typeof f.name === "string" &&
    typeof f.updatedAt === "string" &&
    typeof f.definition === "object" &&
    f.definition !== null
  );
}

export function loadForms(): SavedForm[] {
  if (typeof localStorage === "undefined") return [];
  try {
    const stored = localStorage.getItem(FORMS_KEY);
    if (!stored) return [];
    const parsed: unknown = JSON.parse(stored);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(isSavedForm);
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
