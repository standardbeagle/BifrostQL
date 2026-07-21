export type PivotAggregate = "count" | "sum" | "avg" | "min" | "max";

export interface PivotSource {
  kind: "table" | "saved-query";
  /** A schema-derived table GraphQL name. Saved queries retain their backing table. */
  table: string;
  savedQueryRef?: string;
  filter?: Record<string, unknown>;
  filterType?: string;
}

export interface PivotDefinition {
  kind: "bifrost.pivot";
  version: 1;
  source: PivotSource;
  rowKeys: string[];
  pivotColumn: string;
  valueColumn: string;
  aggregate: PivotAggregate;
}

export interface PivotPayload {
  pivotColumn: string;
  rowKeys: string[];
  columns: string[];
  rows: Array<Record<string, unknown> & { cells: Record<string, unknown> }>;
}

export const NULL_PIVOT_LABEL = "(null)";
const GRAPHQL_NAME = /^[_A-Za-z][_0-9A-Za-z]*$/;
const aggregateValues: readonly PivotAggregate[] = ["count", "sum", "avg", "min", "max"];

function assertName(value: unknown, label: string): asserts value is string {
  if (typeof value !== "string" || !GRAPHQL_NAME.test(value)) throw new Error(`Invalid GraphQL ${label}.`);
}

/** Saved objects are untrusted JSON; only schema-derived identifiers may reach a document. */
export function parsePivotDefinition(value: unknown): PivotDefinition | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const candidate = value as Partial<PivotDefinition>;
  if (candidate.kind !== "bifrost.pivot" || candidate.version !== 1 || !candidate.source ||
    !["table", "saved-query"].includes(candidate.source.kind ?? "") ||
    !Array.isArray(candidate.rowKeys) || !candidate.rowKeys.every((key) => typeof key === "string") ||
    typeof candidate.pivotColumn !== "string" || typeof candidate.valueColumn !== "string" ||
    !aggregateValues.includes(candidate.aggregate as PivotAggregate)) return null;
  try {
    assertName(candidate.source.table, "pivot table");
    candidate.rowKeys.forEach((key) => assertName(key, "pivot row key"));
    assertName(candidate.pivotColumn, "pivot column");
    assertName(candidate.valueColumn, "pivot value column");
    if (candidate.source.filterType) assertName(candidate.source.filterType, "pivot filter type");
  } catch { return null; }
  return candidate as PivotDefinition;
}

/** Builds a table pivot call only. The database, not this client, cross-tabs values. */
export function buildPivotQuery(value: PivotDefinition): { query: string; variables: Record<string, unknown> } {
  const definition = parsePivotDefinition(value);
  if (!definition || definition.rowKeys.length === 0 || !definition.pivotColumn || !definition.valueColumn)
    throw new Error("Choose at least one row, a pivot column, and a value column.");
  if (definition.rowKeys.includes(definition.pivotColumn)) throw new Error("A pivot column cannot also be a row field.");
  const { source } = definition;
  if (source.filter && !source.filterType) throw new Error("Pivot filter is missing its schema-derived type.");
  const args = [
    `rowKeys: [${definition.rowKeys.join(", ")}]`,
    `pivotColumn: ${definition.pivotColumn}`,
    `valueColumn: ${definition.valueColumn}`,
    `aggregate: ${definition.aggregate}`,
    source.filter ? "filter: $filter" : "",
  ].filter(Boolean).join(", ");
  return {
    query: `query Pivot${source.filter ? `($filter: ${source.filterType})` : ""} { ${source.table}Pivot(${args}) }`,
    variables: source.filter ? { filter: source.filter } : {},
  };
}

export function parsePivotPayload(value: unknown): PivotPayload {
  const candidate = value as Partial<PivotPayload> | null;
  if (!candidate || !Array.isArray(candidate.rowKeys) || !Array.isArray(candidate.columns) || !Array.isArray(candidate.rows))
    throw new Error("The server returned an invalid pivot result.");
  return {
    pivotColumn: typeof candidate.pivotColumn === "string" ? candidate.pivotColumn : "",
    rowKeys: candidate.rowKeys.filter((key): key is string => typeof key === "string"),
    columns: candidate.columns.map((column) => column === null ? NULL_PIVOT_LABEL : String(column)),
    rows: candidate.rows.filter((row): row is PivotPayload["rows"][number] => !!row && typeof row === "object" && !Array.isArray(row) && !!(row as { cells?: unknown }).cells && typeof (row as { cells: unknown }).cells === "object"),
  };
}

export function displayPivotColumn(value: string): string { return value === "" ? "(empty string)" : value === NULL_PIVOT_LABEL ? NULL_PIVOT_LABEL : value; }
