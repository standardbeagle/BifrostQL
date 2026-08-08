/**
 * One-time bootstrap: import forms that pre-exist in browser localStorage
 * (`bifrostql_saved_forms`, see forms-storage.ts) into the server-backed
 * saved-object store as `type: 'form'` objects.
 *
 * NOTE this is a bootstrap, not a retirement of localStorage: FormBuilderPane
 * still writes NEW forms only to localStorage, and the once-per-browser flag
 * below means forms saved after this migration ran are never migrated. The
 * localStorage path (forms-storage.ts) remains the live write path for forms;
 * only saved queries write to the server store today.
 *
 * Idempotent: guarded by a localStorage flag so it runs at most once per browser,
 * and it skips any form already present on the server (never clobbers a newer
 * server copy). Errors are surfaced to the caller, not swallowed here — the boot
 * wrapper decides whether to set the done-flag.
 */

import {
  SavedObjectConflictError,
  type SavedObject,
  type SavedObjectsClient,
} from "@standardbeagle/edit-db";
import { loadForms } from "./forms-storage";

export const FORMS_MIGRATION_FLAG = "bifrostql_forms_migrated_to_saved_objects";

export interface FormsMigrationResult {
  imported: number;
  skipped: number;
  alreadyDone: boolean;
}

/**
 * Imports every locally-stored form that the server does not already have. A form
 * already on the server (by id) is skipped, not overwritten. Pass `force: true` to
 * re-run past the once-per-browser guard (tests/manual re-sync).
 */
export async function migrateFormsToSavedObjects(
  client: SavedObjectsClient,
  opts: { force?: boolean } = {}
): Promise<FormsMigrationResult> {
  const done = typeof localStorage !== "undefined" && localStorage.getItem(FORMS_MIGRATION_FLAG) === "true";
  if (done && !opts.force) {
    return { imported: 0, skipped: 0, alreadyDone: true };
  }

  const forms = loadForms();
  let imported = 0;
  let skipped = 0;

  for (const form of forms) {
    const existing = await client.get("form", form.id);
    if (existing !== null) {
      skipped++;
      continue;
    }
    const object: SavedObject = {
      id: form.id,
      type: "form",
      name: form.name,
      definition: form.definition,
      version: 0,
    };
    try {
      await client.put(object);
      imported++;
    } catch (error) {
      // A concurrent create (another tab/device) means it now exists — treat as skip.
      if (error instanceof SavedObjectConflictError) {
        skipped++;
        continue;
      }
      throw error;
    }
  }

  if (typeof localStorage !== "undefined") {
    localStorage.setItem(FORMS_MIGRATION_FLAG, "true");
  }
  return { imported, skipped, alreadyDone: false };
}
