import { useEffect, useMemo, useState } from "react";
import type { GraphQLFetcher } from "@standardbeagle/edit-db";
import { Area, AreaChart, Bar, BarChart, CartesianGrid, Legend, Line, LineChart, Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { buildChartAggregateQuery, mapAggregateRows, measureKey, type ChartDefinition, type ChartMeasure, type ChartPoint, type ChartType } from "./chart-model";
import { chartStore, CHART_SAVED_OBJECT_TYPE, openChart, saveChart } from "./chart-store";
import type { SavedObject, SavedObjectsClient } from "@standardbeagle/edit-db";

const palette = ["var(--accent-action)", "var(--accent-success)", "var(--accent-warning)", "var(--text-secondary)"];
const emptyDefinition = (table: string): ChartDefinition => ({ kind: "bifrost.chart", version: 1, source: { kind: "table", table }, dimensions: [], measures: [{ op: "count" }], chartType: "bar", limit: 100 });

export function ChartPane({ fetcher, initialDefinition, store = chartStore }: { fetcher: GraphQLFetcher; initialDefinition?: ChartDefinition | null; store?: SavedObjectsClient }) {
  const [definition, setDefinition] = useState<ChartDefinition>(() => initialDefinition ?? emptyDefinition(""));
  const [tables, setTables] = useState<Array<{ graphQlName: string; columns: Array<{ graphQlName: string }> }>>([]);
  const [savedCharts, setSavedCharts] = useState<SavedObject[]>([]);
  const [chartName, setChartName] = useState("Untitled chart");
  const [points, setPoints] = useState<ChartPoint[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const columns = useMemo(() => definition.dimensions, [definition.dimensions]);
  const selectedTable = tables.find((table) => table.graphQlName === definition.source.table);
  // These are the field names declared by MetadataSchemaGenerator's _dbSchema
  // surface. Labels are a client projection and are deliberately not queried.
  useEffect(() => { void fetcher.query<{ _dbSchema: typeof tables }>("query ChartSchema { _dbSchema { graphQlName columns { graphQlName } } }").then((value) => setTables(value._dbSchema ?? [])); }, [fetcher]);
  useEffect(() => { void store.list(CHART_SAVED_OBJECT_TYPE).then((objects) => setSavedCharts(objects.filter((object) => openChart(object) !== null))); }, [store]);
  useEffect(() => { if (initialDefinition) setDefinition(initialDefinition); }, [initialDefinition]);
  useEffect(() => {
    if (!definition.source.table || !definition.dimensions.length) return;
    const { query, variables } = buildChartAggregateQuery(definition);
    let live = true;
    setPoints(null); setError(null);
    void fetcher.query<Record<string, Record<string, unknown>[]>>(query, variables).then((result) => {
      if (live) setPoints(mapAggregateRows(result[`${definition.source.table}Aggregate`] ?? [], definition));
    }).catch((reason) => live && setError(reason instanceof Error ? reason.message : String(reason)));
    return () => { live = false; };
  }, [fetcher, definition]);
  const update = (patch: Partial<ChartDefinition>) => setDefinition((value) => ({ ...value, ...patch }));
  const addMeasure = () => update({ measures: [...definition.measures, { op: "count" }] });
  const save = async () => {
    try {
      const object = await saveChart(store, { id: crypto.randomUUID?.() ?? `chart-${Date.now()}`, name: chartName || "Untitled chart", definition, version: 0 });
      setSavedCharts((current) => [...current.filter((item) => item.id !== object.id), object]);
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
  };
  const load = (id: string) => {
    const chart = savedCharts.find((object) => object.id === id);
    const parsed = chart && openChart(chart);
    if (parsed) { setDefinition(parsed); setChartName(chart.name); }
  };
  return <section className="bifrost-chart-pane" aria-label="Chart panel">
    <header><h2>Chart</h2><label>Table <select aria-label="Chart table" value={definition.source.table} onChange={(e) => update({ source: { ...definition.source, table: e.target.value }, dimensions: [] })}><option value="">Choose a table</option>{tables.map((table) => <option key={table.graphQlName} value={table.graphQlName}>{table.graphQlName}</option>)}</select></label>
      <label>Dimension <select aria-label="Chart dimension" value={columns[0] ?? ""} onChange={(e) => update({ dimensions: e.target.value ? [e.target.value] : [] })}><option value="">Choose a column</option>{selectedTable?.columns.map((column) => <option key={column.graphQlName} value={column.graphQlName}>{column.graphQlName}</option>)}</select></label>
      <label>Type <select aria-label="Chart type" value={definition.chartType} onChange={(e) => update({ chartType: e.target.value as ChartType })}><option value="bar">Bar</option><option value="line">Line</option><option value="pie">Pie</option><option value="area">Area</option></select></label>
      <button type="button" onClick={addMeasure}>Add measure</button></header>
    <div className="bifrost-chart-save"><label>Chart name <input aria-label="Chart name" value={chartName} onChange={(e) => setChartName(e.target.value)} /></label><button type="button" onClick={() => void save()}>Save chart</button><label>Open saved chart <select aria-label="Open saved chart" defaultValue="" onChange={(e) => load(e.target.value)}><option value="">Open a saved chart</option>{savedCharts.map((chart) => <option key={chart.id} value={chart.id}>{chart.name}</option>)}</select></label></div>
    <div className="bifrost-chart-measures">{definition.measures.map((measure, index) => <MeasureEditor key={index} measure={measure} columns={selectedTable?.columns ?? []} onChange={(next) => update({ measures: definition.measures.map((m, i) => i === index ? next : m) })} />)}</div>
    {error && <p role="alert">{error}</p>}
    {!error && points === null && <p>Choose a table and dimension to preview a server aggregate.</p>}
    {!error && points?.length === 0 && <p role="status">No data matches this chart.</p>}
    {points && points.length > 0 && <ChartPreview type={definition.chartType} points={points} measures={definition.measures} />}
  </section>;
}

function MeasureEditor({ measure, columns, onChange }: { measure: ChartMeasure; columns: Array<{ graphQlName: string }>; onChange: (next: ChartMeasure) => void }) {
  return <label>Measure <select aria-label="Measure operation" value={measure.op} onChange={(e) => onChange({ ...measure, op: e.target.value as ChartMeasure["op"] })}><option value="count">Count</option><option value="sum">Sum</option><option value="avg">Average</option><option value="min">Min</option><option value="max">Max</option></select>{measure.op !== "count" && <select aria-label="Measure column" value={measure.column ?? ""} onChange={(e) => onChange({ ...measure, column: e.target.value })}><option value="">Choose a column</option>{columns.map((column) => <option key={column.graphQlName} value={column.graphQlName}>{column.graphQlName}</option>)}</select>}</label>;
}

export function ChartPreview({ type, points, measures }: { type: ChartType; points: ChartPoint[]; measures: ChartMeasure[] }) {
  const data = points.map((point) => ({ category: point.category, ...point.values }));
  const series = measures.map(measureKey);
  const common = series.map((key, index) => ({ key, stroke: palette[index % palette.length] }));
  if (type === "pie") return <div className="bifrost-chart-preview" data-theme-palette="tokens"><ResponsiveContainer width="100%" height={320}><PieChart><Tooltip /><Legend /><Pie data={data} dataKey={series[0]} nameKey="category" fill={palette[0]} /></PieChart></ResponsiveContainer></div>;
  const Chart = type === "bar" ? BarChart : type === "line" ? LineChart : AreaChart;
  return <div className="bifrost-chart-preview" data-theme-palette="tokens"><ResponsiveContainer width="100%" height={320}><Chart data={data}><CartesianGrid stroke="var(--ui-border)" /><XAxis dataKey="category" stroke="var(--text-secondary)" /><YAxis stroke="var(--text-secondary)" /><Tooltip /><Legend />{common.map((s) => type === "bar" ? <Bar key={s.key} dataKey={s.key} fill={s.stroke} /> : type === "line" ? <Line key={s.key} dataKey={s.key} stroke={s.stroke} /> : <Area key={s.key} dataKey={s.key} stroke={s.stroke} fill={s.stroke} />)}</Chart></ResponsiveContainer></div>;
}
