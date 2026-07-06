/**
 * @module lib/label-match
 *
 * Client-side label filter shared by FK dropdowns (`ParentField`) and the
 * many-to-many `TargetPicker` fallback. Used when the label column is not
 * String-typed, so the server-side `_contains` search is unavailable and rows
 * are filtered within the fetched window instead.
 */

/**
 * Case-insensitive substring match against a row's `label`, falling back to its
 * id field (`idKey`) when no label was fetched.
 */
export function matchesLabel(row: object, idKey: string, term: string): boolean {
    const rec = row as Record<string, unknown>;
    return String(rec.label ?? rec[idKey] ?? '').toLowerCase().includes(term.toLowerCase());
}
