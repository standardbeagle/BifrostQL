import { useEffect, useMemo, useState } from "react";
import { downloadTextFile, exportAllRows, filenameFor, mimeFor, type GraphQLFetcher, type SavedObject, type SavedObjectsClient } from "@standardbeagle/edit-db";
import { parseQueryDefinition, type SavedQueryDefinition } from "../designer/saved-query";
import { toFilter } from "../designer/designer-state";
import type { VisualFilter } from "../lib/visual-query";
import { buildPivotQuery, displayPivotColumn, parsePivotPayload, type PivotAggregate, type PivotDefinition, type PivotPayload, type ResolvedSavedQueryPivotSource } from "./pivot-model";
import { openPivot, PIVOT_SAVED_OBJECT_TYPE, pivotStore, savePivot } from "./pivot-store";

type Table = { graphQlName: string; columns: Array<{ graphQlName: string }> };
const empty = (table = ""): PivotDefinition => ({ kind: "bifrost.pivot", version: 1, source: { kind: "table", table }, rowKeys: [], pivotColumn: "", valueColumn: "", aggregate: "count" });

function tablePart(qualified: string): string { const parts = qualified.split("."); return parts[parts.length - 1] ?? qualified; }

/** Converts a one-table saved designer filter into the public table-filter input.
 * All identifiers were validated by the saved-query parser; this only moves the
 * user values into GraphQL variables, never into the document text. */
function resolveVisualFilter(filter: VisualFilter | null, tableRef: string): Record<string, unknown> | undefined {
  if (!filter) return undefined;
  if (filter.op === "leaf") {
    const criterion = filter.criterion;
    if (!criterion || criterion.table !== tableRef) throw new Error("The saved query filter is not scoped to its backing table.");
    return { [criterion.column]: { [criterion.operator]: criterion.value } };
  }
  const children = (filter.children ?? []).map((child) => resolveVisualFilter(child, tableRef)).filter((child): child is Record<string, unknown> => !!child);
  return children.length ? { [filter.op]: children } : undefined;
}

/** Resolve a saved visual query into the exact table/filter context accepted by
 * the server pivot field. Multi-table saved queries remain unsupported instead
 * of producing a deceptively different pivot. */
export function resolveSavedQueryPivotSource(saved: SavedQueryDefinition, tables: readonly Table[]): ResolvedSavedQueryPivotSource {
  if (saved.state.tables.length !== 1 || saved.state.joins.length !== 0) throw new Error("Pivoting a saved query requires one backing table and no joins.");
  const tableRef = saved.state.tables[0].alias ?? saved.state.tables[0].table;
  const table = tables.find((candidate) => candidate.graphQlName === tablePart(saved.state.tables[0].table));
  if (!table) throw new Error("The saved query backing table is not present in this schema.");
  const filter = resolveVisualFilter(saved.state.filter ?? toFilter(saved.state), tableRef);
  return { table: table.graphQlName, filter, filterType: filter ? `TableFilter${table.graphQlName}Input` : undefined };
}

/** Export the displayed server matrix through edit-db's shared 3.3 serializer. */
export async function buildPivotCsv(result: PivotPayload): Promise<string> {
  const headers = [...result.rowKeys, ...result.columns.map(displayPivotColumn)];
  const exported = await exportAllRows({
    headers, format: "csv",
    fetchPage: async (offset, limit) => ({
      total: result.rows.length,
      rows: result.rows.slice(offset, offset + limit).map((row) => [...result.rowKeys.map((key) => row[key]), ...result.columns.map((column) => row.cells[column])]),
    }),
  });
  return exported.content;
}

export function PivotPane({ fetcher, initialDefinition, store = pivotStore }: { fetcher: GraphQLFetcher; initialDefinition?: PivotDefinition | null; store?: SavedObjectsClient }) {
  const [definition, setDefinition] = useState<PivotDefinition>(() => initialDefinition ?? empty());
  const [tables, setTables] = useState<Table[]>([]);
  const [saved, setSaved] = useState<SavedObject[]>([]);
  const [name, setName] = useState("Untitled pivot");
  const [result, setResult] = useState<PivotPayload | null>(null);
  const [error, setError] = useState<string | null>(null);
  const savedQueries = useMemo(() => saved.flatMap((object) => {
    const parsed = parseQueryDefinition(object.definition);
    return object.type === PIVOT_SAVED_OBJECT_TYPE && parsed ? [{ object, definition: parsed }] : [];
  }), [saved]);
  const resolved = useMemo(() => {
    if (definition.source.kind !== "saved-query") return { definition, error: null as string | null };
    const query = savedQueries.find(({ object }) => object.id === definition.source.savedQueryRef);
    if (!query) return { definition: null, error: "Choose a saved query source." };
    try {
      const source = resolveSavedQueryPivotSource(query.definition, tables);
      return { definition: { ...definition, source: { ...definition.source, ...source } }, error: null as string | null };
    } catch (reason) { return { definition: null, error: reason instanceof Error ? reason.message : String(reason) }; }
  }, [definition, savedQueries, tables]);
  const active = resolved.definition;
  const selected = tables.find((table) => table.graphQlName === active?.source.table);
  const fields = selected?.columns ?? [];
  const ready = !!active && active.rowKeys.length > 0 && !!active.pivotColumn && !!active.valueColumn;

  useEffect(() => { void fetcher.query<{ _dbSchema: Table[] }>("query PivotSchema { _dbSchema { graphQlName columns { graphQlName } } }").then((data) => setTables(data._dbSchema ?? [])).catch((reason) => setError(String(reason))); }, [fetcher]);
  useEffect(() => { void store.list(PIVOT_SAVED_OBJECT_TYPE).then(setSaved).catch((reason) => setError(String(reason))); }, [store]);
  useEffect(() => { if (initialDefinition) setDefinition(initialDefinition); }, [initialDefinition]);
  useEffect(() => {
    if (!ready || !active) return;
    let live = true;
    const timer = window.setTimeout(() => {
      try {
        const { query, variables } = buildPivotQuery(active);
        setError(null);
        void fetcher.query<Record<string, unknown>>(query, variables).then((data) => {
          if (live) setResult(parsePivotPayload(data[`${active.source.table}Pivot`]));
        }).catch((reason) => { if (live) setError(reason instanceof Error ? reason.message : String(reason)); });
      } catch (reason) { if (live) setError(reason instanceof Error ? reason.message : String(reason)); }
    }, 250);
    return () => { live = false; window.clearTimeout(timer); };
  }, [fetcher, active, ready]);

  const update = (patch: Partial<PivotDefinition>) => setDefinition((current) => ({ ...current, ...patch }));
  const chooseTable = (table: string) => update({ source: { kind: "table", table }, rowKeys: [], pivotColumn: "", valueColumn: "" });
  const addRow = (field: string) => { if (field && field !== definition.pivotColumn) update({ rowKeys: [...definition.rowKeys.filter((key) => key !== field), field] }); };
  const removeRow = (field: string) => update({ rowKeys: definition.rowKeys.filter((key) => key !== field) });
  const save = async () => { try { const object = await savePivot(store, { id: crypto.randomUUID?.() ?? `pivot-${Date.now()}`, name: name || "Untitled pivot", definition, version: 0 }); setSaved((items) => [...items.filter((item) => item.id !== object.id), object]); } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); } };
  const load = (id: string) => { const object = saved.find((item) => item.id === id); const parsed = object && openPivot(object); if (parsed) { setDefinition(parsed); setName(object.name); } };
  const exportMatrix = async () => { if (!result) return; try { downloadTextFile(await buildPivotCsv(result), filenameFor("pivot", "csv"), mimeFor("csv")); } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); } };
  const sourceKind = definition.source.kind;
  const visibleError = error ?? resolved.error;
  return <section className="bifrost-pivot-pane" aria-label="Pivot panel">
    <header><h2>Pivot</h2><label>Table <select aria-label="Pivot table" value={active?.source.table ?? definition.source.table} onChange={(event) => chooseTable(event.target.value)}><option value="">Choose a table</option>{tables.map((table) => <option key={table.graphQlName} value={table.graphQlName}>{table.graphQlName}</option>)}</select></label>
      <label>Source <select aria-label="Pivot source kind" value={sourceKind} onChange={(event) => update({ source: { ...definition.source, kind: event.target.value as "table" | "saved-query" } })}><option value="table">Table</option><option value="saved-query">Saved query</option></select></label>
      {sourceKind === "saved-query" && <label>Saved query <select aria-label="Pivot saved query" value={definition.source.savedQueryRef ?? ""} onChange={(event) => update({ source: { ...definition.source, savedQueryRef: event.target.value } })}><option value="">Choose a saved query</option>{savedQueries.map(({ object }) => <option value={object.id} key={object.id}>{object.name}</option>)}</select></label>}</header>
    <p className="bifrost-pivot-field-list" aria-label="Pivot fields">Drag a schema field into a well: {fields.map((field) => <button key={field.graphQlName} type="button" draggable onDragStart={(event) => event.dataTransfer.setData("text/plain", field.graphQlName)}>{field.graphQlName}</button>)}</p>
    <div className="bifrost-pivot-wells"><RowsWell fields={fields} values={definition.rowKeys} onAdd={addRow} onRemove={removeRow} /><FieldWell label="Columns" fields={fields} value={definition.pivotColumn} onChange={(pivotColumn) => update({ pivotColumn, rowKeys: definition.rowKeys.filter((key) => key !== pivotColumn) })} /><ValueWell fields={fields} value={definition.valueColumn} aggregate={definition.aggregate} onColumn={(valueColumn) => update({ valueColumn })} onAggregate={(aggregate) => update({ aggregate })} /></div>
    <div className="bifrost-pivot-save"><label>Pivot name <input aria-label="Pivot name" value={name} onChange={(event) => setName(event.target.value)} /></label><button type="button" onClick={() => void save()}>Save pivot</button><label>Open saved pivot <select aria-label="Open saved pivot" defaultValue="" onChange={(event) => load(event.target.value)}><option value="">Open a saved pivot</option>{saved.filter((item) => openPivot(item) !== null).map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>{result && <button type="button" onClick={() => void exportMatrix()}>Export CSV</button>}</div>
    {visibleError && <p role="alert">{visibleError.includes("distinct values") ? `${visibleError} Add a filter or choose a lower-cardinality column.` : visibleError}</p>}
    {!visibleError && !ready && <p>Choose rows, a pivot column, and a value to preview a server pivot.</p>}
    {result && <PivotGrid result={result} />}
  </section>;
}

function RowsWell({ fields, values, onAdd, onRemove }: { fields: Table["columns"]; values: string[]; onAdd: (field: string) => void; onRemove: (field: string) => void }) {
  return <fieldset aria-label="Pivot rows" onDragOver={(event) => event.preventDefault()} onDrop={(event) => { event.preventDefault(); onAdd(event.dataTransfer.getData("text/plain")); }}><legend>Rows</legend><select aria-label="Add pivot row" value="" onChange={(event) => onAdd(event.target.value)}><option value="">Add a column</option>{fields.filter((field) => !values.includes(field.graphQlName)).map((field) => <option key={field.graphQlName} value={field.graphQlName}>{field.graphQlName}</option>)}</select><ul>{values.map((field) => <li key={field}>{field} <button type="button" aria-label={`Remove pivot row ${field}`} onClick={() => onRemove(field)}>Remove</button></li>)}</ul></fieldset>;
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
