import type { VisualFilterOperator, VisualSort } from "../lib/visual-query";
import {
  type DesignerState,
  type PlacedColumn,
  parseCriterionValue,
} from "./designer-state";

const OPERATORS: VisualFilterOperator[] = [
  "_eq", "_neq", "_lt", "_lte", "_gt", "_gte", "_contains", "_in", "_between", "_null",
];

/**
 * The Access-style bottom grid, transposed: one column per selected field, rows
 * for Field, Table, Sort, Show, then a Criteria row and N Or rows. Editing a
 * cell drives the designer state, which derives the VisualFilter tree + sort.
 */
export function CriteriaGrid({
  state,
  onSetSort,
  onToggleShow,
  onSetCriterion,
}: {
  state: DesignerState;
  onSetSort: (ref: string, column: string, sort: VisualSort) => void;
  onToggleShow: (ref: string, column: string) => void;
  onSetCriterion: (ref: string, column: string, orIndex: number, operator: VisualFilterOperator, text: string) => void;
}) {
  const cols = state.columns;
  if (cols.length === 0) {
    return <div className="qbe-grid qbe-grid--empty" style={styles.empty}>Select columns to set sorting and criteria.</div>;
  }

  // Always offer one empty Or row beyond the deepest existing criteria row.
  const maxCriteria = cols.reduce((m, c) => Math.max(m, c.criteria.length), 0);
  const orRowCount = Math.max(1, maxCriteria) + 1;

  return (
    <div className="qbe-grid" style={styles.wrap}>
      <table style={styles.table}>
        <thead>
          <tr>
            <th style={styles.rowHead} />
            {cols.map((c, i) => (
              <th key={i} style={styles.colHead}>{c.column}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          <tr>
            <td style={styles.rowHead}>Table</td>
            {cols.map((c, i) => <td key={i} style={styles.cell}>{c.tableRef}</td>)}
          </tr>
          <tr>
            <td style={styles.rowHead}>Sort</td>
            {cols.map((c, i) => (
              <td key={i} style={styles.cell}>
                <select
                  value={c.sort}
                  onChange={(e) => onSetSort(c.tableRef, c.column, e.target.value as VisualSort)}
                  aria-label={`Sort ${c.column}`}
                >
                  <option value="none">—</option>
                  <option value="asc">Asc</option>
                  <option value="desc">Desc</option>
                </select>
              </td>
            ))}
          </tr>
          <tr>
            <td style={styles.rowHead}>Show</td>
            {cols.map((c, i) => (
              <td key={i} style={styles.cell}>
                <input type="checkbox" checked={c.show} onChange={() => onToggleShow(c.tableRef, c.column)} aria-label={`Show ${c.column}`} />
              </td>
            ))}
          </tr>
          {Array.from({ length: orRowCount }).map((_, orIndex) => (
            <tr key={orIndex}>
              <td style={styles.rowHead}>{orIndex === 0 ? "Criteria" : "or"}</td>
              {cols.map((c, i) => (
                <CriterionCell key={i} column={c} orIndex={orIndex} onSetCriterion={onSetCriterion} />
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function CriterionCell({
  column,
  orIndex,
  onSetCriterion,
}: {
  column: PlacedColumn;
  orIndex: number;
  onSetCriterion: (ref: string, column: string, orIndex: number, operator: VisualFilterOperator, text: string) => void;
}) {
  const expr = column.criteria[orIndex] ?? null;
  const operator = expr?.operator ?? "_eq";
  const valueText = expr == null ? "" : formatValue(expr.value);

  return (
    <td style={styles.cell}>
      <div style={styles.criterion}>
        <select
          value={operator}
          onChange={(e) => onSetCriterion(column.tableRef, column.column, orIndex, e.target.value as VisualFilterOperator, valueText)}
          aria-label={`${column.column} operator ${orIndex}`}
        >
          {OPERATORS.map((op) => <option key={op} value={op}>{op}</option>)}
        </select>
        <input
          style={styles.value}
          value={valueText}
          placeholder={operator === "_null" ? "(null)" : operator === "_in" || operator === "_between" ? "a, b" : "value"}
          disabled={operator === "_null"}
          onChange={(e) => onSetCriterion(column.tableRef, column.column, orIndex, operator, e.target.value)}
          aria-label={`${column.column} value ${orIndex}`}
        />
      </div>
    </td>
  );
}

// Renders a stored criterion value back into the cell's text box.
function formatValue(value: unknown): string {
  if (value == null) return "";
  if (Array.isArray(value)) return value.join(", ");
  if (typeof value === "boolean") return "";
  return String(value);
}

// Re-exported for callers wiring onSetCriterion -> setColumnCriterion.
export { parseCriterionValue };

const styles: Record<string, React.CSSProperties> = {
  wrap: { borderTop: "1px solid var(--border, #d1d5db)", overflow: "auto", fontSize: 12 },
  empty: { padding: 16, color: "#6b7280" },
  table: { borderCollapse: "collapse", width: "100%" },
  rowHead: { textAlign: "left", padding: "3px 8px", background: "#f3f4f6", fontWeight: 600, position: "sticky", left: 0, whiteSpace: "nowrap" },
  colHead: { padding: "3px 8px", borderBottom: "1px solid #d1d5db", textAlign: "left", whiteSpace: "nowrap" },
  cell: { padding: "3px 8px", borderBottom: "1px solid #f0f0f0", whiteSpace: "nowrap" },
  criterion: { display: "flex", gap: 4 },
  value: { width: 110 },
};
