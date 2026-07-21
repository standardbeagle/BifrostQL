import { createSavedObjectsClient, type SavedObject, type SavedObjectsClient } from "@standardbeagle/edit-db";
import { parsePivotDefinition, type PivotDefinition } from "./pivot-model";

export const pivotStore = createSavedObjectsClient();
export const PIVOT_SAVED_OBJECT_TYPE = "query" as const;

export async function savePivot(client: SavedObjectsClient, object: Omit<SavedObject, "type"> & { definition: PivotDefinition }): Promise<SavedObject> {
  if (!parsePivotDefinition(object.definition)) throw new Error("Invalid pivot definition.");
  return client.put({ ...object, type: PIVOT_SAVED_OBJECT_TYPE });
}
export function openPivot(object: SavedObject): PivotDefinition | null {
  return object.type === PIVOT_SAVED_OBJECT_TYPE ? parsePivotDefinition(object.definition) : null;
}
