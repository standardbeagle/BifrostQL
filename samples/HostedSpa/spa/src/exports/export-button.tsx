import { useEffect, useRef, useState } from 'react';
import { useBifrostQuery } from '@bifrostql/react';
import type { ColumnConfig, TableFilter, SortOption } from '@bifrostql/react';
import { serializeCsv, downloadCsv } from './csv-export';

/** Props for {@link ExportButton}. */
export interface ExportButtonProps {
  /**
   * BifrostQL GraphQL query field name to export — the same `query` name the
   * screen's `BifrostTable` is bound to (e.g. `members`, `main_dues_invoices`).
   */
  queryName: string;
  /**
   * The screen's visible columns, in display order. This MUST be the
   * already-policy-gated column list (finance columns dropped for non-finance
   * sessions) — the export selects exactly these fields and no others, so the
   * file can never contain a column the screen itself hid.
   */
  columns: ColumnConfig[];
  /**
   * The screen's active server-side filter — ad-hoc filter controls layered
   * with the selected saved view on MemberList, or the report's fixed filter.
   * Forwarded to the Bifrost query so tenant-filter + policy-filter apply
   * server-side, exactly as they do for the on-screen table.
   */
  filter?: TableFilter;
  /** The screen's active server-side sort, forwarded to the Bifrost query. */
  sort?: SortOption[];
  /** Base name (without extension) for the downloaded file. */
  fileName: string;
  /** Stable test id for the button. */
  testId?: string;
}

/**
 * "Export CSV" button that downloads the current screen's data as CSV.
 *
 * On click it runs `queryName` through {@link useBifrostQuery} with the
 * screen's `columns` as the field list and the screen's `filter`/`sort` — so
 * the export goes through the exact same Bifrost path as the on-screen table,
 * and the host's `tenant-filter` and column policy apply automatically. The
 * query is gated `enabled: false` until the first click, so mounting the
 * button issues no request. When the fetch resolves, the rows are serialized
 * with {@link serializeCsv} (header + RFC-4180 escaping) and handed to
 * {@link downloadCsv}.
 *
 * Because the export only ever names the columns the screen passes in — which
 * the finance screens have already gated via `finance-fields.ts` — a
 * non-finance session can neither query nor export a finance column.
 *
 * Must be mounted within a `BifrostProvider` (typically via `AppShellProvider`).
 */
export function ExportButton({
  queryName,
  columns,
  filter,
  sort,
  fileName,
  testId = 'export-button',
}: ExportButtonProps) {
  const [requested, setRequested] = useState(false);
  // Guards against re-downloading on unrelated re-renders once a fetch resolves.
  const downloadedRef = useRef(false);

  const { data, isFetching, isError } = useBifrostQuery<
    Record<string, unknown>[]
  >(queryName, {
    fields: columns.map((column) => column.field),
    filter,
    sort,
    enabled: requested,
  });

  useEffect(() => {
    if (!requested || downloadedRef.current || isFetching || !data) {
      return;
    }
    downloadedRef.current = true;
    downloadCsv(serializeCsv(data, columns), `${fileName}.csv`);
    setRequested(false);
  }, [requested, isFetching, data, columns, fileName]);

  return (
    <div className="export-button">
      <button
        type="button"
        data-testid={testId}
        disabled={isFetching}
        onClick={() => {
          downloadedRef.current = false;
          setRequested(true);
        }}
      >
        {isFetching ? 'Exporting…' : 'Export CSV'}
      </button>
      {isError && (
        <span role="alert" data-testid={`${testId}-error`}>
          Export failed.
        </span>
      )}
    </div>
  );
}
