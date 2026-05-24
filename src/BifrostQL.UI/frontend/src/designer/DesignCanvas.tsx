import type { BuilderSchema } from "../lib/builder-bridge";
import { type DesignerState, type PlacedTable, tableRef, isColumnShown } from "./designer-state";

/**
 * The design surface: one box per placed table showing its columns with
 * show-checkboxes. Column metadata comes from the schema; selection state comes
 * from the designer state. Stateless — edits are reported up via callbacks.
 */
export function DesignCanvas({
  state,
  schema,
  onToggleColumn,
  onRemoveTable,
}: {
  state: DesignerState;
  schema: BuilderSchema;
  onToggleColumn: (ref: string, column: string) => void;
  onRemoveTable: (ref: string) => void;
}) {
  if (state.tables.length === 0) {
    return (
      <div className="qbe-canvas qbe-canvas--empty" style={styles.empty}>
        Add tables from the palette to start building a query.
      </div>
    );
  }

  return (
    <div className="qbe-canvas" style={styles.canvas}>
      {state.tables.map((placed) => (
        <TableBox
          key={tableRef(placed)}
          placed={placed}
          schema={schema}
          state={state}
          onToggleColumn={onToggleColumn}
          onRemoveTable={onRemoveTable}
        />
      ))}
    </div>
  );
}

function TableBox({
  placed,
  schema,
  state,
  onToggleColumn,
  onRemoveTable,
}: {
  placed: PlacedTable;
  schema: BuilderSchema;
  state: DesignerState;
  onToggleColumn: (ref: string, column: string) => void;
  onRemoveTable: (ref: string) => void;
}) {
  const ref = tableRef(placed);
  const columns = schema.columns.filter((c) => c.table === placed.table);

  return (
    <div className="qbe-table-box" style={styles.box}>
      <div style={styles.boxHeader}>
        <span style={styles.boxTitle} title={placed.table}>
          {placed.alias ? `${placed.alias} (${placed.table})` : placed.table}
        </span>
        <button type="button" style={styles.remove} onClick={() => onRemoveTable(ref)} aria-label={`Remove ${ref}`}>
          ×
        </button>
      </div>
      <ul style={styles.cols}>
        {columns.map((col) => (
          <li key={col.name} style={styles.colRow}>
            <label style={styles.colLabel}>
              <input
                type="checkbox"
                checked={isColumnShown(state, ref, col.name)}
                onChange={() => onToggleColumn(ref, col.name)}
              />
              <span>{col.name}</span>
              {col.isPrimaryKey && <span style={styles.pk}>PK</span>}
            </label>
            <span style={styles.type}>{col.type}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

const styles: Record<string, React.CSSProperties> = {
  canvas: { flex: 1, display: "flex", flexWrap: "wrap", alignContent: "flex-start", gap: 12, padding: 12, overflow: "auto" },
  empty: { flex: 1, padding: 24, color: "#6b7280" },
  box: { border: "1px solid var(--border, #d1d5db)", borderRadius: 8, width: 240, alignSelf: "flex-start", background: "var(--surface, #fff)" },
  boxHeader: { display: "flex", justifyContent: "space-between", alignItems: "center", padding: "6px 10px", borderBottom: "1px solid var(--border, #d1d5db)", background: "#f3f4f6", borderRadius: "8px 8px 0 0" },
  boxTitle: { fontWeight: 600, fontSize: 12, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" },
  remove: { border: "none", background: "transparent", cursor: "pointer", fontSize: 16, lineHeight: 1 },
  cols: { listStyle: "none", margin: 0, padding: 0, maxHeight: 240, overflow: "auto" },
  colRow: { display: "flex", justifyContent: "space-between", alignItems: "center", padding: "2px 10px", fontSize: 12 },
  colLabel: { display: "flex", alignItems: "center", gap: 6, cursor: "pointer" },
  pk: { fontSize: 9, color: "#b45309", border: "1px solid #fcd34d", borderRadius: 3, padding: "0 3px" },
  type: { color: "#9ca3af", fontSize: 11 },
};
