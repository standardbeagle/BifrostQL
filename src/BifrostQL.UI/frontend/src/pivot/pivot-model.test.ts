import { describe, expect, it } from "vitest";
import { buildSchema, parse, validate } from "graphql";
import { buildPivotQuery, NULL_PIVOT_LABEL, parsePivotPayload, type PivotDefinition } from "./pivot-model";
import { openPivot, savePivot } from "./pivot-store";

const sales: PivotDefinition = { kind: "bifrost.pivot", version: 1, source: { kind: "table", table: "sales", filterType: "salesFilter", filter: { region: { _eq: "north" } } }, rowKeys: ["region"], pivotColumn: "quarter", valueColumn: "amount", aggregate: "sum" };

describe("pivot model", () => {
  it("builds the server pivot GraphQL call from schema-derived fields, passing filters as variables", () => {
    const { query, variables } = buildPivotQuery(sales);
    expect(query).toContain("salesPivot(rowKeys: [region], pivotColumn: quarter, valueColumn: amount, aggregate: sum, filter: $filter)");
    expect(query).not.toContain("north");
    expect(variables).toEqual({ filter: sales.source.filter });
    const schema = buildSchema(`type Query { salesPivot(rowKeys: [salesColumn!]!, pivotColumn: salesColumn!, valueColumn: salesColumn!, aggregate: PivotAggregate!, filter: salesFilter): JSON! } scalar JSON enum salesColumn { region quarter amount } enum PivotAggregate { count sum avg min max } input salesFilter { region: StringFilter } input StringFilter { _eq: String }`);
    expect(validate(schema, parse(query)).map((error) => error.message)).toEqual([]);
  });

  it("rejects user text in GraphQL identifiers", () => {
    expect(() => buildPivotQuery({ ...sales, pivotColumn: "quarter) { evil" })).toThrow("Choose at least one row");
  });

  it("preserves the full server cross-tab matrix without grouping rows client-side", () => {
    // Equivalent hand-written SQL: SELECT region, SUM(CASE WHEN quarter='Q1' THEN amount END) Q1,
    // SUM(CASE WHEN quarter='Q2' THEN amount END) Q2 FROM sales GROUP BY region.
    const payload = parsePivotPayload({ pivotColumn: "quarter", rowKeys: ["region"], columns: ["Q1", "Q2"], rows: [{ region: "north", cells: { Q1: 12, Q2: 7 } }, { region: "south", cells: { Q1: 3, Q2: 9 } }] });
    expect(payload.rows.map((row) => payload.columns.map((column) => row.cells[column]))).toEqual([[12, 7], [3, 9]]);
  });

  it("keeps NULL pivot categories distinct from empty strings", () => {
    const payload = parsePivotPayload({ pivotColumn: "quarter", rowKeys: ["region"], columns: [NULL_PIVOT_LABEL, ""], rows: [{ region: "north", cells: { [NULL_PIVOT_LABEL]: 1, "": 2 } }] });
    expect(payload.columns).toEqual([NULL_PIVOT_LABEL, ""]);
    expect(payload.rows[0].cells).toEqual({ [NULL_PIVOT_LABEL]: 1, "": 2 });
  });

  it("round-trips saved pivots and supports equivalent saved-query sources", async () => {
    const savedQuery = { ...sales, source: { kind: "saved-query" as const, table: "sales", savedQueryRef: "sales-north", filterType: "salesFilter", filter: { region: { _eq: "north" } } } };
    expect(buildPivotQuery(savedQuery)).toEqual(buildPivotQuery(sales));
    const stored = await savePivot({ put: async (object: any) => ({ ...object, version: 1 }) } as any, { id: "pivot-1", name: "Sales", definition: savedQuery, version: 0 });
    expect(openPivot(stored)).toEqual(savedQuery);
  });
});
