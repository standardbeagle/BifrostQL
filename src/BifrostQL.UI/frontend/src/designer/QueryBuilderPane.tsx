import { useCallback, useEffect, useState } from "react";
import { BridgeError } from "../lib/native-bridge";
import {
  getBuilderSchema,
  buildSql,
  buildAndExec,
  isBuilderBridgeAvailable,
  type BuilderSchema,
  type BuildAndExecResult,
} from "../lib/builder-bridge";
import type { VisualFilterOperator, VisualJoin, VisualJoinType, VisualSort } from "../lib/visual-query";
import { ResultGrid } from "../ResultGrid";
import { TablePalette } from "./TablePalette";
import { DesignCanvas } from "./DesignCanvas";
import { JoinEditor } from "./JoinEditor";
import { CriteriaGrid } from "./CriteriaGrid";
import {
  emptyDesignerState,
  addTableWithAutoJoin,
  removeTable,
  toggleColumnShow,
  setColumnSort,
  setColumnCriterion,
  parseCriterionValue,
  addJoin,
  removeJoin,
  setJoinType,
  toSpec,
  type DesignerState,
} from "./designer-state";

/**
 * The Access-style visual query builder pane: palette + canvas + join editor +
 * criteria grid, with View SQL (build-sql preview) and Run (build-and-exec into
 * the shared result grid). Photino-only; in a browser the bridge is absent and a
 * notice is shown.
 */
export function QueryBuilderPane() {
  const [schema, setSchema] = useState<BuilderSchema | null>(null);
  const [schemaError, setSchemaError] = useState<string | null>(null);
  const [state, setState] = useState<DesignerState>(emptyDesignerState);
  const [ambiguous, setAmbiguous] = useState<VisualJoin[]>([]);
  const [sqlPreview, setSqlPreview] = useState<string | null>(null);
  const [result, setResult] = useState<BuildAndExecResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [running, setRunning] = useState(false);

  const available = isBuilderBridgeAvailable();

  useEffect(() => {
    if (!available) return;
    let cancelled = false;
    getBuilderSchema()
      .then((s) => !cancelled && setSchema(s))
      .catch((e) => !cancelled && setSchemaError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [available]);

  const onAddTable = useCallback(
    (qualified: string) => {
      if (!schema) return;
      const { state: next, ambiguous: amb } = addTableWithAutoJoin(state, schema, qualified);
      setState(next);
      setAmbiguous(amb);
    },
    [schema, state]
  );

  const onSetCriterion = useCallback(
    (ref: string, column: string, orIndex: number, operator: VisualFilterOperator, text: string) => {
      const clear = operator !== "_null" && text.trim() === "";
      setState((s) =>
        setColumnCriterion(s, ref, column, orIndex, clear ? null : { operator, value: parseCriterionValue(operator, text) })
      );
    },
    []
  );

  const onViewSql = useCallback(async () => {
    setError(null);
    try {
      const built = await buildSql(toSpec(state));
      setSqlPreview(built.sql);
      setResult(null);
    } catch (e) {
      setSqlPreview(null);
      setError(errMsg(e));
    }
  }, [state]);

  const onRun = useCallback(async () => {
    if (running) return;
    setRunning(true);
    setError(null);
    try {
      const res = await buildAndExec(toSpec(state));
      setResult(res);
      setSqlPreview(res.sql);
    } catch (e) {
      setResult(null);
      setError(errMsg(e));
    } finally {
      setRunning(false);
    }
  }, [state, running]);

  if (!available) {
    return (
      <div className="qbe" style={styles.unavailable}>
        The visual query builder is only available in the BifrostQL desktop app.
      </div>
    );
  }

  if (schemaError) {
    return <div className="qbe" style={styles.unavailable}>Could not load schema: {schemaError}</div>;
  }

  if (!schema) {
    return <div className="qbe" style={styles.unavailable}>Loading schema…</div>;
  }

  return (
    <div className="qbe" style={styles.root}>
      <div style={styles.top}>
        <TablePalette schema={schema} onAddTable={onAddTable} />
        <div style={styles.canvasCol}>
          <DesignCanvas
            state={state}
            schema={schema}
            onToggleColumn={(ref, col) => setState((s) => toggleColumnShow(s, ref, col))}
            onRemoveTable={(ref) => setState((s) => removeTable(s, ref))}
          />
          {ambiguous.length > 0 && (
            <div style={styles.ambiguous} role="alert">
              <span>Multiple join paths — pick one:</span>
              {ambiguous.map((j, i) => (
                <button
                  key={i}
                  type="button"
                  onClick={() => {
                    setState((s) => addJoin(s, j));
                    setAmbiguous([]);
                  }}
                >
                  {j.leftTable}.({j.leftColumns.join(",")}) = {j.rightTable}.({j.rightColumns.join(",")})
                </button>
              ))}
              <button type="button" onClick={() => setAmbiguous([])}>Dismiss</button>
            </div>
          )}
          <JoinEditor
            state={state}
            schema={schema}
            onAddJoin={(j) => setState((s) => addJoin(s, j))}
            onRemoveJoin={(i) => setState((s) => removeJoin(s, i))}
            onSetJoinType={(i, t: VisualJoinType) => setState((s) => setJoinType(s, i, t))}
          />
          <CriteriaGrid
            state={state}
            onSetSort={(ref, col, sort: VisualSort) => setState((s) => setColumnSort(s, ref, col, sort))}
            onToggleShow={(ref, col) => setState((s) => toggleColumnShow(s, ref, col))}
            onSetCriterion={onSetCriterion}
          />
        </div>
      </div>

      <div style={styles.toolbar}>
        <button type="button" onClick={() => void onViewSql()} style={styles.btn}>View SQL</button>
        <button type="button" onClick={() => void onRun()} disabled={running} style={styles.runBtn}>
          {running ? "Running…" : "Run"}
        </button>
        {result && (
          <span style={styles.status}>
            {result.rows.length} row(s){result.truncated ? " (truncated)" : ""}
          </span>
        )}
      </div>

      {error && <div role="alert" style={styles.error}>{error}</div>}
      {!error && sqlPreview && <pre style={styles.sql}>{sqlPreview}</pre>}
      {!error && result && result.columns.length > 0 && (
        <ResultGrid columns={result.columns} rows={result.rows} />
      )}
    </div>
  );
}

function errMsg(e: unknown): string {
  return e instanceof BridgeError || e instanceof Error ? e.message : String(e);
}

const styles: Record<string, React.CSSProperties> = {
  root: { display: "flex", flexDirection: "column", height: "100%", minHeight: 0 },
  top: { display: "flex", flex: 1, minHeight: 0, borderBottom: "1px solid var(--border, #d1d5db)" },
  canvasCol: { display: "flex", flexDirection: "column", flex: 1, minWidth: 0, overflow: "auto" },
  ambiguous: { display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap", padding: "6px 12px", background: "#fffbeb", borderTop: "1px solid #fde68a", fontSize: 12 },
  toolbar: { display: "flex", alignItems: "center", gap: 12, padding: "8px 12px" },
  btn: { border: "1px solid currentColor", background: "transparent", borderRadius: 6, padding: "6px 14px", cursor: "pointer", font: "inherit" },
  runBtn: { background: "#2563eb", color: "#fff", border: "none", borderRadius: 6, padding: "6px 14px", cursor: "pointer", font: "inherit" },
  status: { fontSize: 12, color: "#6b7280" },
  sql: { margin: "0 12px 12px", padding: 12, background: "#f8fafc", border: "1px solid var(--border, #d1d5db)", borderRadius: 6, fontFamily: "var(--font-mono, monospace)", fontSize: 12, whiteSpace: "pre-wrap", overflow: "auto" },
  error: { color: "#b91c1c", background: "#fef2f2", border: "1px solid #fecaca", borderRadius: 6, padding: 12, margin: "0 12px 12px", fontFamily: "var(--font-mono, monospace)", fontSize: 12, whiteSpace: "pre-wrap" },
  unavailable: { padding: 24, color: "#6b7280" },
};
