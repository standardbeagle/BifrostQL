import { createSavedObjectsClient, type SavedObject, type SavedObjectsClient } from "@standardbeagle/edit-db";
import { parseDashboardDefinition, type DashboardDefinition } from "./dashboard-model";

export const DASHBOARD_SAVED_OBJECT_TYPE = "dashboard" as const;
export const dashboardStore = createSavedObjectsClient();

export function openDashboard(object: SavedObject): DashboardDefinition | null {
  return object.type === DASHBOARD_SAVED_OBJECT_TYPE ? parseDashboardDefinition(object.definition) : null;
}

export async function saveDashboard(client: SavedObjectsClient, object: Omit<SavedObject, "type"> & { definition: DashboardDefinition }): Promise<SavedObject> {
  if (!parseDashboardDefinition(object.definition)) throw new Error("Invalid dashboard definition.");
  return client.put({ ...object, type: DASHBOARD_SAVED_OBJECT_TYPE });
}
