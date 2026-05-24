import { useState } from "react";
import type { BuilderSchema } from "../lib/builder-bridge";
import type { VisualJoin, VisualJoinType } from "../lib/visual-query";
import { type DesignerState, tableRef } from "./designer-state";

/**
 * Lists the query's joins with an INNER/LEFT toggle and delete, plus a small
 * form to add a manual single-column join. Auto-joins (from FK metadata) are
 * created upstream when a related table is dropped; this editor is for review,
 * type changes, deletion, and hand-wiring joins the schema doesn't know about.
 */
export function JoinEditor({
  state,
  schema,
  onAddJoin,
  onRemoveJoin,
  onSetJoinType,
}: {
  state: DesignerState;
  schema: BuilderSchema;
  onAddJoin: (join: VisualJoin) => void;
  onRemoveJoin: (index: number) => void;
  onSetJoinType: (index: number, type: VisualJoinType) => void;
}) {
  const refs = state.tables.map(tableRef);
  const qualifiedOf = (ref: string) =>
    state.tables.find((t) => tableRef(t) === ref)?.table ?? ref;
  const columnsOf = (ref: string) =>
    schema.columns.filter((c) => c.table === qualifiedOf(ref)).map((c) => c.name);

  return (
    <div className="qbe-joins" style={styles.wrap}>
      <div style={styles.header}>Joins</div>

      {state.joins.length === 0 && <div style={styles.empty}>No joins.</div>}
      <ul style={styles.list}>
        {state.joins.map((j, i) => (
          <li key={i} style={styles.row}>
            <span style={styles.expr}>
              {j.leftTable}.({j.leftColumns.join(", ")}) = {j.rightTable}.({j.rightColumns.join(", ")})
            </span>
            <select
              value={j.type}
              onChange={(e) => onSetJoinType(i, e.target.value as VisualJoinType)}
              aria-label={`Join ${i + 1} type`}
            >
              <option value="inner">INNER</option>
              <option value="left">LEFT</option>
            </select>
            <button type="button" onClick={() => onRemoveJoin(i)} aria-label={`Remove join ${i + 1}`}>
              ×
            </button>
          </li>
        ))}
      </ul>

      <ManualJoinForm refs={refs} columnsOf={columnsOf} onAddJoin={onAddJoin} />
    </div>
  );
}

function ManualJoinForm({
  refs,
  columnsOf,
  onAddJoin,
}: {
  refs: string[];
  columnsOf: (ref: string) => string[];
  onAddJoin: (join: VisualJoin) => void;
}) {
  const [leftTable, setLeftTable] = useState("");
  const [leftColumn, setLeftColumn] = useState("");
  const [rightTable, setRightTable] = useState("");
  const [rightColumn, setRightColumn] = useState("");
  const [type, setType] = useState<VisualJoinType>("inner");

  const canAdd =
    leftTable && rightTable && leftColumn && rightColumn && leftTable !== rightTable;

  function submit() {
    if (!canAdd) return;
    onAddJoin({
      leftTable,
      leftColumns: [leftColumn],
      rightTable,
      rightColumns: [rightColumn],
      type,
    });
    setLeftColumn("");
    setRightColumn("");
  }

  return (
    <div style={styles.form}>
      <TableColPicker label="Left" refs={refs} columnsOf={columnsOf} table={leftTable} column={leftColumn} onTable={setLeftTable} onColumn={setLeftColumn} />
      <span>=</span>
      <TableColPicker label="Right" refs={refs} columnsOf={columnsOf} table={rightTable} column={rightColumn} onTable={setRightTable} onColumn={setRightColumn} />
      <select value={type} onChange={(e) => setType(e.target.value as VisualJoinType)} aria-label="New join type">
        <option value="inner">INNER</option>
        <option value="left">LEFT</option>
      </select>
      <button type="button" disabled={!canAdd} onClick={submit}>
        Add join
      </button>
    </div>
  );
}

function TableColPicker({
  label,
  refs,
  columnsOf,
  table,
  column,
  onTable,
  onColumn,
}: {
  label: string;
  refs: string[];
  columnsOf: (ref: string) => string[];
  table: string;
  column: string;
  onTable: (v: string) => void;
  onColumn: (v: string) => void;
}) {
  return (
    <span style={styles.picker}>
      <select value={table} onChange={(e) => { onTable(e.target.value); onColumn(""); }} aria-label={`${label} table`}>
        <option value="">{label} table…</option>
        {refs.map((r) => (
          <option key={r} value={r}>{r}</option>
        ))}
      </select>
      <select value={column} onChange={(e) => onColumn(e.target.value)} disabled={!table} aria-label={`${label} column`}>
        <option value="">column…</option>
        {table && columnsOf(table).map((c) => (
          <option key={c} value={c}>{c}</option>
        ))}
      </select>
    </span>
  );
}

const styles: Record<string, React.CSSProperties> = {
  wrap: { borderTop: "1px solid var(--border, #d1d5db)", padding: "8px 12px", fontSize: 12 },
  header: { fontWeight: 600, marginBottom: 6 },
  empty: { color: "#6b7280", marginBottom: 6 },
  list: { listStyle: "none", margin: 0, padding: 0, display: "flex", flexDirection: "column", gap: 4 },
  row: { display: "flex", alignItems: "center", gap: 8 },
  expr: { fontFamily: "var(--font-mono, monospace)", flex: 1 },
  form: { display: "flex", alignItems: "center", gap: 6, marginTop: 8, flexWrap: "wrap" },
  picker: { display: "flex", gap: 4 },
};
