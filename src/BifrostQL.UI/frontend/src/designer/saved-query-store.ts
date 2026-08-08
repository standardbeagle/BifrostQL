/**
 * The one storage path for saved queries: the server-backed saved-object store,
 * scoped to `type: 'query'`. Both the nav list and the designer pane share this
 * single client so there is no second, divergent persistence route.
 */

import { createSavedObjectsClient, type SavedObjectsClient } from "@standardbeagle/edit-db";
import { newSavedObjectId } from "../lib/saved-object-id";

export const SAVED_QUERY_TYPE = "query" as const;

export const savedQueryStore: SavedObjectsClient = createSavedObjectsClient();

/** A fresh saved-query id. */
export function newQueryId(): string {
  return newSavedObjectId("query");
}
