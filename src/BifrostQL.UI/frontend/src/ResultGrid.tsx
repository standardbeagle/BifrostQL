/**
 * Shared columnar result grid, used by both the raw SQL console and the visual
 * query builder's Run output. Takes the same `{columns, rows}` shape produced by
 * the exec-sql and build-and-exec bridge handlers.
 */

export interface ResultColumn {
  name: string;
  type: string;
}

export function ResultGrid({
  columns,
  rows,
}: {
  columns: ResultColumn[];
  rows: unknown[][];
}) {
  return (
    <div className="result-grid" style={styles.gridWrap}>
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
          {rows.map((row, r) => (
            <tr key={r}>
              {row.map((cell, c) => (
                <td key={c} style={styles.td}>
                  {renderCell(cell)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function renderCell(value: unknown): string {
  if (value === null || value === undefined) return "NULL";
  if (typeof value === "object") return JSON.stringify(value);
  return String(value);
}

const styles: Record<string, React.CSSProperties> = {
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
};
