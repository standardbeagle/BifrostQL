import { useCallback, useEffect, useMemo, useState } from "react";
import type { SavedObject } from "@standardbeagle/edit-db";
import { SavedObjectConflictError } from "@standardbeagle/edit-db";
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
  tableRef,
  m2mJoinPlans,
  applyM2mJoinPlan,
  type DesignerState,
  type M2mJoinPlan,
} from "./designer-state";
import {
  serializeQuery,
  parseQueryDefinition,
  detectSchemaDrift,
  describeDrift,
  hasDrift,
  type SavedQueryDefinition,
  type SchemaDrift,
} from "./saved-query";
import { savedQueryStore, SAVED_QUERY_TYPE, newQueryId } from "./saved-query-store";

/** The saved query currently open in the designer (id is stable across renames). */
interface ActiveQuery {
  id: string;
  name: string;
  /** Store version, echoed back on save for optimistic-concurrency detection. */
  version: number;
}

interface QueryBuilderPaneProps {
  /** A saved query the shell's nav asked us to open. */
  openRequest?: SavedObject | null;
  /**
   * Fired once the open request has been consumed (opened, declined, or
   * rejected). The shell clears it, so a later unmount/remount of this pane
   * cannot replay a stale request and resurrect a deleted or superseded query.
   */
  onOpenHandled?: () => void;
  /** Reports which saved query is open (or null) so the nav can highlight it. */
  onActiveChange?: (id: string | null) => void;
  /** Fired after a save/rename/delete so the nav refetches its list. */
  onStoreChanged?: () => void;
}

/**
 * The Access-style visual query builder pane: palette + canvas + join editor +
 * criteria grid, with View SQL (build-sql preview) and Run (build-and-exec into
 * the shared result grid). Photino-only; in a browser the bridge is absent and a
 * notice is shown.
 *
 * Designs persist as `type: 'query'` saved objects. Opening one restores the
 * design and diffs its schema fingerprint against the live schema: broken
 * references open the query in degraded mode (running is blocked, the definition
 * is left exactly as stored) so the user — not the app — decides the repair.
 */
export function QueryBuilderPane({
  openRequest,
  onOpenHandled,
  onActiveChange,
  onStoreChanged,
}: QueryBuilderPaneProps = {}) {
  const [schema, setSchema] = useState<BuilderSchema | null>(null);
  const [schemaError, setSchemaError] = useState<string | null>(null);
  const [state, setState] = useState<DesignerState>(emptyDesignerState);
  const [ambiguous, setAmbiguous] = useState<VisualJoin[]>([]);
  const [m2mPlans, setM2mPlans] = useState<M2mJoinPlan[]>([]);
  const [sqlPreview, setSqlPreview] = useState<string | null>(null);
  const [result, setResult] = useState<BuildAndExecResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [running, setRunning] = useState(false);

  // Saved-query session: what's open, what was last persisted, and whether the
  // stored design still matches the live schema.
  const [active, setActive] = useState<ActiveQuery | null>(null);
  const [savedDefinition, setSavedDefinition] = useState<SavedQueryDefinition | null>(null);
  const [savedSnapshot, setSavedSnapshot] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

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

  const dirty = useMemo(() => {
    if (savedSnapshot === null) return state.tables.length > 0;
    return JSON.stringify(state) !== savedSnapshot;
  }, [state, savedSnapshot]);

  // Degraded mode: the design on the canvas references something the database no
  // longer has. Derived from the state itself — never from a stored fingerprint,
  // which could disagree with the state and only ever by under-reporting. So a
  // query saved by an older build, or with a truncated fingerprint, still opens
  // degraded; and once the user edits the offending references away, the design
  // is runnable again. Building SQL from a drifted design would fail anyway, so
  // Run/View SQL are blocked; Save/Save-as are blocked too, so the one action
  // that writes a broken design back to the store isn't the one left open.
  const drift: SchemaDrift | null = useMemo(
    () => (schema ? detectSchemaDrift(serializeQuery(state), schema) : null),
    [state, schema],
  );
  const degraded = drift !== null && hasDrift(drift);
  // Nothing to save: an empty canvas that isn't attached to a saved query.
  const empty = state.tables.length === 0 && active === null;

  // Open a saved query: restore the design exactly as stored, after confirming
  // any unsaved edits may be discarded. Consumed one-shot (onOpenHandled) so a
  // pane switch cannot replay it. Never rewrites the definition to "fix" it.
  useEffect(() => {
    if (!openRequest || !schema) return;
    const definition = parseQueryDefinition(openRequest.definition);
    if (!definition) {
      setError(`"${openRequest.name}" is not a saved visual query this version can open.`);
      onOpenHandled?.();
      return;
    }
    if (dirty && !window.confirm(`Discard unsaved changes and open "${openRequest.name}"?`)) {
      onOpenHandled?.();
      return;
    }
    setState(definition.state);
    setSavedDefinition(definition);
    setSavedSnapshot(JSON.stringify(definition.state));
    setActive({ id: openRequest.id, name: openRequest.name, version: openRequest.version });
    setAmbiguous([]);
    setM2mPlans([]);
    setSqlPreview(null);
    setResult(null);
    setError(null);
    setStatus(null);
    onActiveChange?.(openRequest.id);
    onOpenHandled?.();
    // `dirty` is read to guard the discard; the request is consumed on the first
    // run, so this cannot re-fire for the same request when dirty later changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [openRequest, schema, onActiveChange, onOpenHandled]);

  /** Persists the given definition under `id`/`name`; returns false when it failed. */
  const persist = useCallback(
    async (id: string, name: string, version: number, definition: SavedQueryDefinition): Promise<boolean> => {
      try {
        const stored = await savedQueryStore.put({
          id,
          type: SAVED_QUERY_TYPE,
          name,
          definition,
          version,
        });
        setActive({ id: stored.id, name: stored.name, version: stored.version });
        setSavedDefinition(definition);
        setSavedSnapshot(JSON.stringify(definition.state));
        setError(null);
        setStatus(`Saved "${stored.name}".`);
        onActiveChange?.(stored.id);
        onStoreChanged?.();
        return true;
      } catch (e) {
        setStatus(null);
        setError(
          e instanceof SavedObjectConflictError
            ? `"${name}" was changed elsewhere. Reopen it before saving to avoid overwriting that change.`
            : errMsg(e),
        );
        return false;
      }
    },
    [onActiveChange, onStoreChanged],
  );

  const onSaveAs = useCallback(async () => {
    const name = window.prompt("Save query as", active ? `${active.name} copy` : "Untitled query");
    if (name === null || name.trim() === "") return;
    // A new object: fresh id, version 0 (the store assigns the stored version).
    await persist(newQueryId(), name.trim(), 0, serializeQuery(state));
  }, [active, state, persist]);

  const onSave = useCallback(async () => {
    if (!active) {
      await onSaveAs();
      return;
    }
    await persist(active.id, active.name, active.version, serializeQuery(state));
  }, [active, state, persist, onSaveAs]);

  // Rename keeps the id — and the stored definition — so a rename can never
  // smuggle in unsaved design edits (or auto-repair a drifted definition).
  const onRename = useCallback(async () => {
    if (!active || !savedDefinition) return;
    const name = window.prompt("Rename query", active.name);
    if (name === null || name.trim() === "" || name.trim() === active.name) return;
    await persist(active.id, name.trim(), active.version, savedDefinition);
  }, [active, savedDefinition, persist]);

  const onDelete = useCallback(async () => {
    if (!active) return;
    if (!window.confirm(`Delete the saved query "${active.name}"? This cannot be undone.`)) return;
    try {
      await savedQueryStore.remove(SAVED_QUERY_TYPE, active.id);
    } catch (e) {
      setError(errMsg(e));
      return;
    }
    // The design stays on the canvas — deleting the saved copy shouldn't throw
    // away the work in front of the user — but it is no longer attached to one.
    setActive(null);
    setSavedDefinition(null);
    setSavedSnapshot(null);
    setError(null);
    setStatus(`Deleted "${active.name}". The design is still open, unsaved.`);
    onActiveChange?.(null);
    onStoreChanged?.();
  }, [active, onActiveChange, onStoreChanged]);

  const onAddTable = useCallback(
    (qualified: string) => {
      if (!schema) return;
      const { state: next, ambiguous: amb } = addTableWithAutoJoin(state, schema, qualified);
      setState(next);
      setAmbiguous(amb);
      // Offer many-to-many bridges to the new table only when no direct FK join
      // was auto-applied or pending — the direct path takes precedence.
      const directJoined = next.joins.length > state.joins.length;
      const newRef = tableRef(next.tables[next.tables.length - 1]);
      setM2mPlans(!directJoined && amb.length === 0 ? m2mJoinPlans(next, schema, newRef) : []);
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
      {degraded && drift && (
        <div role="alert" style={styles.drift}>
          <strong>This saved query no longer matches the database.</strong>
          <span>
            {describeDrift(drift).join(", ")} {drift.missingTables.length + drift.missingColumns.length === 1 ? "is" : "are"} gone.
            The saved definition was left untouched — edit the design to repair it, then save.
          </span>
        </div>
      )}

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
          {m2mPlans.length > 0 && (
            <div style={styles.ambiguous} role="alert">
              <span>Many-to-many — join through the junction table:</span>
              {m2mPlans.map((plan, i) => (
                <button
                  key={i}
                  type="button"
                  onClick={() => {
                    setState((s) => applyM2mJoinPlan(s, plan));
                    setM2mPlans([]);
                  }}
                >
                  via {plan.junctionTable}
                </button>
              ))}
              <button type="button" onClick={() => setM2mPlans([])}>Dismiss</button>
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
        <span style={styles.name}>
          {active ? active.name : "Untitled query"}
          {dirty && (
            <span style={styles.dirty} title="Unsaved changes" aria-label="Unsaved changes">
              •
            </span>
          )}
        </span>
        <button
          type="button"
          onClick={() => void onSave()}
          disabled={degraded || empty || (!dirty && active !== null)}
          title={degraded ? DEGRADED_SAVE_HINT : undefined}
          style={styles.btn}
        >
          Save
        </button>
        <button
          type="button"
          onClick={() => void onSaveAs()}
          disabled={degraded || empty}
          title={degraded ? DEGRADED_SAVE_HINT : undefined}
          style={styles.btn}
        >
          Save as…
        </button>
        <button type="button" onClick={() => void onRename()} disabled={!active} style={styles.btn}>Rename</button>
        <button type="button" onClick={() => void onDelete()} disabled={!active} style={styles.btn}>Delete</button>

        <span style={styles.spacer} />

        <button type="button" onClick={() => void onViewSql()} disabled={degraded} style={styles.btn}>View SQL</button>
        <button type="button" onClick={() => void onRun()} disabled={running || degraded} style={styles.runBtn}>
          {running ? "Running…" : "Run"}
        </button>
        {result && (
          <span style={styles.status}>
            {result.rows.length} row(s){result.truncated ? " (truncated)" : ""}
          </span>
        )}
        {!result && status && <span style={styles.status}>{status}</span>}
      </div>

      {error && <div role="alert" style={styles.error}>{error}</div>}
      {!error && sqlPreview && <pre style={styles.sql}>{sqlPreview}</pre>}
      {!error && result && result.columns.length > 0 && (
        <ResultGrid columns={result.columns} rows={result.rows} />
      )}
    </div>
  );
}

const DEGRADED_SAVE_HINT =
  "Repair the broken references before saving — saving now would store a design the database can no longer run.";

function errMsg(e: unknown): string {
  return e instanceof BridgeError || e instanceof Error ? e.message : String(e);
}

const styles: Record<string, React.CSSProperties> = {
  root: { display: "flex", flexDirection: "column", height: "100%", minHeight: 0, flex: 1, minWidth: 0 },
  top: { display: "flex", flex: 1, minHeight: 0, borderBottom: "1px solid var(--border, #d1d5db)" },
  canvasCol: { display: "flex", flexDirection: "column", flex: 1, minWidth: 0, overflow: "auto" },
  ambiguous: { display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap", padding: "6px 12px", background: "#fffbeb", borderTop: "1px solid #fde68a", fontSize: 12 },
  drift: { display: "flex", flexDirection: "column", gap: 4, padding: "8px 12px", background: "#fffbeb", borderBottom: "1px solid #fde68a", color: "#92400e", fontSize: 12 },
  toolbar: { display: "flex", alignItems: "center", gap: 8, padding: "8px 12px" },
  name: { fontSize: 13, fontWeight: 600, marginRight: 4 },
  dirty: { color: "#b45309", marginLeft: 4, fontSize: 16, lineHeight: 1 },
  spacer: { flex: 1 },
  btn: { border: "1px solid currentColor", background: "transparent", borderRadius: 6, padding: "6px 14px", cursor: "pointer", font: "inherit" },
  runBtn: { background: "#2563eb", color: "#fff", border: "none", borderRadius: 6, padding: "6px 14px", cursor: "pointer", font: "inherit" },
  status: { fontSize: 12, color: "#6b7280" },
  sql: { margin: "0 12px 12px", padding: 12, background: "#f8fafc", border: "1px solid var(--border, #d1d5db)", borderRadius: 6, fontFamily: "var(--font-mono, monospace)", fontSize: 12, whiteSpace: "pre-wrap", overflow: "auto" },
  error: { color: "#b91c1c", background: "#fef2f2", border: "1px solid #fecaca", borderRadius: 6, padding: 12, margin: "0 12px 12px", fontFamily: "var(--font-mono, monospace)", fontSize: 12, whiteSpace: "pre-wrap" },
  unavailable: { padding: 24, color: "#6b7280" },
};
