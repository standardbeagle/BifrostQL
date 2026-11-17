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
 * Most rows one export writes. Matches the server's default `max-query-rows`
 * ceiling (10000): a limit-less query would stop at the 100-row default
 * window, so the export names its window explicitly. When more rows match
 * than came back — this cap, or a lower operator ceiling — the envelope's
 * unpaged `total` says so and the button reports the truncation instead of
 * passing a partial file off as complete.
 */
const EXPORT_ROW_CAP = 10_000;

/** Rows written vs rows matched, when an export did not cover every match. */
interface ExportTruncation {
  exported: number;
  total: number;
}

/**
 * "Export CSV" button that downloads the current screen's data as CSV.
 *
 * On click it runs `queryName` through {@link useBifrostQuery} with the
 * screen's `columns` as the field list and the screen's `filter`/`sort` — so
 * the export goes through the exact same Bifrost path as the on-screen table,
 * and the host's `tenant-filter` and column policy apply automatically. The
 * query asks for up to {@link EXPORT_ROW_CAP} rows and is gated
 * `enabled: false` until the first click, so mounting the button issues no
 * request. When the fetch resolves, the rows are serialized with
 * {@link serializeCsv} (header + RFC-4180 escaping) and handed to
 * {@link downloadCsv}; if the envelope's `total` exceeds the rows received,
 * a notice names both counts.
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
  const [truncation, setTruncation] = useState<ExportTruncation | null>(null);
  // Guards against re-downloading on unrelated re-renders once a fetch resolves.
  const downloadedRef = useRef(false);

  const { data, total, isFetching, isError } = useBifrostQuery<
    Record<string, unknown>[]
  >(queryName, {
    fields: columns.map((column) => column.field),
    filter,
    sort,
    pagination: { limit: EXPORT_ROW_CAP },
    enabled: requested,
  });

  useEffect(() => {
    if (!requested || downloadedRef.current || isFetching || !data) {
      return;
    }
    downloadedRef.current = true;
    downloadCsv(serializeCsv(data, columns), `${fileName}.csv`);
    setTruncation(
      total !== undefined && total > data.length
        ? { exported: data.length, total }
        : null,
    );
    setRequested(false);
  }, [requested, isFetching, data, total, columns, fileName]);

  return (
    <div className="export-button">
      <button
        type="button"
        data-testid={testId}
        disabled={isFetching}
        onClick={() => {
          downloadedRef.current = false;
          setTruncation(null);
          setRequested(true);
        }}
      >
        {isFetching ? 'Exporting…' : 'Export CSV'}
      </button>
      {truncation && (
        <span role="status" data-testid={`${testId}-truncated`}>
          Exported {truncation.exported} of {truncation.total} rows. Narrow
          the filter to export the rest.
        </span>
      )}
      {isError && (
        <span role="alert" data-testid={`${testId}-error`}>
          Export failed.
        </span>
      )}
    </div>
  );
}
