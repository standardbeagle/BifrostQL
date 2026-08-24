import { isBridgeAvailable, sendBridgeRequest } from './native-bridge';

/**
 * The desktop shell's native download channel. `save-file` shows the OS save
 * dialog and writes the text — it drives a native window, so it exists ONLY on
 * the in-process Photino bridge (never the headless HTTP mirror): gate on
 * {@link isNativeSaveAvailable}, not on the shared any-bridge probe.
 */
export function isNativeSaveAvailable(): boolean {
  return isBridgeAvailable();
}

export interface SaveFileResult {
  saved: boolean;
  path?: string;
}

export function saveTextFileNative(
  suggestedName: string,
  content: string,
  title = 'Save file',
): Promise<SaveFileResult> {
  return sendBridgeRequest<SaveFileResult>('save-file', { content, suggestedName, title }, { timeoutMs: 120_000 });
}

export interface TableDdlResult {
  table: string;
  ddl: string;
}

/** CREATE TABLE DDL for a qualified (or bare) table name, in the connection's dialect. */
export function getTableDdl(table: string): Promise<TableDdlResult> {
  return sendBridgeRequest<TableDdlResult>('get-table-ddl', { table });
}
