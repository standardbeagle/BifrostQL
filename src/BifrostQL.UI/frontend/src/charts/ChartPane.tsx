import { useEffect, useMemo, useState } from "react";
import type { GraphQLFetcher } from "@standardbeagle/edit-db";
import { Area, AreaChart, Bar, BarChart, CartesianGrid, Legend, Line, LineChart, Pie, PieChart, ResponsiveContainer, Sankey, Tooltip, XAxis, YAxis } from "recharts";
import { buildChartAggregateQuery, drillFilter, mapChartData, measureKey, type ChartData, type ChartDefinition, type ChartDrillFilter, type ChartMeasure, type ChartType } from "./chart-model";
import { chartStore, CHART_SAVED_OBJECT_TYPE, openChart, saveChart } from "./chart-store";
import type { SavedObject, SavedObjectsClient } from "@standardbeagle/edit-db";

const palette = ["var(--accent-action)", "var(--accent-success)", "var(--accent-warning)", "var(--text-secondary)"];
const emptyDefinition = (table: string): ChartDefinition => ({ kind: "bifrost.chart", version: 1, source: { kind: "table", table }, dimensions: [], measures: [{ op: "count" }], chartType: "bar", limit: 100 });

export function ChartPane({ fetcher, initialDefinition, onInitialDefinitionConsumed, onDrill, store = chartStore }: { fetcher: GraphQLFetcher; initialDefinition?: ChartDefinition | null; onInitialDefinitionConsumed?: () => void; onDrill?: (table: string, filters: ChartDrillFilter[]) => void; store?: SavedObjectsClient }) {
  const [definition, setDefinition] = useState<ChartDefinition>(() => initialDefinition ?? emptyDefinition(""));
  const [tables, setTables] = useState<Array<{ graphQlName: string; columns: Array<{ graphQlName: string }> }>>([]);
  const [savedCharts, setSavedCharts] = useState<SavedObject[]>([]);
  const [chartName, setChartName] = useState("Untitled chart");
  const [data, setData] = useState<ChartData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const columns = useMemo(() => definition.dimensions, [definition.dimensions]);
  const selectedTable = tables.find((table) => table.graphQlName === definition.source.table);
  // These are the field names declared by MetadataSchemaGenerator's _dbSchema
  // surface. Labels are a client projection and are deliberately not queried.
  useEffect(() => { void fetcher.query<{ _dbSchema: typeof tables }>("query ChartSchema { _dbSchema { graphQlName columns { graphQlName } } }").then((value) => setTables(value._dbSchema ?? [])); }, [fetcher]);
  useEffect(() => { void store.list(CHART_SAVED_OBJECT_TYPE).then((objects) => setSavedCharts(objects.filter((object) => openChart(object) !== null))); }, [store]);
  // Consuming the seed lets the shell clear it (mirrors the query builder's
  // onOpenHandled) — otherwise a later visit to the Charts pane re-seeds the
  // stale definition from the last Visualize.
  useEffect(() => { if (initialDefinition) { setDefinition(initialDefinition); onInitialDefinitionConsumed?.(); } }, [initialDefinition]);
  useEffect(() => {
    if (!definition.source.table || !definition.dimensions.length) return;
    if (definition.chartType === "sankey" && definition.dimensions.length < 2) return;
    let built;
    try { built = buildChartAggregateQuery(definition); }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); return; }
    const { query, variables } = built;
    let live = true;
    setData(null); setError(null);
    void fetcher.query<Record<string, Record<string, unknown>[]>>(query, variables).then((result) => {
      if (live) setData(mapChartData(result[`${definition.source.table}Aggregate`] ?? [], definition));
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
      <label>Dimension <select aria-label="Chart dimension" value={columns[0] ?? ""} onChange={(e) => update({ dimensions: e.target.value ? [e.target.value, ...columns.slice(1)] : [] })}><option value="">Choose a column</option>{selectedTable?.columns.map((column) => <option key={column.graphQlName} value={column.graphQlName}>{column.graphQlName}</option>)}</select></label>
      {definition.chartType === "sankey" && <label>Flow to <select aria-label="Sankey target dimension" value={columns[1] ?? ""} onChange={(e) => update({ dimensions: e.target.value ? [columns[0] ?? "", e.target.value] : columns.slice(0, 1) })}><option value="">Choose a column</option>{selectedTable?.columns.map((column) => <option key={column.graphQlName} value={column.graphQlName}>{column.graphQlName}</option>)}</select></label>}
      <label>Type <select aria-label="Chart type" value={definition.chartType} onChange={(e) => update({ chartType: e.target.value as ChartType })}><option value="bar">Bar</option><option value="line">Line</option><option value="pie">Pie</option><option value="area">Area</option><option value="sankey">Sankey</option></select></label>
      <button type="button" onClick={addMeasure}>Add measure</button></header>
    <div className="bifrost-chart-save"><label>Chart name <input aria-label="Chart name" value={chartName} onChange={(e) => setChartName(e.target.value)} /></label><button type="button" onClick={() => void save()}>Save chart</button><label>Open saved chart <select aria-label="Open saved chart" defaultValue="" onChange={(e) => load(e.target.value)}><option value="">Open a saved chart</option>{savedCharts.map((chart) => <option key={chart.id} value={chart.id}>{chart.name}</option>)}</select></label></div>
    <div className="bifrost-chart-measures">{definition.measures.map((measure, index) => <MeasureEditor key={index} measure={measure} columns={selectedTable?.columns ?? []} onChange={(next) => update({ measures: definition.measures.map((m, i) => i === index ? next : m) })} />)}</div>
    {error && <p role="alert">{error}</p>}
    {!error && data === null && <p>Choose a table and dimension to preview a server aggregate.</p>}
    {!error && data && isEmptyChartData(data) && <p role="status">No data matches this chart.</p>}
    {data && !isEmptyChartData(data) && <ChartPreview type={definition.chartType} data={data} measures={definition.measures} dimensions={definition.dimensions}
      onDrill={onDrill && definition.source.table ? (filters) => onDrill(definition.source.table, filters) : undefined} />}
  </section>;
}

function isEmptyChartData(data: ChartData): boolean {
  return data.kind === "sankey" ? data.links.length === 0 : data.points.length === 0;
}

function MeasureEditor({ measure, columns, onChange }: { measure: ChartMeasure; columns: Array<{ graphQlName: string }>; onChange: (next: ChartMeasure) => void }) {
  return <label>Measure <select aria-label="Measure operation" value={measure.op} onChange={(e) => onChange({ ...measure, op: e.target.value as ChartMeasure["op"] })}><option value="count">Count</option><option value="sum">Sum</option><option value="avg">Average</option><option value="min">Min</option><option value="max">Max</option></select>{measure.op !== "count" && <select aria-label="Measure column" value={measure.column ?? ""} onChange={(e) => onChange({ ...measure, column: e.target.value })}><option value="">Choose a column</option>{columns.map((column) => <option key={column.graphQlName} value={column.graphQlName}>{column.graphQlName}</option>)}</select>}</label>;
}

// Recharts draws sankey node RECTANGLES only; the label is the custom shape's
// job. Labels sit INWARD (source column labels right of its bar, target column
// labels left of its bar) — the conventional sankey look, verified against the
// recorded demo. Column detection: recharts' `payload.sourceLinks` holds the
// links ARRIVING at a node (despite the name), so an empty list marks the
// source column. Exact for the two-column flows this chart type builds.
function SankeyNodeShape(props: {
  x?: number; y?: number; width?: number; height?: number;
  payload?: { name?: string; sourceLinks?: unknown[] };
}) {
  const { x = 0, y = 0, width = 0, height = 0, payload } = props;
  const isSourceColumn = (payload?.sourceLinks?.length ?? 0) === 0;
  return <g>
    <rect x={x} y={y} width={width} height={height} fill={palette[0]} fillOpacity={0.9} />
    <text x={isSourceColumn ? x + width + 6 : x - 6} y={y + height / 2}
      textAnchor={isSourceColumn ? "start" : "end"} dominantBaseline="middle"
      fill="var(--text-secondary)" fontSize={12}>{payload?.name ?? ""}</text>
  </g>;
}

// Recharts computes the ribbon geometry and clones this element with it; the
// shape only draws the default bezier — it exists so a band can take a CLICK
// (the stock link object cannot), which is what makes a sankey navigable:
// clicking a flow drills to the grid filtered to that source/target pair.
function SankeyLinkShape(props: {
  sourceX?: number; targetX?: number; sourceY?: number; targetY?: number;
  sourceControlX?: number; targetControlX?: number; linkWidth?: number;
  payload?: { source?: { name?: string }; target?: { name?: string } };
  onDrillFlow?: (source: string, target: string) => void;
}) {
  const { sourceX = 0, targetX = 0, sourceY = 0, targetY = 0, sourceControlX = 0, targetControlX = 0, linkWidth = 0, payload, onDrillFlow } = props;
  const source = payload?.source?.name;
  const target = payload?.target?.name;
  const drill = onDrillFlow && source !== undefined && target !== undefined
    ? () => onDrillFlow(source, target)
    : undefined;
  return <path
    className="bifrost-sankey-link"
    d={`M${sourceX},${sourceY}C${sourceControlX},${sourceY} ${targetControlX},${targetY} ${targetX},${targetY}`}
    fill="none" stroke="var(--accent-action)" strokeOpacity={0.35} strokeWidth={linkWidth}
    style={drill ? { cursor: "pointer" } : undefined} onClick={drill} />;
}

export function ChartPreview({ type, data, measures, dimensions, onDrill }: { type: ChartType; data: ChartData; measures: ChartMeasure[]; dimensions?: string[]; onDrill?: (filters: ChartDrillFilter[]) => void }) {
  const [dimension, flowDimension] = dimensions ?? [];
  const drillCategory = onDrill && dimension
    ? (category: unknown) => { if (typeof category === "string") onDrill([drillFilter(dimension, category)]); }
    : undefined;
  const drillFlow = onDrill && dimension && flowDimension
    ? (source: string, target: string) => onDrill([drillFilter(dimension, source), drillFilter(flowDimension, target)])
    : undefined;
  if (data.kind === "sankey") {
    // Recharts lays a sankey out itself from nodes+indexed links; no axes apply.
    return <div className="bifrost-chart-preview" data-theme-palette="tokens"><ResponsiveContainer width="100%" height={320}>
      <Sankey data={{ nodes: data.nodes, links: data.links }} nodePadding={24}
        node={<SankeyNodeShape />}
        link={<SankeyLinkShape onDrillFlow={drillFlow} />}
        margin={{ top: 16, right: 130, bottom: 16, left: 130 }}>
        <Tooltip />
      </Sankey>
    </ResponsiveContainer></div>;
  }
  const points = data.points.map((point) => ({ category: point.category, ...point.values }));
  const series = measures.map(measureKey);
  const common = series.map((key, index) => ({ key, stroke: palette[index % palette.length] }));
  const clickEntry = drillCategory
    ? (entry: { payload?: { category?: unknown }; category?: unknown } | undefined) => drillCategory(entry?.payload?.category ?? entry?.category)
    : undefined;
  if (type === "pie") return <div className="bifrost-chart-preview" data-theme-palette="tokens"><ResponsiveContainer width="100%" height={320}><PieChart><Tooltip /><Legend /><Pie data={points} dataKey={series[0]} nameKey="category" fill={palette[0]} onClick={clickEntry} cursor={clickEntry ? "pointer" : undefined} /></PieChart></ResponsiveContainer></div>;
  const Chart = type === "bar" ? BarChart : type === "line" ? LineChart : AreaChart;
  return <div className="bifrost-chart-preview" data-theme-palette="tokens"><ResponsiveContainer width="100%" height={320}><Chart data={points}><CartesianGrid stroke="var(--ui-border)" /><XAxis dataKey="category" stroke="var(--text-secondary)" /><YAxis stroke="var(--text-secondary)" /><Tooltip /><Legend />{common.map((s) => type === "bar" ? <Bar key={s.key} dataKey={s.key} fill={s.stroke} onClick={clickEntry} cursor={clickEntry ? "pointer" : undefined} /> : type === "line" ? <Line key={s.key} dataKey={s.key} stroke={s.stroke} /> : <Area key={s.key} dataKey={s.key} stroke={s.stroke} fill={s.stroke} />)}</Chart></ResponsiveContainer></div>;
}
