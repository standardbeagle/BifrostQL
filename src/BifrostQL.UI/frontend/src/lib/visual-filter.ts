import type { VisualFilter } from "./visual-query";

/** The bare table name of a possibly schema-qualified designer reference. */
export function tablePart(qualified: string): string {
  const parts = qualified.split(".");
  return parts[parts.length - 1] ?? qualified;
}

/**
 * Converts a one-table saved designer filter into the public table-filter
 * input shape. All identifiers were validated by the saved-query parser; this
 * only moves the user VALUES into GraphQL variables, never into document text.
 * Shared by the pivot, dashboard, and query-builder → chart bridges so the
 * three surfaces cannot drift on what a designer filter means.
 */
export function resolveVisualFilter(filter: VisualFilter | null, tableRef: string): Record<string, unknown> | undefined {
  if (!filter) return undefined;
  if (filter.op === "leaf") {
    const criterion = filter.criterion;
    if (!criterion || criterion.table !== tableRef) throw new Error("The saved query filter is not scoped to its backing table.");
    return { [criterion.column]: { [criterion.operator]: criterion.value } };
  }
  const children = (filter.children ?? []).map((child) => resolveVisualFilter(child, tableRef)).filter((child): child is Record<string, unknown> => !!child);
  return children.length ? { [filter.op]: children } : undefined;
}

/** The schema-generated name of a table's filter input type — the ONE naming
 * convention (TableFilter<table>Input, see TableSchemaGenerator). The grid's
 * Visualize bridge shipped broken because it guessed a second spelling. */
export function tableFilterTypeName(table: string): string {
  return `TableFilter${table}Input`;
}
