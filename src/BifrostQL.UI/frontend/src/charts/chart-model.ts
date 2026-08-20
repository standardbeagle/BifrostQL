export type ChartType = "bar" | "line" | "pie" | "area" | "sankey";
export type MeasureOp = "count" | "sum" | "avg" | "min" | "max";

export interface ChartMeasure { column?: string; op: MeasureOp; }
export interface ChartSource {
  kind: "table" | "saved-query";
  table: string;
  /** Filter is built by edit-db's schema-aware query-builder, never user text. */
  filter?: Record<string, unknown>;
  filterType?: string;
  savedQueryRef?: string;
}
export interface ChartDefinition {
  kind: "bifrost.chart";
  version: 1;
  source: ChartSource;
  dimensions: string[];
  measures: ChartMeasure[];
  chartType: ChartType;
  sort?: "asc" | "desc";
  limit?: number;
}

export interface ChartPoint extends Record<string, unknown> {
  category: string;
  values: Record<string, number | null>;
}

/** Recharts Sankey shape: links index into the node array. */
export interface SankeyNode { name: string; }
export interface SankeyLink { source: number; target: number; value: number; }
export type ChartData =
  | { kind: "cartesian"; points: ChartPoint[] }
  | { kind: "sankey"; nodes: SankeyNode[]; links: SankeyLink[] };

export const NULL_CATEGORY_LABEL = "(null)";
export const MAX_CHART_CATEGORIES = 100;

const GRAPHQL_NAME = /^[_A-Za-z][_0-9A-Za-z]*$/;
/** The values passed here are schema-derived dropdown selections, never query text. */
function assertGraphQlName(value: unknown, kind: string): asserts value is string {
  if (typeof value !== "string" || !GRAPHQL_NAME.test(value)) throw new Error(`Invalid GraphQL ${kind}.`);
}
const names = (values: string[]) => values.forEach((value) => assertGraphQlName(value, "chart field"));

export function parseChartDefinition(value: unknown): ChartDefinition | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const candidate = value as Partial<ChartDefinition>;
  if (candidate.kind !== "bifrost.chart" || candidate.version !== 1 || !candidate.source ||
    !["table", "saved-query"].includes(candidate.source.kind ?? "") || typeof candidate.source.table !== "string" ||
    !Array.isArray(candidate.dimensions) || !candidate.dimensions.every((v) => typeof v === "string") ||
    !Array.isArray(candidate.measures) || !candidate.measures.every((m) => m && ["count", "sum", "avg", "min", "max"].includes(m.op) && (m.op === "count" || typeof m.column === "string")) ||
    !["bar", "line", "pie", "area", "sankey"].includes(candidate.chartType ?? "")) return null;
  try {
    assertGraphQlName(candidate.source.table, "chart table");
    names(candidate.dimensions);
    candidate.measures.forEach((m) => { if (m.column) assertGraphQlName(m.column, "chart measure"); });
  } catch { return null; }
  return candidate as ChartDefinition;
}

function selectionFor(measure: ChartMeasure): string {
  if (measure.op === "count") return "_count";
  assertGraphQlName(measure.column, "chart measure");
  return `_${measure.op} { ${measure.column} }`;
}

/** Builds only from previously schema-validated references. Filter values travel as variables. */
export function buildChartAggregateQuery(definition: ChartDefinition): { query: string; variables: Record<string, unknown> } {
  const parsed = parseChartDefinition(definition);
  if (!parsed) throw new Error("Invalid chart definition.");
  // A sankey is a FLOW between two categorical dimensions; every other type
  // charts one. Extra stored dimensions are ignored rather than smuggled into
  // groupBy, so switching a saved sankey to a bar chart degrades cleanly.
  const dimensions = parsed.chartType === "sankey" ? parsed.dimensions.slice(0, 2) : parsed.dimensions.slice(0, 1);
  if (parsed.chartType === "sankey") {
    if (dimensions.length !== 2) throw new Error("A sankey chart needs a source and a target dimension.");
    if (dimensions[0] === dimensions[1]) throw new Error("Choose two different sankey dimensions.");
  }
  const dimension = dimensions[0];
  if (!dimension) throw new Error("Choose a chart dimension.");
  const filter = parsed.source.filter;
  const filterType = parsed.source.filterType;
  if (filter && !filterType) throw new Error("Chart filter is missing its schema-derived type.");
  if (filterType) assertGraphQlName(filterType, "chart filter type");
  const vars = filter ? "$filter: " + filterType : "";
  // The aggregate surface intentionally exposes only filter and groupBy.  Unlike
  // normal table queries it has no pagination arguments, so do not smuggle a
  // `limit` into the document.  The bounded category guard lives at the result
  // boundary below, before any chart renderer receives the rows.
  const args = [filter ? "filter: $filter" : "", `groupBy: [${dimensions.join(", ")}]`].filter(Boolean).join(", ");
  return {
    query: `query ChartAggregate${vars ? `(${vars})` : ""} { ${parsed.source.table}Aggregate(${args}) { ${dimensions.join(" ")} ${parsed.measures.map(selectionFor).join(" ")} } }`,
    variables: filter ? { filter } : {},
  };
}

export function measureKey(measure: ChartMeasure): string { return measure.op === "count" ? "count" : `${measure.op}:${measure.column}`; }

function measureValue(row: Record<string, unknown>, measure: ChartMeasure): number | null {
  const value = measure.op === "count" ? row._count : (row[`_${measure.op}`] as Record<string, unknown> | undefined)?.[measure.column!];
  return value == null ? null : Number(value);
}

const categoryLabel = (value: unknown) => value === null || value === undefined ? NULL_CATEGORY_LABEL : String(value);

/** Maps aggregate rows only; it deliberately has no page-row input or summation. */
export function mapAggregateRows(rows: readonly Record<string, unknown>[], definition: ChartDefinition): ChartPoint[] {
  const dimension = definition.dimensions[0];
  if (!dimension) return [];
  if (rows.length > MAX_CHART_CATEGORIES) throw new Error(`Too many categories (maximum ${MAX_CHART_CATEGORIES}). Refine the chart filter.`);
  return rows.map((row) => {
    const values: Record<string, number | null> = {};
    for (const measure of definition.measures) values[measureKey(measure)] = measureValue(row, measure);
    return { category: categoryLabel(row[dimension]), values };
  });
}

/**
 * Maps two-dimension aggregate rows into the recharts Sankey shape. The source
 * and target sides get SEPARATE nodes even when a category name appears on
 * both (searching Electronics and buying Electronics are two different nodes —
 * collapsing them would draw a cycle, which a sankey cannot lay out). Links
 * take the FIRST measure; a null or non-positive value is dropped rather than
 * rendered, since a zero-width band is invisible and a negative one is
 * meaningless flow.
 */
export function mapSankeyData(rows: readonly Record<string, unknown>[], definition: ChartDefinition): { nodes: SankeyNode[]; links: SankeyLink[] } {
  const [sourceDim, targetDim] = definition.dimensions;
  if (!sourceDim || !targetDim) return { nodes: [], links: [] };
  if (rows.length > MAX_CHART_CATEGORIES) throw new Error(`Too many flows (maximum ${MAX_CHART_CATEGORIES}). Refine the chart filter.`);
  const measure = definition.measures[0] ?? { op: "count" as const };
  const nodes: SankeyNode[] = [];
  const sourceIndex = new Map<string, number>();
  const targetIndex = new Map<string, number>();
  const nodeFor = (index: Map<string, number>, name: string) => {
    let i = index.get(name);
    if (i === undefined) { i = nodes.length; nodes.push({ name }); index.set(name, i); }
    return i;
  };
  const links: SankeyLink[] = [];
  for (const row of rows) {
    const value = measureValue(row, measure);
    if (value == null || value <= 0) continue;
    links.push({
      source: nodeFor(sourceIndex, categoryLabel(row[sourceDim])),
      target: nodeFor(targetIndex, categoryLabel(row[targetDim])),
      value,
    });
  }
  return { nodes, links };
}

/** One entry point per chart render: the type decides the mapped shape. */
export function mapChartData(rows: readonly Record<string, unknown>[], definition: ChartDefinition): ChartData {
  return definition.chartType === "sankey"
    ? { kind: "sankey", ...mapSankeyData(rows, definition) }
    : { kind: "cartesian", points: mapAggregateRows(rows, definition) };
}

/** One clicked chart element, expressed as a grid column filter. */
export interface ChartDrillFilter { column: string; operator: string; value: unknown; }

/**
 * The filter a clicked category means on the GRID: an ordinary equality, except
 * the explicit null node — "(null)" is a LABEL, not a value, so it drills to
 * the grid's `_null` operator rather than matching rows whose text is the label.
 */
export function drillFilter(column: string, category: string): ChartDrillFilter {
  return category === NULL_CATEGORY_LABEL
    ? { column, operator: "_null", value: true }
    : { column, operator: "_eq", value: category };
}
