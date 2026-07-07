import { useCallback, useRef } from 'react';
import {
  rowsToCsv,
  rowsToJson,
  rowsToTsv,
  triggerDownload,
} from '../../utils/table-export';
import type {
  ColumnConfig,
  ExportConfig,
  ExportFormat,
  ExportState,
} from '../use-bifrost-table.types';

export interface UseTableExportOptions {
  columnOrder: string[];
  visibleColumns: string[];
  columns: ColumnConfig[];
  data: Record<string, unknown>[];
  exportConfig: ExportConfig | undefined;
  table: string;
}

/** Derives export actions (CSV, Excel/TSV, JSON, clipboard) from the visible,
 * ordered columns and the current data. */
export function useTableExport({
  columnOrder,
  visibleColumns,
  columns,
  data,
  exportConfig,
  table,
}: UseTableExportOptions): ExportState {
  const getExportFields = useCallback((): {
    fields: string[];
    headers: string[];
  } => {
    const orderedVisible = columnOrder.filter((f) =>
      visibleColumns.includes(f),
    );
    const headers = orderedVisible.map((f) => {
      const col = columns.find((c) => c.field === f);
      return col?.header ?? f;
    });
    return { fields: orderedVisible, headers };
  }, [columnOrder, visibleColumns, columns]);

  // The table uses server-side pagination, so this hook only ever holds the
  // current page's rows — it has no fetcher to pull other pages. `allPages`
  // therefore cannot be honored; every export uses the loaded rows. Warn once
  // so the flag doesn't silently no-op, and export what we have.
  const warnedAllPagesRef = useRef(false);
  const getExportRows = useCallback(
    (allPages?: boolean): Record<string, unknown>[] => {
      if (allPages && !warnedAllPagesRef.current) {
        warnedAllPagesRef.current = true;
        console.warn(
          '[bifrost] Export: `allPages` is not supported because the table ' +
            'loads one page at a time via server-side pagination. Only the ' +
            'currently loaded rows were exported. Raise the page size or fetch ' +
            'the full result set separately to export everything.',
        );
      }
      return data;
    },
    [data],
  );

  const exportCsv = useCallback(
    (allPages?: boolean) => {
      const { fields: exportFields, headers } = getExportFields();
      const rows = getExportRows(allPages);
      const csv = rowsToCsv(
        rows,
        exportFields,
        headers,
        exportConfig?.formatters,
      );
      const filename = `${exportConfig?.filename ?? table}-export.csv`;
      triggerDownload(csv, filename, 'text/csv;charset=utf-8;');
    },
    [getExportFields, getExportRows, exportConfig, table],
  );

  const exportExcel = useCallback(
    (allPages?: boolean) => {
      const { fields: exportFields, headers } = getExportFields();
      const rows = getExportRows(allPages);
      const tsv = rowsToTsv(
        rows,
        exportFields,
        headers,
        exportConfig?.formatters,
      );
      const filename = `${exportConfig?.filename ?? table}-export.xls`;
      triggerDownload(tsv, filename, 'application/vnd.ms-excel');
    },
    [getExportFields, getExportRows, exportConfig, table],
  );

  const exportJson = useCallback(
    (allPages?: boolean) => {
      const { fields: exportFields } = getExportFields();
      const rows = getExportRows(allPages);
      const json = rowsToJson(rows, exportFields, exportConfig?.formatters);
      const filename = `${exportConfig?.filename ?? table}-export.json`;
      triggerDownload(json, filename, 'application/json');
    },
    [getExportFields, getExportRows, exportConfig, table],
  );

  const copyToClipboard = useCallback(
    async (allPages?: boolean): Promise<void> => {
      if (typeof navigator === 'undefined' || !navigator.clipboard) return;
      const { fields: exportFields, headers } = getExportFields();
      const rows = getExportRows(allPages);
      const tsv = rowsToTsv(
        rows,
        exportFields,
        headers,
        exportConfig?.formatters,
      );
      await navigator.clipboard.writeText(tsv);
    },
    [getExportFields, getExportRows, exportConfig],
  );

  const downloadFile = useCallback(
    (format: ExportFormat, allPages?: boolean) => {
      switch (format) {
        case 'csv':
          exportCsv(allPages);
          break;
        case 'excel':
          exportExcel(allPages);
          break;
        case 'json':
          exportJson(allPages);
          break;
      }
    },
    [exportCsv, exportExcel, exportJson],
  );

  return {
    exportCsv,
    exportExcel,
    exportJson,
    copyToClipboard,
    downloadFile,
  };
}
