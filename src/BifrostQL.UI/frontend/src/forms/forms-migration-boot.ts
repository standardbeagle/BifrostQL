/**
 * Boot wrapper for the one-time forms → saved-object migration. Runs once the app
 * is connected to a server (which is when the /_saved-objects endpoint exists) and
 * swallows any error — a server without the endpoint wired, or an offline desktop
 * shell, must never break the editor. The migration itself is idempotent and only
 * marks itself done on success, so a transient failure simply retries next boot.
 */

import { createSavedObjectsClient } from "@standardbeagle/edit-db";
import { migrateFormsToSavedObjects } from "./forms-migration";

export async function runFormsMigrationOnce(baseUrl = ""): Promise<void> {
  try {
    const result = await migrateFormsToSavedObjects(createSavedObjectsClient(baseUrl));
    if (result.imported > 0) {
      console.info(`Imported ${result.imported} local form(s) into the saved-object store.`);
    }
  } catch (error) {
    console.warn("Forms migration to the saved-object store was skipped:", error);
  }
}
