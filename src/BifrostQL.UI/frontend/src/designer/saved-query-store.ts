/**
 * The one storage path for saved queries: the server-backed saved-object store,
 * scoped to `type: 'query'`. Both the nav list and the designer pane share this
 * single client so there is no second, divergent persistence route.
 */

import {
  createSavedObjectsClient,
  type SavedObject,
  type SavedObjectsClient,
} from "@standardbeagle/edit-db";
import { newSavedObjectId } from "../lib/saved-object-id";

export const SAVED_QUERY_TYPE = "query" as const;

export const savedQueryStore: SavedObjectsClient = createSavedObjectsClient();

/** A fresh saved-query id. */
export function newQueryId(): string {
  return newSavedObjectId("query");
}

/** True for the rejection a caller gets when it aborts its own request. */
export function isAbortError(cause: unknown): boolean {
  return cause instanceof DOMException
    ? cause.name === "AbortError"
    : cause instanceof Error && cause.name === "AbortError";
}

/**
 * Turns a saved-query load failure into a sentence a user can act on.
 *
 * A `SyntaxError` here is the browser's own JSON-parse text — WebKit says
 * "The string did not match the expected pattern.", Chromium says
 * "Unexpected token '<'". Both are meaningless to a user and neither names what
 * failed, so they are replaced rather than forwarded. Everything else already
 * carries a server-authored reason (a status line, a store error), which is kept
 * verbatim so the real failure is never swallowed.
 */
export function describeSavedQueryLoadFailure(cause: unknown): string {
  if (cause instanceof SyntaxError) {
    return (
      "Saved queries could not be read: the server's reply was not saved-query data. " +
      "The saved-object endpoint may be unavailable on this server."
    );
  }
  const detail = cause instanceof Error ? cause.message : String(cause);
  return `Saved queries could not be loaded. ${detail}`;
}

/**
 * Lists the saved queries, mapping any failure onto
 * {@link describeSavedQueryLoadFailure}. Aborts propagate untouched so a caller
 * that cancelled its own request can tell that apart from a real failure.
 */
export async function listSavedQueries(signal?: AbortSignal): Promise<SavedObject[]> {
  try {
    return await savedQueryStore.list(SAVED_QUERY_TYPE, signal);
  } catch (cause) {
    if (isAbortError(cause)) throw cause;
    const error = new Error(describeSavedQueryLoadFailure(cause));
    (error as Error & { cause?: unknown }).cause = cause;
    throw error;
  }
}
