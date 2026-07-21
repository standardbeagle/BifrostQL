import { createSavedObjectsClient, type SavedObject, type SavedObjectsClient } from "@standardbeagle/edit-db";
import { parseChartDefinition, type ChartDefinition } from "./chart-model";

export const chartStore = createSavedObjectsClient();
/** Charts are query sub-kind objects, preserving the single saved-object schema. */
export const CHART_SAVED_OBJECT_TYPE = "query" as const;

export async function saveChart(client: SavedObjectsClient, object: Omit<SavedObject, "type"> & { definition: ChartDefinition }): Promise<SavedObject> {
  if (!parseChartDefinition(object.definition)) throw new Error("Invalid chart definition.");
  return client.put({ ...object, type: CHART_SAVED_OBJECT_TYPE });
}
export function openChart(object: SavedObject): ChartDefinition | null {
  return object.type === CHART_SAVED_OBJECT_TYPE ? parseChartDefinition(object.definition) : null;
}
