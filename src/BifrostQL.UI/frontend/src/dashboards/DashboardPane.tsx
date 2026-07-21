import { useEffect, useMemo, useState, type CSSProperties } from "react";
import type { GraphQLFetcher, SavedObject, SavedObjectsClient } from "@standardbeagle/edit-db";
import { ChartPreview } from "../charts/ChartPane";
import { buildChartAggregateQuery, mapAggregateRows, parseChartDefinition, type ChartDefinition, type ChartPoint } from "../charts/chart-model";
import { toFilter } from "../designer/designer-state";
import { parseQueryDefinition } from "../designer/saved-query";
import type { VisualFilter } from "../lib/visual-query";
import { assertDashboardName, blankDashboard, type CountTileConfig, type DashboardDefinition, type DashboardLayout, type DashboardTile, type TableTileConfig } from "./dashboard-model";
import { dashboardStore, DASHBOARD_SAVED_OBJECT_TYPE, openDashboard, saveDashboard } from "./dashboard-store";
import "./dashboard.css";

type ResolvedTile = { kind: "chart"; config: ChartDefinition } | { kind: "count"; config: CountTileConfig } | { kind: "table"; config: TableTileConfig } | { error: string };
type SchemaTable = { graphQlName: string; columns: Array<{ graphQlName: string }> };

function tablePart(qualified: string): string { const parts = qualified.split("."); return parts[parts.length - 1] ?? qualified; }
/** Saved-query filters are parser-validated identifiers. Values still become GraphQL variables. */
function resolveSavedQueryFilter(filter: VisualFilter | null, tableRef: string): Record<string, unknown> | undefined {
  if (!filter) return undefined;
  if (filter.op === "leaf") {
    const criterion = filter.criterion;
    if (!criterion || criterion.table !== tableRef) throw new Error("The saved query filter is not scoped to its backing table.");
    return { [criterion.column]: { [criterion.operator]: criterion.value } };
  }
  const children = (filter.children ?? []).map((child) => resolveSavedQueryFilter(child, tableRef)).filter((child): child is Record<string, unknown> => !!child);
  return children.length ? { [filter.op]: children } : undefined;
}
function configFromSaved(tile: DashboardTile, object: SavedObject): ResolvedTile {
  if (tile.kind === "chart") {
    const chart = parseChartDefinition(object.definition);
    return chart ? { kind: "chart", config: chart } : { error: `Saved object ${object.id} is not a chart.` };
  }
  const query = parseQueryDefinition(object.definition);
  if (query?.state.tables.length === 1 && query.state.joins.length === 0) {
    const tableRef = query.state.tables[0].alias ?? query.state.tables[0].table;
    const table = tablePart(query.state.tables[0].table);
    try {
      const filter = resolveSavedQueryFilter(query.state.filter ?? toFilter(query.state), tableRef);
      const source = filter ? { filter, filterType: `TableFilter${table}Input` } : {};
      if (tile.kind === "count") return { kind: "count", config: { table, label: object.name, ...source } };
      return { kind: "table", config: { table, columns: query.state.columns.filter((column) => column.show).map((column) => column.column), limit: query.state.rowLimit ?? 10, ...source } };
    } catch (reason) { return { error: reason instanceof Error ? reason.message : String(reason) }; }
  }
  const config = object.definition;
  if (config && typeof config === "object" && "table" in config && typeof (config as { table?: unknown }).table === "string") {
    return tile.kind === "count"
      ? { kind: "count", config: config as CountTileConfig }
      : { kind: "table", config: config as TableTileConfig };
  }
  return { error: `Saved object ${object.id} cannot be rendered as a ${tile.kind} tile.` };
}

function resolveTile(tile: DashboardTile, saved: Map<string, SavedObject>): ResolvedTile {
  if (tile.ref) {
    const object = saved.get(tile.ref);
    if (!object) return { error: `Missing saved object: ${tile.ref}` };
    return configFromSaved(tile, object);
  }
  if (!tile.config) return { error: "Tile has no configuration." };
  if (tile.kind === "chart") {
    const chart = parseChartDefinition(tile.config);
    return chart ? { kind: "chart", config: chart } : { error: "Invalid chart tile configuration." };
  }
  const config = tile.config as CountTileConfig | TableTileConfig;
  if (typeof config.table !== "string") return { error: "Invalid tile configuration." };
  return tile.kind === "count" ? { kind: "count", config: config as CountTileConfig } : { kind: "table", config: config as TableTileConfig };
}

export function DashboardPane({ fetcher, store = dashboardStore, onOpenTable }: { fetcher: GraphQLFetcher; store?: SavedObjectsClient; onOpenTable?: (table: string) => void }) {
  const [objects, setObjects] = useState<SavedObject[]>([]);
  const [active, setActive] = useState<SavedObject | null>(null);
  const [definition, setDefinition] = useState<DashboardDefinition>(blankDashboard);
  const [name, setName] = useState("Untitled dashboard");
  const [editMode, setEditMode] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [schema, setSchema] = useState<SchemaTable[]>([]);
  const dashboards = useMemo(() => objects.filter((object) => openDashboard(object) !== null), [objects]);
  const savedById = useMemo(() => new Map(objects.map((object) => [object.id, object])), [objects]);

  const reload = async () => {
    try { setObjects(await store.list()); }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
  };
  useEffect(() => { void reload(); }, [store]);
  useEffect(() => { void fetcher.query<{ _dbSchema: SchemaTable[] }>("query DashboardSchema { _dbSchema { graphQlName columns { graphQlName } } }").then((result) => setSchema(result._dbSchema ?? [])).catch(() => { /* tile queries surface their own errors; schema only seeds new tiles */ }); }, [fetcher]);
  const open = (object: SavedObject) => {
    const parsed = openDashboard(object);
    if (!parsed) return;
    setActive(object); setDefinition(parsed); setName(object.name); setEditMode(false); setError(null);
  };
  const save = async () => {
    try {
      const object = await saveDashboard(store, { id: active?.id ?? crypto.randomUUID?.() ?? `dashboard-${Date.now()}`, name: name || "Untitled dashboard", definition, version: active?.version ?? 0 });
      setActive(object); setName(object.name); await reload();
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
  };
  const rename = async () => {
    if (!active) return;
    try { const object = await saveDashboard(store, { ...active, name: name || "Untitled dashboard", definition }); setActive(object); await reload(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
  };
  const removeDashboard = async () => {
    if (!active) return;
    try { await store.remove(DASHBOARD_SAVED_OBJECT_TYPE, active.id); setActive(null); setDefinition(blankDashboard()); setName("Untitled dashboard"); await reload(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
  };
  const updateTile = (id: string, update: (tile: DashboardTile) => DashboardTile) => setDefinition((current) => ({ ...current, tiles: current.tiles.map((tile) => tile.id === id ? update(tile) : tile) }));
  const removeTile = (id: string) => setDefinition((current) => ({ ...current, tiles: current.tiles.filter((tile) => tile.id !== id) }));
  const addTile = (kind: DashboardTile["kind"]) => setDefinition((current) => {
    const ordinal = current.tiles.length;
    const table = schema[0];
    const fields = table?.columns.map((column) => column.graphQlName) ?? [];
    const tile: DashboardTile = { id: crypto.randomUUID?.() ?? `tile-${Date.now()}-${ordinal}`, kind, title: `${kind[0].toUpperCase()}${kind.slice(1)} tile`, layout: { x: (ordinal * 3) % current.grid.cols, y: Math.floor(ordinal / 4) * 4, w: kind === "table" ? 6 : 3, h: kind === "chart" ? 6 : 4 }, config: kind === "chart" ? { kind: "bifrost.chart", version: 1, source: { kind: "table", table: table?.graphQlName ?? "" }, dimensions: fields.slice(0, 1), measures: [{ op: "count" }], chartType: "bar" } : { table: table?.graphQlName ?? "", ...(kind === "table" ? { columns: fields.slice(0, 4), limit: 10 } : {}) } };
    return { ...current, tiles: [...current.tiles, tile] };
  });
  const move = (id: string, target: DashboardLayout) => updateTile(id, (tile) => ({ ...tile, layout: { ...tile.layout, x: target.x, y: target.y } }));
  const resize = (id: string) => updateTile(id, (tile) => ({ ...tile, layout: { ...tile.layout, w: Math.min(definition.grid.cols, tile.layout.w + 1), h: tile.layout.h + 1 } }));

  return <section className="bifrost-dashboard-pane" aria-label="Dashboards">
    <aside className="bifrost-dashboard-list"><h2>Dashboards</h2>{editMode && <button type="button" onClick={() => { setActive(null); setDefinition(blankDashboard()); setName("Untitled dashboard"); }}>New dashboard</button>}{dashboards.map((object) => <button type="button" key={object.id} aria-pressed={active?.id === object.id} onClick={() => open(object)}>{object.name}</button>)}</aside>
    <main className="bifrost-dashboard-main"><div className="bifrost-dashboard-toolbar">{editMode
      ? <><label>Name <input aria-label="Dashboard name" value={name} onChange={(event) => setName(event.target.value)} /></label><button type="button" onClick={() => void save()}>Save dashboard</button>{active && <button type="button" onClick={() => void rename()}>Rename dashboard</button>}{active && <button type="button" onClick={() => void removeDashboard()}>Delete dashboard</button>}</>
      : <h2 className="bifrost-dashboard-title">{name}</h2>}
      <button type="button" aria-pressed={editMode} onClick={() => setEditMode((value) => !value)}>{editMode ? "View dashboard" : "Edit dashboard"}</button>{editMode && <><button type="button" onClick={() => addTile("chart")}>Add chart</button><button type="button" onClick={() => addTile("count")}>Add count</button><button type="button" onClick={() => addTile("table")}>Add table</button></>}</div>
      {error && <p role="alert">{error}</p>}
      <div className="bifrost-dashboard-grid" style={{ "--dashboard-columns": definition.grid.cols } as CSSProperties} aria-label="Dashboard tile grid">
        {definition.tiles.map((tile) => <DashboardTileView key={tile.id} tile={tile} resolved={resolveTile(tile, savedById)} fetcher={fetcher} editMode={editMode} onMove={move} onResize={resize} onRemove={removeTile} onOpenTable={onOpenTable} />)}
      </div>
    </main>
  </section>;
}

function DashboardTileView({ tile, resolved, fetcher, editMode, onMove, onResize, onRemove, onOpenTable }: { tile: DashboardTile; resolved: ResolvedTile; fetcher: GraphQLFetcher; editMode: boolean; onMove: (id: string, target: DashboardLayout) => void; onResize: (id: string) => void; onRemove: (id: string) => void; onOpenTable?: (table: string) => void }) {
  const [dragged, setDragged] = useState<string | null>(null);
  const style = { gridColumn: `${tile.layout.x + 1} / span ${tile.layout.w}`, gridRow: `${tile.layout.y + 1} / span ${tile.layout.h}` };
  return <article className="bifrost-dashboard-tile" style={style} draggable={editMode} onDragStart={(event) => { event.dataTransfer.setData("text/plain", tile.id); setDragged(tile.id); }} onDragEnd={() => setDragged(null)} onDragOver={(event) => { if (editMode) event.preventDefault(); }} onDrop={(event) => { event.preventDefault(); const id = event.dataTransfer.getData("text/plain") || dragged; if (editMode && id && id !== tile.id) onMove(id, tile.layout); }} aria-label={`${tile.title} tile`}>
    <h3>{tile.title}</h3>{editMode && <div className="bifrost-dashboard-tile__controls"><span className="bifrost-dashboard-drag" aria-label={`Drag ${tile.title}`} title="Drag tile">⠿</span><button type="button" aria-label={`Remove ${tile.title}`} onClick={() => onRemove(tile.id)}>Remove</button><button type="button" className="bifrost-dashboard-resize" aria-label={`Resize ${tile.title}`} onClick={() => onResize(tile.id)}>↘</button></div>}
    {"error" in resolved ? <p className="bifrost-dashboard-error" role="alert">{resolved.error}</p> : resolved.kind === "chart" ? <ChartTile fetcher={fetcher} definition={resolved.config} refreshSeconds={tile.refreshSeconds} /> : resolved.kind === "count" ? <CountTile fetcher={fetcher} config={resolved.config} refreshSeconds={tile.refreshSeconds} /> : <TableTile fetcher={fetcher} config={resolved.config} refreshSeconds={tile.refreshSeconds} onOpenTable={onOpenTable} />}
  </article>;
}

function useTileRequest<T>(run: () => Promise<T>, refreshSeconds?: number): { value: T | null; error: string | null } {
  const [state, setState] = useState<{ value: T | null; error: string | null }>({ value: null, error: null });
  useEffect(() => {
    let live = true;
    const request = () => { void run().then((value) => live && setState({ value, error: null })).catch((reason) => live && setState({ value: null, error: reason instanceof Error ? reason.message : String(reason) })); };
    request(); const timer = refreshSeconds ? window.setInterval(request, refreshSeconds * 1000) : undefined;
    return () => { live = false; if (timer) window.clearInterval(timer); };
  }, [run, refreshSeconds]);
  return state;
}

function ChartTile({ fetcher, definition, refreshSeconds }: { fetcher: GraphQLFetcher; definition: ChartDefinition; refreshSeconds?: number }) {
  const request = useMemo(() => () => { const built = buildChartAggregateQuery(definition); return fetcher.query<Record<string, Record<string, unknown>[]>>(built.query, built.variables).then((data) => mapAggregateRows(data[`${definition.source.table}Aggregate`] ?? [], definition)); }, [fetcher, definition]);
  const { value, error } = useTileRequest<ChartPoint[]>(request, refreshSeconds);
  if (error) return <p className="bifrost-dashboard-error" role="alert">Chart failed: {error}</p>;
  return value ? <ChartPreview type={definition.chartType} points={value} measures={definition.measures} /> : <p>Loading chart…</p>;
}

function CountTile({ fetcher, config, refreshSeconds }: { fetcher: GraphQLFetcher; config: CountTileConfig; refreshSeconds?: number }) {
  const request = useMemo(() => () => { assertDashboardName(config.table, "table"); if (config.filter && !config.filterType) throw new Error("Count filter is missing its schema-derived type."); if (config.filterType) assertDashboardName(config.filterType, "filter type"); const declaration = config.filter ? `($filter: ${config.filterType})` : ""; const argument = config.filter ? "(filter: $filter)" : ""; return fetcher.query<Record<string, { _count?: number }>>(`query DashboardCount${declaration} { ${config.table}Aggregate${argument} { _count } }`, config.filter ? { filter: config.filter } : {}).then((data) => Number(data[`${config.table}Aggregate`]?._count ?? 0)); }, [fetcher, config]);
  const { value, error } = useTileRequest<number>(request, refreshSeconds);
  if (error) return <p className="bifrost-dashboard-error" role="alert">Count failed: {error}</p>;
  return <p className="bifrost-dashboard-count" aria-label={config.label ?? config.table}>{value ?? "…"}</p>;
}

function TableTile({ fetcher, config, refreshSeconds, onOpenTable }: { fetcher: GraphQLFetcher; config: TableTileConfig; refreshSeconds?: number; onOpenTable?: (table: string) => void }) {
  const columns = config.columns.length ? config.columns : ["id"];
  const request = useMemo(() => () => { assertDashboardName(config.table, "table"); columns.forEach((column) => assertDashboardName(column, "table field")); if (config.filter && !config.filterType) throw new Error("Table filter is missing its schema-derived type."); if (config.filterType) assertDashboardName(config.filterType, "filter type"); const declaration = config.filter ? `, $filter: ${config.filterType}` : ""; const argument = config.filter ? ", filter: $filter" : ""; return fetcher.query<Record<string, Record<string, unknown>[]>>(`query DashboardTable($limit: Int!, $offset: Int!${declaration}) { ${config.table}(limit: $limit, offset: $offset${argument}) { ${columns.join(" ")} } }`, { limit: Math.min(Math.max(config.limit ?? 10, 1), 100), offset: 0, ...(config.filter ? { filter: config.filter } : {}) }).then((data) => data[config.table] ?? []); }, [fetcher, config, columns]);
  const { value, error } = useTileRequest<Record<string, unknown>[]>(request, refreshSeconds);
  if (error) return <p className="bifrost-dashboard-error" role="alert">Table failed: {error}</p>;
  if (!value) return <p>Loading table…</p>;
  return <><table className="bifrost-dashboard-table"><thead><tr>{columns.map((column) => <th key={column}>{column}</th>)}</tr></thead><tbody>{value.map((row, index) => <tr key={index}>{columns.map((column) => <td key={column}>{row[column] == null ? "" : String(row[column])}</td>)}</tr>)}</tbody></table>{onOpenTable && <button type="button" onClick={() => onOpenTable(config.table)}>Open full grid</button>}</>;
}
