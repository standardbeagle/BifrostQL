import type { BuilderSchema } from "../lib/builder-bridge";

/**
 * Left-hand palette listing every table in the active connection's schema. The
 * user clicks Add to place a table on the design canvas. Stateless — placement
 * lives in the designer state owned by the parent.
 */
export function TablePalette({
  schema,
  onAddTable,
}: {
  schema: BuilderSchema;
  onAddTable: (qualified: string) => void;
}) {
  return (
    <div className="qbe-palette" style={styles.palette}>
      <div style={styles.header}>Tables</div>
      <ul style={styles.list}>
        {schema.tables.map((t) => (
          <li key={t.qualified} style={styles.row}>
            <span style={styles.name} title={t.qualified}>
              {t.name}
            </span>
            <button
              type="button"
              style={styles.add}
              onClick={() => onAddTable(t.qualified)}
              aria-label={`Add ${t.qualified}`}
            >
              +
            </button>
          </li>
        ))}
        {schema.tables.length === 0 && <li style={styles.empty}>No tables</li>}
      </ul>
    </div>
  );
}

const styles: Record<string, React.CSSProperties> = {
  palette: { width: 220, borderRight: "1px solid var(--border, #d1d5db)", overflow: "auto", flexShrink: 0 },
  header: { fontWeight: 600, padding: "8px 12px", fontSize: 13, borderBottom: "1px solid var(--border, #d1d5db)" },
  list: { listStyle: "none", margin: 0, padding: 0 },
  row: { display: "flex", alignItems: "center", justifyContent: "space-between", padding: "4px 12px", fontSize: 13 },
  name: { overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" },
  add: { border: "1px solid currentColor", background: "transparent", borderRadius: 4, cursor: "pointer", width: 22, lineHeight: "18px" },
  empty: { padding: "8px 12px", color: "#6b7280", fontSize: 12 },
};
