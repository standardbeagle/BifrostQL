/**
 * Shared columnar result grid, used by both the raw SQL console and the visual
 * query builder's Run output. Takes the same `{columns, rows}` shape produced by
 * the exec-sql and build-and-exec bridge handlers.
 *
 * Rendering is windowed: only the rows currently scrolled into view (plus a
 * small overscan) are mounted. Without this, a query with a large `maxRows`
 * (the bridge default is 1000, but callers can request more) would render
 * every row/cell up front and jank on mount and on every scroll/resize.
 * There is no virtualization dependency in this package, so this is a
 * hand-rolled fixed-row-height window rather than e.g. @tanstack/react-virtual.
 */
import { useRef, useState, useCallback, useLayoutEffect } from "react";
import {
  buildCsv,
  buildJson,
  downloadTextFile,
  filenameFor,
  mimeFor,
  type ExportFormat,
} from "@standardbeagle/edit-db";

export interface ResultColumn {
  name: string;
  type: string;
}

/** Header text per column, naming unnamed columns positionally (col1, col2, …). */
function exportHeaders(columns: ResultColumn[]): string[] {
  return columns.map((col, i) => col.name || `col${i + 1}`);
}

// Matches the padding/font-size in `styles.td` below closely enough for a
// stable estimate; rows don't wrap (whiteSpace: "nowrap"), so a fixed height
// is accurate.
const ROW_HEIGHT_PX = 25;
// Extra rows rendered above/below the visible window so fast scrolling
// doesn't flash empty space before the next paint catches up.
const OVERSCAN_ROWS = 10;

export function ResultGrid({
  columns,
  rows,
}: {
  columns: ResultColumn[];
  rows: unknown[][];
}) {
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const [viewportHeight, setViewportHeight] = useState(0);
  const [scrollTop, setScrollTop] = useState(0);

  // Export the whole in-memory result (already fully materialized here — no
  // paging) through the shared edit-db util, so the console reuses the one
  // serializer rather than carrying a second CSV/JSON implementation.
  const handleExport = useCallback(
    (format: ExportFormat) => {
      const headers = exportHeaders(columns);
      const content =
        format === "csv"
          ? buildCsv(headers, rows, { bom: true })
          : buildJson(headers, rows);
      downloadTextFile(content, filenameFor("query-result", format), mimeFor(format));
    },
    [columns, rows],
  );
  const hasRows = rows.length > 0;

  const handleScroll = useCallback(() => {
    if (scrollRef.current) setScrollTop(scrollRef.current.scrollTop);
  }, []);

  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    setViewportHeight(el.clientHeight);
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (entry) setViewportHeight(entry.contentRect.height);
    });
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const rowCount = rows.length;
  const totalHeight = rowCount * ROW_HEIGHT_PX;

  // Fall back to rendering everything until the viewport has been measured
  // (e.g. first paint), rather than showing a zero-height grid.
  const hasMeasured = viewportHeight > 0;
  const firstVisible = hasMeasured
    ? Math.max(0, Math.floor(scrollTop / ROW_HEIGHT_PX) - OVERSCAN_ROWS)
    : 0;
  const visibleCount = hasMeasured
    ? Math.ceil(viewportHeight / ROW_HEIGHT_PX) + OVERSCAN_ROWS * 2
    : rowCount;
  const lastVisible = hasMeasured
    ? Math.min(rowCount, firstVisible + visibleCount)
    : rowCount;

  const topSpacerHeight = firstVisible * ROW_HEIGHT_PX;
  const bottomSpacerHeight = Math.max(0, totalHeight - lastVisible * ROW_HEIGHT_PX);

  const visibleRows = rows.slice(firstVisible, lastVisible);

  return (
    <div style={styles.wrap}>
      <div style={styles.toolbar}>
        <button
          type="button"
          style={styles.exportBtn}
          onClick={() => handleExport("csv")}
          disabled={!hasRows}
        >
          Export CSV
        </button>
        <button
          type="button"
          style={styles.exportBtn}
          onClick={() => handleExport("json")}
          disabled={!hasRows}
        >
          Export JSON
        </button>
      </div>
      <div
        className="result-grid"
        style={styles.gridWrap}
        ref={scrollRef}
        onScroll={handleScroll}
      >
      <table style={styles.table}>
        <thead>
          <tr>
            {columns.map((col, i) => (
              <th key={i} style={styles.th} title={col.type}>
                {col.name || `col${i + 1}`}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {topSpacerHeight > 0 && (
            <tr aria-hidden="true">
              <td colSpan={columns.length || 1} style={{ ...styles.spacer, height: topSpacerHeight }} />
            </tr>
          )}
          {visibleRows.map((row, i) => {
            const r = firstVisible + i;
            return (
              <tr key={r}>
                {row.map((cell, c) => (
                  <td key={c} style={styles.td}>
                    {renderCell(cell)}
                  </td>
                ))}
              </tr>
            );
          })}
          {bottomSpacerHeight > 0 && (
            <tr aria-hidden="true">
              <td colSpan={columns.length || 1} style={{ ...styles.spacer, height: bottomSpacerHeight }} />
            </tr>
          )}
        </tbody>
      </table>
      </div>
    </div>
  );
}

export function renderCell(value: unknown): string {
  if (value === null || value === undefined) return "NULL";
  if (typeof value === "object") return JSON.stringify(value);
  return String(value);
}

const styles: Record<string, React.CSSProperties> = {
  wrap: { display: "flex", flexDirection: "column", flex: 1, minHeight: 0 },
  toolbar: { display: "flex", gap: 6, justifyContent: "flex-end", padding: "4px 0" },
  exportBtn: {
    fontSize: 12,
    padding: "3px 10px",
    borderRadius: 4,
    border: "1px solid var(--void-border, #d1d5db)",
    background: "var(--void-surface, transparent)",
    color: "var(--text-primary, inherit)",
    cursor: "pointer",
  },
  gridWrap: { flex: 1, minHeight: 0, overflow: "auto", border: "1px solid var(--border, #d1d5db)", borderRadius: 6 },
  table: { borderCollapse: "collapse", width: "100%", fontSize: 13 },
  th: {
    position: "sticky",
    top: 0,
    background: "#f3f4f6",
    textAlign: "left",
    padding: "6px 10px",
    borderBottom: "1px solid #d1d5db",
    whiteSpace: "nowrap",
  },
  td: {
    padding: "4px 10px",
    borderBottom: "1px solid #f0f0f0",
    whiteSpace: "nowrap",
    fontFamily: "var(--font-mono, monospace)",
  },
  spacer: {
    padding: 0,
    border: "none",
  },
};
