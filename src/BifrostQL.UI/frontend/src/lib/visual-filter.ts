import type { VisualFilter } from "./visual-query";

/**
 * A table's database identity paired with its GraphQL names. Both name sources
 * carry it: the `_dbSchema` field (`schema dbName graphQlName columns { dbName
 * graphQlName }`) and the builder schema (`namedTablesFromBuilder`).
 */
export interface NamedTable {
  schema: string;
  dbName: string;
  graphQlName: string;
  columns: ReadonlyArray<{ dbName: string; graphQlName: string }>;
}

/** The GraphQL names a designer table reference maps onto. */
export interface DesignerTableNames {
  graphQlName: string;
  /** GraphQL field name for a DB column of this table; throws for an unknown column. */
  column(dbName: string): string;
}

/** The designer's "schema.name" form of a table (bare name when it has no schema). */
export function qualifiedDbName(table: { schema: string; dbName: string }): string {
  return table.schema ? `${table.schema}.${table.dbName}` : table.dbName;
}

/**
 * Maps a designer table reference (the qualified DB name the builder stores)
 * onto its GraphQL names. Designer state holds DATABASE names; GraphQL text and
 * filter keys need GRAPHQL names, and the two differ whenever a name is
 * sanitized (`order items` → `order_items`), prefixed, or deduped across
 * schemas (`dbo.orders` / `sales.orders`). A qualified name resolves exactly;
 * a bare name only when unique. Anything else is an error, never a guess.
 */
export function resolveDesignerTable(qualified: string, tables: readonly NamedTable[]): DesignerTableNames {
  const wanted = qualified.toLowerCase();
  let table = tables.find((candidate) => qualifiedDbName(candidate).toLowerCase() === wanted);
  if (!table && !qualified.includes(".")) {
    const bare = tables.filter((candidate) => candidate.dbName.toLowerCase() === wanted);
    if (bare.length === 1) table = bare[0];
  }
  if (!table) throw new Error(`The saved query's table "${qualified}" is not present in this schema.`);
  const found = table;
  return {
    graphQlName: found.graphQlName,
    column(dbName: string) {
      const column = found.columns.find((candidate) => candidate.dbName.toLowerCase() === dbName.toLowerCase());
      if (!column) throw new Error(`Column "${dbName}" is not present on table "${qualified}".`);
      return column.graphQlName;
    },
  };
}

/**
 * Converts a one-table saved designer filter into the public table-filter
 * input shape. All identifiers were validated by the saved-query parser; this
 * only moves the user VALUES into GraphQL variables, never into document text.
 * Filter keys are the GraphQL field names `columnName` maps each DB column to.
 * Shared by the pivot, dashboard, and query-builder → chart bridges so the
 * three surfaces cannot drift on what a designer filter means.
 */
export function resolveVisualFilter(
  filter: VisualFilter | null,
  tableRef: string,
  columnName: (dbName: string) => string,
): Record<string, unknown> | undefined {
  if (!filter) return undefined;
  if (filter.op === "leaf") {
    const criterion = filter.criterion;
    if (!criterion || criterion.table !== tableRef) throw new Error("The saved query filter is not scoped to its backing table.");
    return { [columnName(criterion.column)]: { [criterion.operator]: criterion.value } };
  }
  const children = (filter.children ?? []).map((child) => resolveVisualFilter(child, tableRef, columnName)).filter((child): child is Record<string, unknown> => !!child);
  return children.length ? { [filter.op]: children } : undefined;
}

/** A one-table designer source expressed in GraphQL names, ready for a query. */
export interface DesignerSource {
  table: string;
  filter: Record<string, unknown> | undefined;
  filterType: string | undefined;
  names: DesignerTableNames;
}

/**
 * The single bridge from a placed designer table (+ its filter) to GraphQL:
 * the table, the filter keyed by GraphQL field names, and the filter type.
 * Used by Visualize, the pivot's saved-query source and dashboard tiles.
 */
export function resolveDesignerSource(
  placed: { table: string; alias: string | null },
  filter: VisualFilter | null,
  tables: readonly NamedTable[],
): DesignerSource {
  const names = resolveDesignerTable(placed.table, tables);
  const resolved = resolveVisualFilter(filter, placed.alias ?? placed.table, names.column);
  return { table: names.graphQlName, filter: resolved, filterType: resolved ? tableFilterTypeName(names.graphQlName) : undefined, names };
}

/** The schema-generated name of a table's filter input type — the ONE naming
 * convention (TableFilter<table>Input, see TableSchemaGenerator). The grid's
 * Visualize bridge shipped broken because it guessed a second spelling. */
export function tableFilterTypeName(table: string): string {
  return `TableFilter${table}Input`;
}
