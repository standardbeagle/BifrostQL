import { useCallback, useEffect, useMemo, useState } from "react";
import { newSavedObjectId } from "../lib/saved-object-id";
import {
  FormRunnerHost,
  parseFormDefinition,
  type GraphQLFetcher,
} from "@standardbeagle/edit-db";
import {
  getBuilderSchema,
  isBuilderBridgeAvailable,
  type BuilderSchema,
} from "../lib/builder-bridge";
import {
  buildFormFromTable,
  toggleField,
  setFieldLabel,
  setFieldControl,
  setFieldReadOnly,
  setTitle,
  setLayoutColumns,
  moveField,
  visibleFields,
  type FormDefinition,
  type FormControlType,
} from "./form-state";
import {
  loadForms,
  upsertForm,
  deleteForm,
  type SavedForm,
} from "./forms-storage";
import { usePanelSize } from "../hooks/usePanelSize";

const CONTROL_OPTIONS: { value: FormControlType; label: string }[] = [
  { value: "text", label: "Text" },
  { value: "textarea", label: "Multi-line" },
  { value: "number", label: "Number" },
  { value: "checkbox", label: "Checkbox" },
  { value: "date", label: "Date" },
  { value: "datetime", label: "Date & time" },
  { value: "select", label: "Dropdown" },
];

/**
 * The Access-style form builder pane: pick a table, then design a bound
 * single-record data-entry form over it — toggle fields, rename labels, choose
 * controls, reorder, and set the layout column count, with a live preview.
 * Definitions persist to localStorage. Photino-only; in a browser the schema
 * bridge is absent and a notice is shown.
 *
 * The design preview renders controls disabled. "Run form" opens the live
 * record runtime (task 2.2) over the same transport fetcher the editor uses, so
 * a designed or saved form can be exercised against real data without leaving
 * the pane.
 */
export function FormBuilderPane({ fetcher }: { fetcher?: GraphQLFetcher }) {
  const editorWidth = usePanelSize({ key: "form-editor-width", initial: 560, min: 320, max: 1100, axis: "x" });
  const [schema, setSchema] = useState<BuilderSchema | null>(null);
  const [schemaError, setSchemaError] = useState<string | null>(null);
  const [def, setDef] = useState<FormDefinition | null>(null);
  const [forms, setForms] = useState<SavedForm[]>(() => loadForms());
  const [activeId, setActiveId] = useState<string | null>(null);
  // `error` distinguishes a real failure from a confirmation. A failed save
  // used to be indistinguishable from a successful one, so a form lost to a
  // quota error still read as "Saved".
  const [status, setStatus] = useState<{ text: string; error?: boolean } | null>(null);
  // The form currently open in the live runtime overlay, or null when designing.
  const [runningDef, setRunningDef] = useState<FormDefinition | null>(null);

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

  const sortedTables = useMemo(
    () => (schema ? [...schema.tables].sort((a, b) => a.qualified.localeCompare(b.qualified)) : []),
    [schema],
  );

  const onPickTable = useCallback(
    (qualified: string) => {
      if (!schema || !qualified) return;
      setDef(buildFormFromTable(schema, qualified));
      setActiveId(null);
      setStatus(null);
    },
    [schema],
  );

  const onSave = useCallback(() => {
    if (!def) return;
    const id = activeId ?? newSavedObjectId("form");
    let next;
    try {
      next = upsertForm(forms, { id, name: def.title, definition: def }, new Date().toISOString());
    } catch (error) {
      // The definition stays on screen so the user can retry or copy it out;
      // what must not happen is reporting success over a write that failed.
      setStatus({ text: error instanceof Error ? error.message : String(error), error: true });
      return;
    }
    setForms(next);
    setActiveId(id);
    setStatus({ text: `Saved "${def.title}"` });
  }, [def, activeId, forms]);

  const onLoad = useCallback(
    (id: string) => {
      const found = forms.find((f) => f.id === id);
      if (!found) return;
      setDef(found.definition);
      setActiveId(id);
      setStatus({ text: `Loaded "${found.name}"` });
    },
    [forms],
  );

  const onDelete = useCallback(() => {
    if (!activeId) return;
    let next;
    try {
      next = deleteForm(forms, activeId);
    } catch (error) {
      setStatus({ text: error instanceof Error ? error.message : String(error), error: true });
      return;
    }
    setForms(next);
    setActiveId(null);
    setStatus({ text: "Deleted" });
  }, [forms, activeId]);

  // Launch the live runtime for a definition — from the builder (current def) or
  // from the saved-object list (a saved form's stored definition). Routed through
  // the runner's tolerant parser so a stale/partial definition fails safe.
  const onRun = useCallback((definition: FormDefinition) => {
    setRunningDef(definition);
  }, []);

  if (!available) {
    return <div style={styles.unavailable}>The form builder is only available in the BifrostQL desktop app.</div>;
  }
  if (schemaError) {
    return <div style={styles.unavailable}>Could not load schema: {schemaError}</div>;
  }
  if (!schema) {
    return <div style={styles.unavailable}>Loading schema…</div>;
  }

  return (
    <div style={styles.root}>
      <div style={styles.toolbar}>
        <label style={styles.toolLabel}>
          Table
          <select
            style={styles.select}
            value={def?.table ?? ""}
            onChange={(e) => onPickTable(e.target.value)}
          >
            <option value="">Choose a table…</option>
            {sortedTables.map((t) => (
              <option key={t.qualified} value={t.qualified}>{t.qualified}</option>
            ))}
          </select>
        </label>

        {forms.length > 0 && (
          <label style={styles.toolLabel}>
            Saved
            <select
              style={styles.select}
              value={activeId ?? ""}
              onChange={(e) => e.target.value && onLoad(e.target.value)}
            >
              <option value="">Open a saved form…</option>
              {forms.map((f) => (
                <option key={f.id} value={f.id}>{f.name}</option>
              ))}
            </select>
          </label>
        )}

        <div style={styles.spacer} />
        {def && <button type="button" style={styles.btn} onClick={onSave}>Save</button>}
        {def && fetcher && <button type="button" style={styles.btn} onClick={() => onRun(def)}>Run form</button>}
        {activeId && <button type="button" style={styles.btn} onClick={onDelete}>Delete</button>}
        {status && (
          <span
            style={status.error ? styles.statusError : styles.status}
            role={status.error ? 'alert' : undefined}
          >
            {status.text}
          </span>
        )}
      </div>

      {!def ? (
        <div style={styles.unavailable}>Choose a table to start a form, or open a saved one.</div>
      ) : (
        <div style={styles.split}>
          <div style={{ flex: `0 0 ${editorWidth.size}px`, minWidth: 0, display: "flex", flexDirection: "column", overflow: "hidden" }}>
            <FieldEditor def={def} setDef={setDef} />
          </div>
          <div
            role="separator"
            aria-orientation="vertical"
            aria-label="Resize form editor"
            tabIndex={0}
            className="bifrost-resize-handle bifrost-resize-handle--col"
            onPointerDown={editorWidth.onPointerDown}
            onKeyDown={editorWidth.onKeyDown}
          />
          <FormPreview def={def} />
        </div>
      )}

      {runningDef && fetcher && (
        <div style={styles.runnerOverlay}>
          <FormRunnerHost
            fetcher={fetcher}
            definition={parseFormDefinition(runningDef) ?? runningDef}
            onClose={() => setRunningDef(null)}
          />
        </div>
      )}
    </div>
  );
}

function FieldEditor({
  def,
  setDef,
}: {
  def: FormDefinition;
  setDef: React.Dispatch<React.SetStateAction<FormDefinition | null>>;
}) {
  return (
    <div style={styles.editor}>
      <div style={styles.editorHeader}>
        <label style={styles.toolLabel}>
          Title
          <input
            style={styles.input}
            value={def.title}
            onChange={(e) => setDef((d) => (d ? setTitle(d, e.target.value) : d))}
          />
        </label>
        <label style={styles.toolLabel}>
          Columns
          <select
            style={styles.select}
            value={def.columns}
            onChange={(e) => setDef((d) => (d ? setLayoutColumns(d, Number(e.target.value)) : d))}
          >
            {[1, 2, 3, 4].map((n) => <option key={n} value={n}>{n}</option>)}
          </select>
        </label>
      </div>

      <table style={styles.fieldTable}>
        <thead>
          <tr>
            <th style={styles.th}>Show</th>
            <th style={styles.th}>Column</th>
            <th style={styles.th}>Label</th>
            <th style={styles.th}>Control</th>
            <th style={styles.th}>Read-only</th>
            <th style={styles.th}>Order</th>
          </tr>
        </thead>
        <tbody>
          {def.fields.map((f) => (
            <tr key={f.column}>
              <td style={styles.td}>
                <input
                  type="checkbox"
                  checked={f.include}
                  aria-label={`Show ${f.column}`}
                  onChange={() => setDef((d) => (d ? toggleField(d, f.column) : d))}
                />
              </td>
              <td style={styles.td}><code>{f.column}</code>{f.required && <span style={styles.req}> *</span>}</td>
              <td style={styles.td}>
                <input
                  style={styles.input}
                  value={f.label}
                  aria-label={`Label for ${f.column}`}
                  onChange={(e) => setDef((d) => (d ? setFieldLabel(d, f.column, e.target.value) : d))}
                />
              </td>
              <td style={styles.td}>
                <select
                  style={styles.select}
                  value={f.control}
                  aria-label={`Control for ${f.column}`}
                  onChange={(e) => setDef((d) => (d ? setFieldControl(d, f.column, e.target.value as FormControlType) : d))}
                >
                  {CONTROL_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                </select>
              </td>
              <td style={styles.td}>
                <input
                  type="checkbox"
                  checked={f.readOnly}
                  aria-label={`Read-only ${f.column}`}
                  onChange={(e) => setDef((d) => (d ? setFieldReadOnly(d, f.column, e.target.checked) : d))}
                />
              </td>
              <td style={styles.td}>
                <button type="button" style={styles.iconBtn} title="Move up" onClick={() => setDef((d) => (d ? moveField(d, f.column, -1) : d))}>↑</button>
                <button type="button" style={styles.iconBtn} title="Move down" onClick={() => setDef((d) => (d ? moveField(d, f.column, 1) : d))}>↓</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function FormPreview({ def }: { def: FormDefinition }) {
  const fields = visibleFields(def);
  return (
    <div style={styles.preview}>
      <div style={styles.previewCard}>
        <h2 style={styles.previewTitle}>{def.title}</h2>
        <div style={{ ...styles.previewGrid, gridTemplateColumns: `repeat(${def.columns}, minmax(0, 1fr))` }}>
          {fields.map((f) => (
            <label key={f.column} style={styles.previewField}>
              <span style={styles.previewLabel}>
                {f.label}{f.required && <span style={styles.req}> *</span>}
              </span>
              {renderControl(f.control, f.readOnly)}
            </label>
          ))}
          {fields.length === 0 && <p style={styles.muted}>No fields included — check some on the left.</p>}
        </div>
      </div>
    </div>
  );
}

function renderControl(control: FormControlType, readOnly: boolean): React.ReactNode {
  // Preview is non-bound: controls are disabled until the record runtime lands.
  const common = { disabled: true, style: styles.control } as const;
  switch (control) {
    case "checkbox":
      return <input type="checkbox" disabled style={styles.checkbox} />;
    case "textarea":
      return <textarea {...common} rows={3} placeholder={readOnly ? "(read-only)" : ""} />;
    case "number":
      return <input {...common} type="number" placeholder={readOnly ? "(read-only)" : ""} />;
    case "date":
      return <input {...common} type="date" />;
    case "datetime":
      return <input {...common} type="datetime-local" />;
    case "select":
      return <select {...common}><option>—</option></select>;
    default:
      return <input {...common} type="text" placeholder={readOnly ? "(read-only)" : ""} />;
  }
}

const styles: Record<string, React.CSSProperties> = {
  root: { display: "flex", flexDirection: "column", height: "100%", minHeight: 0, position: "relative" },
  toolbar: { display: "flex", alignItems: "flex-end", gap: 12, padding: "8px 12px", borderBottom: "1px solid var(--border, #d1d5db)", flexWrap: "wrap" },
  toolLabel: { display: "flex", flexDirection: "column", gap: 2, fontSize: 12, color: "#6b7280" },
  spacer: { flex: 1 },
  split: { display: "flex", flex: 1, minHeight: 0 },
  editor: { flex: 1, minWidth: 0, overflow: "auto", borderRight: "1px solid var(--border, #d1d5db)", padding: 12 },
  editorHeader: { display: "flex", gap: 16, marginBottom: 12, flexWrap: "wrap" },
  fieldTable: { width: "100%", borderCollapse: "collapse", fontSize: 13 },
  th: { textAlign: "left", padding: "4px 6px", borderBottom: "1px solid var(--border, #d1d5db)", color: "#6b7280", fontWeight: 600, whiteSpace: "nowrap" },
  td: { padding: "4px 6px", borderBottom: "1px solid #f1f5f9", verticalAlign: "middle" },
  preview: { flex: 1, minWidth: 0, overflow: "auto", padding: 16, background: "#f8fafc" },
  previewCard: { background: "#fff", border: "1px solid var(--border, #d1d5db)", borderRadius: 8, padding: 20, maxWidth: 720, margin: "0 auto" },
  previewTitle: { margin: "0 0 16px", fontSize: 18 },
  previewGrid: { display: "grid", gap: 14 },
  previewField: { display: "flex", flexDirection: "column", gap: 4, minWidth: 0 },
  previewLabel: { fontSize: 12, fontWeight: 600, color: "#374151" },
  control: { padding: "6px 8px", border: "1px solid var(--border, #d1d5db)", borderRadius: 6, font: "inherit", fontSize: 13, background: "#f9fafb" },
  checkbox: { width: 16, height: 16 },
  input: { padding: "4px 6px", border: "1px solid var(--border, #d1d5db)", borderRadius: 6, font: "inherit", fontSize: 13 },
  select: { padding: "4px 6px", border: "1px solid var(--border, #d1d5db)", borderRadius: 6, font: "inherit", fontSize: 13, background: "#fff" },
  btn: { border: "1px solid currentColor", background: "transparent", borderRadius: 6, padding: "6px 14px", cursor: "pointer", font: "inherit" },
  iconBtn: { border: "1px solid var(--border, #d1d5db)", background: "transparent", borderRadius: 4, padding: "0 6px", cursor: "pointer", font: "inherit", marginRight: 2 },
  status: { fontSize: 12, color: "#16a34a" },
  statusError: { fontSize: 12, color: "#dc2626" },
  req: { color: "#dc2626" },
  muted: { color: "#9ca3af", fontSize: 13 },
  unavailable: { padding: 24, color: "#6b7280" },
  runnerOverlay: { position: "absolute", inset: 0, background: "#fff", zIndex: 20, display: "flex", flexDirection: "column" },
};
