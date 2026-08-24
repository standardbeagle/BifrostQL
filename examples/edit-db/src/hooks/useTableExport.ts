import { useCallback } from 'react';
import { useFetcher } from '../common/fetcher';
import { useSchema } from './useSchema';
import type { Table } from '../types/schema';
import { buildQuery, exportableColumns } from '../lib/query-builder';
import {
    DEFAULT_ROW_CAP,
    exportAllRows,
    filenameFor,
    mimeFor,
    type ExportFormat,
    type ExportResult,
} from '../lib/export';
import { useSaveFile } from './useSaveFile';

/**
 * Downloads a whole table WITHOUT opening it: the navigation-level counterpart
 * of the grid toolbar's export. Reuses the grid's export projection
 * (`buildQuery` with `fields: 'export'` — long text included, non-PK binary
 * excluded, no join blocks), the shared page-draining serializer, and the same
 * browser download path, so the two exports cannot drift. Unfiltered by design:
 * a filtered export belongs to the grid, where the filters live.
 * <see cref="DEFAULT_ROW_CAP"/> bounds the drain; the result reports
 * `truncated` so the caller can warn instead of silently shipping a partial file.
 */
export function useTableExport(): (table: Table, format: ExportFormat) => Promise<ExportResult> {
    const schema = useSchema();
    const fetcher = useFetcher();
    const saveFile = useSaveFile();

    return useCallback(async (table: Table, format: ExportFormat) => {
        const query = buildQuery(table, schema, '', [], undefined, undefined, undefined, { fields: 'export' });
        if (!query) throw new Error(`Cannot export '${table.label ?? table.name}': no export query could be built.`);
        const columns = exportableColumns(table);

        const result = await exportAllRows({
            format,
            headers: columns.map((c) => c.label ?? c.name),
            csv: { bom: true },
            rowCap: DEFAULT_ROW_CAP,
            fetchPage: async (offset, limit) => {
                const res = await fetcher.query<Record<string, { total: number; data: Record<string, unknown>[] }>>(
                    query, { limit, offset });
                const page = res?.[table.name];
                const records = page?.data ?? [];
                return {
                    rows: records.map((r) => columns.map((c) => r[c.name])),
                    total: page?.total ?? records.length,
                };
            },
        });
        await saveFile(filenameFor(table.name, format), result.content, mimeFor(format));
        return result;
    }, [schema, fetcher, saveFile]);
}
