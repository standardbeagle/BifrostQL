/**
 * Shared id-minting for saved objects (queries, forms, …). One helper so every
 * surface mints ids the same way instead of re-inlining the randomUUID fallback.
 */

/** A fresh saved-object id. Falls back when `crypto.randomUUID` is unavailable. */
export function newSavedObjectId(prefix: string): string {
  return crypto.randomUUID?.() ?? `${prefix}-${Date.now()}`;
}
