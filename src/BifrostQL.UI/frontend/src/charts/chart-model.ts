export type ChartType = "bar" | "line" | "pie" | "area";
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
    !["bar", "line", "pie", "area"].includes(candidate.chartType ?? "")) return null;
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
  const dimension = parsed.dimensions[0];
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
  const args = [filter ? "filter: $filter" : "", `groupBy: [${dimension}]`].filter(Boolean).join(", ");
  return {
    query: `query ChartAggregate${vars ? `(${vars})` : ""} { ${parsed.source.table}Aggregate(${args}) { ${dimension} ${parsed.measures.map(selectionFor).join(" ")} } }`,
    variables: filter ? { filter } : {},
  };
}

export function measureKey(measure: ChartMeasure): string { return measure.op === "count" ? "count" : `${measure.op}:${measure.column}`; }

/** Maps aggregate rows only; it deliberately has no page-row input or summation. */
export function mapAggregateRows(rows: readonly Record<string, unknown>[], definition: ChartDefinition): ChartPoint[] {
  const dimension = definition.dimensions[0];
  if (!dimension) return [];
  if (rows.length > MAX_CHART_CATEGORIES) throw new Error(`Too many categories (maximum ${MAX_CHART_CATEGORIES}). Refine the chart filter.`);
  return rows.map((row) => {
    const values: Record<string, number | null> = {};
    for (const measure of definition.measures) {
      const value = measure.op === "count" ? row._count : (row[`_${measure.op}`] as Record<string, unknown> | undefined)?.[measure.column!];
      values[measureKey(measure)] = value == null ? null : Number(value);
    }
    return { category: row[dimension] === null || row[dimension] === undefined ? NULL_CATEGORY_LABEL : String(row[dimension]), values };
  });
}
