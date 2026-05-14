/**
 * Client-side CSV export for already-fetched, policy-filtered table data.
 *
 * The serializer takes the *result of a Bifrost query* — rows the host has
 * already tenant-scoped and column-policy-filtered — plus the visible column
 * list, and produces an RFC-4180 CSV string. It never touches the database and
 * never widens the column set, so whatever the Bifrost path was allowed to
 * return is exactly what lands in the file.
 */

import type { ColumnConfig } from '@bifrostql/react';

/** A field that needs RFC-4180 quoting: contains `"`, `,`, CR, or LF. */
const NEEDS_QUOTING = /["\n\r,]/;

/**
 * Render one cell value as an RFC-4180 field.
 *
 * `null`/`undefined` become an empty field. Every other value is stringified;
 * if it contains a comma, double-quote, or line break it is wrapped in double
 * quotes with embedded quotes doubled.
 */
function toCsvField(value: unknown): string {
  if (value === null || value === undefined) {
    return '';
  }
  const text = String(value);
  if (NEEDS_QUOTING.test(text)) {
    return `"${text.replace(/"/g, '""')}"`;
  }
  return text;
}

/**
 * Serialize `rows` into an RFC-4180 CSV string with a header row.
 *
 * The header is each column's `header`; each data row emits the columns in
 * `columns` order, reading `row[column.field]`. Rows are joined with CRLF, as
 * RFC-4180 specifies. `columns` should already be the policy-gated column list
 * (e.g. finance columns dropped for non-finance sessions) — this function does
 * no gating of its own.
 *
 * @param rows - Already-fetched, policy-filtered row records.
 * @param columns - The visible columns, in display order.
 */
export function serializeCsv(
  rows: ReadonlyArray<Record<string, unknown>>,
  columns: ReadonlyArray<ColumnConfig>,
): string {
  const header = columns.map((column) => toCsvField(column.header)).join(',');
  const body = rows.map((row) =>
    columns.map((column) => toCsvField(row[column.field])).join(','),
  );
  return [header, ...body].join('\r\n');
}

/**
 * Trigger a browser download of `csv` as a file named `filename`.
 *
 * Builds a `text/csv` blob, attaches it to a transient `<a download>`, clicks
 * it, then revokes the object URL. No-op-safe to call from a click handler.
 *
 * @param csv - The CSV text to download.
 * @param filename - The suggested file name (e.g. `members.csv`).
 */
export function downloadCsv(csv: string, filename: string): void {
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
}
