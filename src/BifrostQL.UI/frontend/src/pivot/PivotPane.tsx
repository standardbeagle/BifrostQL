import { useEffect, useState } from "react";
import type { GraphQLFetcher, SavedObject, SavedObjectsClient } from "@standardbeagle/edit-db";
import { buildPivotQuery, displayPivotColumn, parsePivotPayload, type PivotAggregate, type PivotDefinition, type PivotPayload } from "./pivot-model";
import { openPivot, PIVOT_SAVED_OBJECT_TYPE, pivotStore, savePivot } from "./pivot-store";

type Table = { graphQlName: string; columns: Array<{ graphQlName: string }> };
const empty = (table = ""): PivotDefinition => ({ kind: "bifrost.pivot", version: 1, source: { kind: "table", table }, rowKeys: [], pivotColumn: "", valueColumn: "", aggregate: "count" });

export function PivotPane({ fetcher, initialDefinition, store = pivotStore }: { fetcher: GraphQLFetcher; initialDefinition?: PivotDefinition | null; store?: SavedObjectsClient }) {
  const [definition, setDefinition] = useState<PivotDefinition>(() => initialDefinition ?? empty());
  const [tables, setTables] = useState<Table[]>([]);
  const [saved, setSaved] = useState<SavedObject[]>([]);
  const [name, setName] = useState("Untitled pivot");
  const [result, setResult] = useState<PivotPayload | null>(null);
  const [error, setError] = useState<string | null>(null);
  const selected = tables.find((table) => table.graphQlName === definition.source.table);
  const fields = selected?.columns ?? [];
  const ready = definition.rowKeys.length > 0 && !!definition.pivotColumn && !!definition.valueColumn;

  useEffect(() => { void fetcher.query<{ _dbSchema: Table[] }>("query PivotSchema { _dbSchema { graphQlName columns { graphQlName } } }").then((data) => setTables(data._dbSchema ?? [])).catch((reason) => setError(String(reason))); }, [fetcher]);
  useEffect(() => { void store.list(PIVOT_SAVED_OBJECT_TYPE).then((objects) => setSaved(objects.filter((item) => openPivot(item) !== null))); }, [store]);
  useEffect(() => { if (initialDefinition) setDefinition(initialDefinition); }, [initialDefinition]);
  useEffect(() => {
    if (!ready) { setResult(null); return; }
    const timer = window.setTimeout(() => {
      try {
        const { query, variables } = buildPivotQuery(definition);
        setError(null);
        void fetcher.query<Record<string, unknown>>(query, variables).then((data) => setResult(parsePivotPayload(data[`${definition.source.table}Pivot`]))).catch((reason) => setError(reason instanceof Error ? reason.message : String(reason)));
      } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
    }, 250);
    return () => window.clearTimeout(timer);
  }, [fetcher, definition, ready]);

  const update = (patch: Partial<PivotDefinition>) => setDefinition((current) => ({ ...current, ...patch }));
  const chooseTable = (table: string) => update({ source: { kind: "table", table }, rowKeys: [], pivotColumn: "", valueColumn: "" });
  const setRow = (field: string) => update({ rowKeys: field ? [...definition.rowKeys.filter((key) => key !== field), field].filter((key) => key !== definition.pivotColumn) : [] });
  const save = async () => { try { const object = await savePivot(store, { id: crypto.randomUUID?.() ?? `pivot-${Date.now()}`, name: name || "Untitled pivot", definition, version: 0 }); setSaved((items) => [...items.filter((item) => item.id !== object.id), object]); } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); } };
  const load = (id: string) => { const object = saved.find((item) => item.id === id); const parsed = object && openPivot(object); if (parsed) { setDefinition(parsed); setName(object.name); } };
  const sourceKind = definition.source.kind;
  return <section className="bifrost-pivot-pane" aria-label="Pivot panel">
    <header><h2>Pivot</h2><label>Table <select aria-label="Pivot table" value={definition.source.table} onChange={(event) => chooseTable(event.target.value)}><option value="">Choose a table</option>{tables.map((table) => <option key={table.graphQlName} value={table.graphQlName}>{table.graphQlName}</option>)}</select></label>
      <label>Source <select aria-label="Pivot source kind" value={sourceKind} onChange={(event) => update({ source: { ...definition.source, kind: event.target.value as "table" | "saved-query" } })}><option value="table">Table</option><option value="saved-query">Saved query</option></select></label>
      {sourceKind === "saved-query" && <label>Saved query <select aria-label="Pivot saved query" value={definition.source.savedQueryRef ?? ""} onChange={(event) => update({ source: { ...definition.source, savedQueryRef: event.target.value } })}><option value="">Equivalent saved query</option>{saved.map((item) => <option value={item.id} key={item.id}>{item.name}</option>)}</select></label>}</header>
    <p className="bifrost-pivot-field-list" aria-label="Pivot fields">Drag a schema field into a well: {fields.map((field) => <button key={field.graphQlName} type="button" draggable onDragStart={(event) => event.dataTransfer.setData("text/plain", field.graphQlName)}>{field.graphQlName}</button>)}</p>
    <div className="bifrost-pivot-wells"><FieldWell label="Rows" fields={fields} value={definition.rowKeys[0] ?? ""} onChange={setRow} /><FieldWell label="Columns" fields={fields} value={definition.pivotColumn} onChange={(pivotColumn) => update({ pivotColumn, rowKeys: definition.rowKeys.filter((key) => key !== pivotColumn) })} /><ValueWell fields={fields} value={definition.valueColumn} aggregate={definition.aggregate} onColumn={(valueColumn) => update({ valueColumn })} onAggregate={(aggregate) => update({ aggregate })} /></div>
    <div className="bifrost-pivot-save"><label>Pivot name <input aria-label="Pivot name" value={name} onChange={(event) => setName(event.target.value)} /></label><button type="button" onClick={() => void save()}>Save pivot</button><label>Open saved pivot <select aria-label="Open saved pivot" defaultValue="" onChange={(event) => load(event.target.value)}><option value="">Open a saved pivot</option>{saved.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label></div>
    {error && <p role="alert">{error.includes("distinct values") ? `${error} Add a filter or choose a lower-cardinality column.` : error}</p>}
    {!error && !ready && <p>Choose rows, a pivot column, and a value to preview a server pivot.</p>}
    {!error && result && <PivotGrid result={result} />}
  </section>;
}

function FieldWell({ label, fields, value, onChange }: { label: string; fields: Table["columns"]; value: string; onChange: (field: string) => void }) {
  return <label onDragOver={(event) => event.preventDefault()} onDrop={(event) => { event.preventDefault(); onChange(event.dataTransfer.getData("text/plain")); }}>{label}<select aria-label={`Pivot ${label.toLowerCase()}`} value={value} onChange={(event) => onChange(event.target.value)}><option value="">Choose a column</option>{fields.map((field) => <option key={field.graphQlName} value={field.graphQlName}>{field.graphQlName}</option>)}</select></label>;
}

function ValueWell({ fields, value, aggregate, onColumn, onAggregate }: { fields: Table["columns"]; value: string; aggregate: PivotAggregate; onColumn: (value: string) => void; onAggregate: (value: PivotAggregate) => void }) {
  return <label onDragOver={(event) => event.preventDefault()} onDrop={(event) => { event.preventDefault(); onColumn(event.dataTransfer.getData("text/plain")); }}>Values <select aria-label="Pivot value column" value={value} onChange={(event) => onColumn(event.target.value)}><option value="">Choose a column</option>{fields.map((field) => <option key={field.graphQlName} value={field.graphQlName}>{field.graphQlName}</option>)}</select><select aria-label="Pivot aggregate" value={aggregate} onChange={(event) => onAggregate(event.target.value as PivotAggregate)}>{["count", "sum", "avg", "min", "max"].map((op) => <option key={op} value={op}>{op}</option>)}</select></label>;
}

export function PivotGrid({ result }: { result: PivotPayload }) {
  return <div className="bifrost-pivot-grid"><table><thead><tr><th colSpan={result.rowKeys.length}>Row keys</th><th colSpan={result.columns.length}>{result.pivotColumn}</th></tr><tr>{result.rowKeys.map((key) => <th key={key}>{key}</th>)}{result.columns.map((column) => <th key={column}>{displayPivotColumn(column)}</th>)}</tr></thead><tbody>{result.rows.map((row, index) => <tr key={index}>{result.rowKeys.map((key) => <td key={key}>{row[key] == null ? "" : String(row[key])}</td>)}{result.columns.map((column) => <td key={column}>{row.cells[column] == null ? "" : String(row.cells[column])}</td>)}</tr>)}</tbody></table></div>;
}
