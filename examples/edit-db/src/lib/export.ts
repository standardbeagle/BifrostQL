/**
 * The single, framework-free result-export utility for the shipped edit-db
 * chain. Every export surface — the data grid, the raw SQL console result grid,
 * and any future report runner — serializes through THIS module so there is
 * exactly one CSV/JSON implementation in the shipped path.
 *
 * The serializers are matrix-based (`string[]` headers + `unknown[][]` rows) so
 * they serve both the grid (record rows projected onto ordered fields) and the
 * console (already-columnar positional rows) without a second code path.
 *
 * Value coercion is deliberate about the type classes that have bitten edit-db
 * before:
 *  - NULL vs empty string are distinguishable in CSV (NULL -> empty field,
 *    "" -> a quoted empty pair, the PostgreSQL CSV convention).
 *  - bigint is rendered as its exact decimal string, never coerced through
 *    Number (which silently rounds past 2^53 — a known BigInt-PK bug class).
 *  - Date is rendered as a stable ISO-8601 string.
 */

export type ExportFormat = 'csv' | 'json';
export type JsonMode = 'array' | 'lines';

/** The UTF-8 byte-order mark. Prepending it makes Excel open the CSV as UTF-8. */
export const UTF8_BOM = '﻿';

/** A field that needs RFC4180 quoting: contains `"`, `,`, CR, or LF. */
const NEEDS_QUOTING = /["\n\r,]/;

/**
 * Render one cell value as an RFC4180 CSV field.
 *
 * `null`/`undefined` become a bare empty field; an empty string becomes a
 * quoted empty pair (`""`) so the two stay distinguishable in the output bytes.
 * bigint and Date get exact/stable string forms; objects are JSON-encoded.
 */
export function formatCsvCell(value: unknown): string {
    if (value === null || value === undefined) return '';
    const text = stringifyCell(value);
    if (text === '') return '""';
    if (NEEDS_QUOTING.test(text)) {
        return `"${text.replace(/"/g, '""')}"`;
    }
    return text;
}

/** Stringify a non-null cell value with type-aware, lossless coercion. */
function stringifyCell(value: unknown): string {
    switch (typeof value) {
        case 'string':
            return value;
        case 'bigint':
            return value.toString();
        case 'number':
        case 'boolean':
            return String(value);
        case 'object':
            if (value instanceof Date) return value.toISOString();
            return JSON.stringify(value);
        default:
            return String(value);
    }
}

export interface CsvOptions {
    /** Prepend the UTF-8 BOM so Excel detects the encoding. Default false. */
    bom?: boolean;
}

/**
 * Serialize a header row plus data rows into an RFC4180 CSV string. Rows are
 * joined with CRLF as the RFC specifies. `rows` are positional, aligned to
 * `headers`.
 */
export function buildCsv(
    headers: readonly string[],
    rows: ReadonlyArray<readonly unknown[]>,
    options: CsvOptions = {},
): string {
    const headerLine = headers.map(formatCsvCell).join(',');
    const dataLines = rows.map((row) => row.map(formatCsvCell).join(','));
    const body = [headerLine, ...dataLines].join('\r\n');
    return options.bom ? UTF8_BOM + body : body;
}

/**
 * JSON.stringify replacer that keeps bigint values lossless — bigint is not
 * valid JSON, and stringify throws on it, so emit its exact decimal string.
 * (Date already serializes to ISO-8601 via its own `toJSON`.)
 */
function jsonReplacer(_key: string, value: unknown): unknown {
    return typeof value === 'bigint' ? value.toString() : value;
}

/** Project a positional row onto `{ header: value }` for JSON output. */
function rowToObject(
    headers: readonly string[],
    row: readonly unknown[],
): Record<string, unknown> {
    const obj: Record<string, unknown> = {};
    for (let i = 0; i < headers.length; i++) {
        obj[headers[i]] = row[i];
    }
    return obj;
}

export interface JsonOptions {
    /** `array` (default): a single pretty-printed JSON array. `lines`: NDJSON. */
    mode?: JsonMode;
}

/**
 * Serialize header + rows into JSON, keyed by the header names. `array` mode
 * emits one pretty-printed array; `lines` mode emits newline-delimited JSON
 * (one compact object per line). bigint PKs round-trip losslessly as strings.
 */
export function buildJson(
    headers: readonly string[],
    rows: ReadonlyArray<readonly unknown[]>,
    options: JsonOptions = {},
): string {
    if (options.mode === 'lines') {
        return rows
            .map((row) => JSON.stringify(rowToObject(headers, row), jsonReplacer))
            .join('\n');
    }
    const objects = rows.map((row) => rowToObject(headers, row));
    return JSON.stringify(objects, jsonReplacer, 2);
}

/** One page of the source result set, as returned by the paging fetcher. */
export interface ExportPage {
    /** Positional rows, aligned to the export headers. */
    rows: unknown[][];
    /** The server's total row count for the current filter (drives the loop). */
    total: number;
}

export interface ExportAllOptions {
    headers: string[];
    format: ExportFormat;
    /**
     * Pull one page at the given offset. This is the ONLY data seam — the caller
     * closes over the existing fetcher/query-builder so the current filters and
     * sort are honored, and no new HTTP client is introduced.
     */
    fetchPage: (offset: number, limit: number) => Promise<ExportPage>;
    /** Page size for each fetch. Default 500. */
    pageSize?: number;
    /** Hard stop after this many rows; the result is marked `truncated`. */
    rowCap?: number;
    /** Abort mid-export; the promise rejects and no content is assembled. */
    signal?: AbortSignal;
    /** Called after each page with the running fetched count and the total. */
    onProgress?: (fetched: number, total: number) => void;
    csv?: CsvOptions;
    json?: JsonOptions;
}

export interface ExportResult {
    content: string;
    rowCount: number;
    total: number;
    truncated: boolean;
}

const DEFAULT_PAGE_SIZE = 500;

function throwIfAborted(signal: AbortSignal | undefined): void {
    if (signal?.aborted) {
        throw new DOMException('Export cancelled', 'AbortError');
    }
}

/**
 * Drain the full result set page by page and serialize it once at the end.
 *
 * Cancellation is fail-safe: an aborted signal makes this throw before any
 * content string is built, so the caller — which only writes a file on a
 * resolved result — never produces a partial file. The row cap stops paging
 * early and flags `truncated` so the caller can warn.
 */
export async function exportAllRows(
    options: ExportAllOptions,
): Promise<ExportResult> {
    const pageSize = options.pageSize ?? DEFAULT_PAGE_SIZE;
    const { rowCap, signal } = options;
    const all: unknown[][] = [];
    let total = 0;
    let truncated = false;

    throwIfAborted(signal);

    let offset = 0;
    let known = Number.POSITIVE_INFINITY;
    while (offset < known) {
        throwIfAborted(signal);
        const page = await options.fetchPage(offset, pageSize);
        total = page.total;
        known = page.total;

        for (const row of page.rows) {
            if (rowCap !== undefined && all.length >= rowCap) {
                truncated = true;
                break;
            }
            all.push(row);
        }

        options.onProgress?.(all.length, total);

        if (truncated) break;
        // No forward progress (empty page / lying total) — stop rather than spin.
        if (page.rows.length === 0) break;
        offset += page.rows.length;
    }

    throwIfAborted(signal);

    const content =
        options.format === 'csv'
            ? buildCsv(options.headers, all, options.csv)
            : buildJson(options.headers, all, options.json);

    return { content, rowCount: all.length, total, truncated };
}

/** MIME type for a downloaded file of the given format. */
export function mimeFor(format: ExportFormat): string {
    return format === 'csv'
        ? 'text/csv;charset=utf-8;'
        : 'application/json;charset=utf-8;';
}

/** `<base>-export.<ext>` filename for the given format. */
export function filenameFor(base: string, format: ExportFormat): string {
    return `${base}-export.${format}`;
}

/**
 * Trigger a browser download of `content` as `filename`. Builds a Blob, clicks a
 * transient `<a download>`, then revokes the object URL. No-ops outside a DOM.
 */
export function downloadTextFile(
    content: string,
    filename: string,
    mimeType: string,
): void {
    if (typeof window === 'undefined' || typeof document === 'undefined') return;
    const blob = new Blob([content], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
}
