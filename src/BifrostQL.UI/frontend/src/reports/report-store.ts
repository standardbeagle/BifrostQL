import { createSavedObjectsClient, type SavedObject, type SavedObjectsClient } from '@standardbeagle/edit-db';
import { parseReportDefinition, type ReportDefinition } from './report-definition';

export const reportStore = createSavedObjectsClient();
export const REPORT_TYPE = 'report' as const;

export async function saveReport(client: SavedObjectsClient, object: Omit<SavedObject, 'type'> & { definition: ReportDefinition }): Promise<SavedObject> {
  if (!parseReportDefinition(object.definition)) throw new Error('Invalid report definition.');
  return client.put({ ...object, type: REPORT_TYPE });
}

export function openReport(object: SavedObject): ReportDefinition | null {
  return object.type === REPORT_TYPE ? parseReportDefinition(object.definition) : null;
}
